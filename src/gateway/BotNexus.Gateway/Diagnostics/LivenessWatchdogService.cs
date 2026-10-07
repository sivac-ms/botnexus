using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Configuration for the liveness watchdog service.
/// </summary>
public sealed class LivenessWatchdogOptions
{
    /// <summary>
    /// How often the watchdog checks for inactivity. Default: 30 seconds.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Duration of inactivity after which a warning is logged. Default: 15 minutes.
    /// </summary>
    public TimeSpan WarningThreshold { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Duration of inactivity after which scheduler responsiveness is verified. Default: 30 minutes.
    /// </summary>
    public TimeSpan CriticalThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the scheduler probe may take before the gateway is declared unresponsive.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan CriticalProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Verifies that work queued to the runtime scheduler can execute within a bounded interval.
/// </summary>
public interface IThreadPoolProbe
{
    /// <summary>
    /// Queues work and reports whether it ran before <paramref name="timeout"/> elapsed.
    /// Cancellation represents host shutdown and must not be interpreted as scheduler failure.
    /// </summary>
    Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Probes the runtime scheduler by queuing a no-op to the managed thread pool.
/// </summary>
public sealed class ThreadPoolProbe : IThreadPoolProbe
{
    /// <inheritdoc />
    public async Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ThreadPool.QueueUserWorkItem(static state => ((TaskCompletionSource)state!).TrySetResult(), completion))
        {
            return false;
        }

        try
        {
            await completion.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// Monitors gateway inactivity and verifies scheduler responsiveness before emitting a fatal alert.
/// Quiet gateways remain actionable without conflating ordinary idle time with scheduler failure.
/// </summary>
/// <remarks>
/// #4689: a responsive thread pool proves only that the process can run a callback, not that the
/// scheduler dispatches work. CRITICAL escalation of a "responsive" gateway is therefore driven by
/// direct dispatch evidence (<see cref="IDispatchFreshnessProbe"/>: a due job that was not fired),
/// never by idle time alone, so a host with nothing due stays at WARNING however long it is quiet.
/// Re-alarms follow a doubling ladder so a sustained episode stays visible without spamming.
/// </remarks>
public sealed class LivenessWatchdogService : BackgroundService
{
    private readonly IActivityTracker _activityTracker;
    private readonly IThreadPoolProbe _threadPoolProbe;
    private readonly IDispatchFreshnessProbe? _dispatchProbe;
    private readonly LivenessWatchdogOptions _options;
    private readonly ILogger<LivenessWatchdogService> _logger;
    private bool _warningEmitted;
    private bool _criticalEpisodeEvaluated;

    /// <summary>Inactivity at the last critical evaluation; the next one needs the gap to double.</summary>
    private TimeSpan _lastEvaluatedElapsed = TimeSpan.Zero;

    /// <summary>
    /// Creates the watchdog with the scheduler probe used to corroborate critical inactivity and the
    /// optional dispatch-freshness probe that supplies real dispatch evidence (#4689).
    /// </summary>
    public LivenessWatchdogService(
        IActivityTracker activityTracker,
        IThreadPoolProbe threadPoolProbe,
        IOptions<LivenessWatchdogOptions> options,
        ILogger<LivenessWatchdogService> logger,
        IDispatchFreshnessProbe? dispatchProbe = null)
    {
        _activityTracker = activityTracker;
        _threadPoolProbe = threadPoolProbe;
        _dispatchProbe = dispatchProbe;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckLivenessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Liveness watchdog check failed unexpectedly.");
            }

            await Task.Delay(_options.CheckInterval, stoppingToken);
        }
    }

    internal async Task CheckLivenessAsync(CancellationToken cancellationToken)
    {
        var elapsed = _activityTracker.TimeSinceLastActivity;

        if (elapsed >= _options.CriticalThreshold)
        {
            // #4689: do NOT latch an episode forever. Re-evaluate each time the gap doubles so an
            // outage cannot stay silent for hours, while a steady gap logs once per rung.
            if (_criticalEpisodeEvaluated && elapsed < NextReAlarmThreshold())
            {
                return;
            }

            var responsive = await _threadPoolProbe.IsResponsiveAsync(
                _options.CriticalProbeTimeout,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _criticalEpisodeEvaluated = true;
            _warningEmitted = true;
            _lastEvaluatedElapsed = elapsed;

            if (!responsive)
            {
                _logger.LogCritical(
                    "Gateway liveness CRITICAL: scheduler probe timed out after {CriticalProbeTimeout} " +
                    "with {Elapsed} of inactivity. Last activity at {LastActivity}. " +
                    "Deadlock or thread pool exhaustion may have prevented scheduling.",
                    _options.CriticalProbeTimeout,
                    elapsed,
                    _activityTracker.LastActivityUtc);
                return;
            }

            var dispatch = await CheckDispatchAsync(cancellationToken);
            if (dispatch.StoreUnreadable)
            {
                _logger.LogCritical(
                    "Gateway liveness CRITICAL: no activity for {Elapsed} and the cron store could not be read, " +
                    "so due-job dispatch cannot be verified. The thread pool is responsive; treating this as a " +
                    "possible stall rather than healthy (#4732). Last activity at {LastActivity}.",
                    elapsed,
                    _activityTracker.LastActivityUtc);
                return;
            }

            if (dispatch.IsStalled)
            {
                _logger.LogCritical(
                    "Gateway liveness CRITICAL: no activity for {Elapsed} and {OverdueCount} due cron job(s) " +
                    "have not been dispatched (oldest overdue by {OldestOverdue}; jobs: {OverdueJobs}). The " +
                    "thread pool is responsive, so suspect a stalled scheduler or a wedged run. " +
                    "Last activity at {LastActivity}.",
                    elapsed,
                    dispatch.OverdueJobIds.Count,
                    dispatch.OldestOverdue,
                    string.Join(", ", dispatch.OverdueJobIds),
                    _activityTracker.LastActivityUtc);
                return;
            }

            _logger.LogWarning(
                "Gateway liveness WARNING: no activity for {Elapsed}, but scheduler probe succeeded " +
                "within {CriticalProbeTimeout} and no due cron job is overdue. Thread pool is responsive; " +
                "this does NOT prove non-cron work is flowing. Last activity at {LastActivity}.",
                elapsed,
                _options.CriticalProbeTimeout,
                _activityTracker.LastActivityUtc);
            return;
        }

        if (elapsed >= _options.WarningThreshold)
        {
            if (!_warningEmitted)
            {
                _logger.LogWarning(
                    "Gateway liveness WARNING: no activity for {Elapsed}. Last activity at {LastActivity}.",
                    elapsed,
                    _activityTracker.LastActivityUtc);
                _warningEmitted = true;
            }

            return;
        }

        if (_warningEmitted || _criticalEpisodeEvaluated)
        {
            _logger.LogInformation(
                "Gateway liveness recovered. Activity resumed after {Elapsed} of inactivity.",
                elapsed);
        }

        _warningEmitted = false;
        _criticalEpisodeEvaluated = false;
        _lastEvaluatedElapsed = TimeSpan.Zero;
    }

    private async Task<DispatchFreshnessResult> CheckDispatchAsync(CancellationToken cancellationToken)
    {
        if (_dispatchProbe is null)
        {
            return DispatchFreshnessResult.Healthy;
        }

        try
        {
            return await _dispatchProbe.CheckAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // #4732: fail CLOSED. An unreadable store hides a stall just like #4689, so it is not health.
            _logger.LogWarning(ex, "Liveness watchdog could not read cron dispatch freshness; treating it as NOT fresh.");
            return DispatchFreshnessResult.Unreadable;
        }
    }

    /// <summary>
    /// Inactivity at which an evaluated episode is re-evaluated: each re-alarm requires the gap to
    /// have doubled, so a long outage produces a bounded (logarithmic) number of lines.
    /// </summary>
    private TimeSpan NextReAlarmThreshold()
        => _lastEvaluatedElapsed <= TimeSpan.Zero
            ? _options.CriticalThreshold
            : TimeSpan.FromTicks(_lastEvaluatedElapsed.Ticks * 2);
}
