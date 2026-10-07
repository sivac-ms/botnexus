using BotNexus.Cron;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Result of a dispatch-freshness check: the jobs that were due but have not been dispatched
/// within the grace window, and how overdue the oldest one is.
/// </summary>
public sealed record DispatchFreshnessResult(
    TimeSpan OldestOverdue,
    IReadOnlyList<string> OverdueJobIds,
    bool StoreUnreadable = false)
{
    /// <summary>No job is overdue: the scheduler is either dispatching or has nothing due.</summary>
    public static DispatchFreshnessResult Healthy { get; } = new(TimeSpan.Zero, []);

    /// <summary>
    /// The cron store could not be read, so freshness is UNKNOWN. This fails closed (#4732): an
    /// unreadable store hides a stall exactly like #4689, so it is not evidence of health.
    /// </summary>
    public static DispatchFreshnessResult Unreadable { get; } = new(TimeSpan.Zero, [], StoreUnreadable: true);

    /// <summary>
    /// True when at least one due job has not been dispatched within the grace window, or when
    /// dispatch could not be verified because the store was unreadable.
    /// </summary>
    public bool IsStalled => StoreUnreadable || OverdueJobIds.Count > 0;
}

/// <summary>
/// Reports whether scheduled work that is DUE is actually being dispatched (#4689). Unlike the
/// thread-pool probe this is direct evidence: a job whose effective next-run time is well in the
/// past was not fired, regardless of how responsive the process is.
/// </summary>
public interface IDispatchFreshnessProbe
{
    /// <summary>Evaluates dispatch freshness at the current instant.</summary>
    Task<DispatchFreshnessResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Dispatch-freshness probe over the cron store. The scheduler advances a job's
/// <c>NextRunAt</c> only after the fired run returns, so a wedged tick loop OR a hung run leaves
/// <c>max(NextRunAt, BackoffUntil)</c> in the past. Jobs with nothing due (future next run, disabled,
/// expired, invalid schedule, or the scheduler switched off) are never reported, so a legitimately
/// idle host stays quiet.
/// </summary>
public sealed class CronDispatchFreshnessProbe : IDispatchFreshnessProbe
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly ILogger _logger;

    /// <summary>Creates the probe; the cron store is resolved lazily so hosts without cron work.</summary>
    public CronDispatchFreshnessProbe(
        IServiceProvider services,
        TimeProvider? timeProvider = null,
        ILogger<CronDispatchFreshnessProbe>? logger = null)
    {
        _services = services;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _startedAtUtc = _timeProvider.GetUtcNow();
    }

    /// <inheritdoc />
    public async Task<DispatchFreshnessResult> CheckAsync(CancellationToken cancellationToken)
    {
        var store = _services.GetService<ICronStore>();
        if (store is null)
        {
            return DispatchFreshnessResult.Healthy;
        }

        var options = _services.GetService<IOptionsMonitor<CronOptions>>()?.CurrentValue ?? new CronOptions();
        if (!options.Enabled)
        {
            // Scheduler deliberately off: nothing is expected to dispatch.
            return DispatchFreshnessResult.Healthy;
        }

        var now = _timeProvider.GetUtcNow();
        var grace = GetGrace(options);
        IReadOnlyList<CronJob> jobs;
        try
        {
            jobs = await store.ListAsync(ct: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // #4732: fail CLOSED. An unreadable store cannot prove due work is dispatching.
            _logger.LogWarning(
                ex,
                "Cron dispatch freshness UNKNOWN: the cron store could not be read; treating dispatch as NOT fresh.");
            return DispatchFreshnessResult.Unreadable;
        }

        var oldest = TimeSpan.Zero;
        var overdue = new List<string>();
        foreach (var job in jobs)
        {
            if (!job.Enabled || job.NextRunAt is not { } nextRun)
                continue;
            if (job.ExpiresAt is { } expiresAt && expiresAt <= now)
                continue;
            if (!IsValidSchedule(job.Schedule))
                continue;

            var due = job.BackoffUntil is { } floor && floor > nextRun ? floor : nextRun;
            // A job that fell due before this process started (gateway downtime) is measured from
            // startup, not from its stale due time: the first tick has not had a chance to run yet.
            if (due < _startedAtUtc)
                due = _startedAtUtc;

            var lateBy = now - due;
            if (lateBy < grace)
                continue;

            overdue.Add(job.Id.Value);
            if (lateBy > oldest)
                oldest = lateBy;
        }

        return overdue.Count == 0 ? DispatchFreshnessResult.Healthy : new DispatchFreshnessResult(oldest, overdue);
    }

    /// <summary>
    /// A tick awaits every due run it fans out, so a legitimately long run (bounded by the job
    /// timeout) delays the next-run bookkeeping of the jobs it ran with. The grace therefore covers
    /// the default job timeout plus two ticks, and is never shorter than 30 minutes.
    /// </summary>
    internal static TimeSpan GetGrace(CronOptions options)
    {
        var timeout = TimeSpan.FromSeconds(options.DefaultJobTimeoutSeconds > 0 ? options.DefaultJobTimeoutSeconds : 3600);
        var ticks = TimeSpan.FromSeconds(2 * Math.Max(1, options.TickIntervalSeconds));
        var grace = timeout + ticks;
        return grace < TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : grace;
    }

    private static bool IsValidSchedule(string schedule)
    {
        try
        {
            CronExpression.Parse(schedule, CronFormat.Standard);
            return true;
        }
        catch
        {
            // The scheduler skips unparseable schedules too, so they are not dispatch evidence.
            return false;
        }
    }
}
