using System.Reflection;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace BotNexus.Cron.Tests;
/// <summary>
/// Bounded quarantine for timed-out uncooperative actions. Before this fix, a hung action kept its
/// job quarantined until it exited, with no time limit, and no alert went out. Every later fire was
/// a silent `skipped` ("still exiting") row. On 2026-10-09, icm-feed-snapshot was silenced for
/// about 6.6h and only a gateway restart cleared it.
/// </summary>
/// <remarks>
/// The grace wait runs on the scheduler's TimeProvider, but the job timeout (CancelAfter) and the
/// 100ms cooperative-exit window use real time. ManualTimeProvider does not fire timers, so these
/// tests use a real, small grace (1s) with generous polling deadlines.
/// </remarks>
public sealed class CronQuarantineGraceTests
{
    private const string AlertConversationId = "conv-alerts";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    [Fact]
    public async Task Quarantine_ForceReleasedAfterGrace_NextFireRuns()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var stuck = new GatedUncooperativeAction("stuck-action");
        await SeedJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1);
        var scheduler = CreateScheduler(store.Store, [stuck], Options(graceSeconds: 1));
        var first = await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline);
        first.Status.ShouldBe(CronRunStatus.TimedOut);
        scheduler.IsQuarantined(JobId.From("stuck")).ShouldBeTrue("the abandoned action still owns the job inside the grace");
        await TestAwait.EventuallyAsync(() => !scheduler.IsQuarantined(JobId.From("stuck")), "the quarantine must be force-released after the grace", Deadline);
        var next = await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline);
        next.Status.ShouldNotBe(CronRunStatus.Skipped, "after a force-release later fires must run again");
        stuck.ExecutionCount.ShouldBe(2);
        stuck.ReleaseAll();
    }
    [Fact]
    public async Task ForceRelease_EmitsFailureAlert_WhenAlertsEnabled()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        var stuck = new GatedUncooperativeAction("stuck-action");
        await SeedJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1, alerting: true);
        var scheduler = CreateScheduler(store.Store, [stuck], Options(graceSeconds: 1), sink);
        (await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.TimedOut);
        await TestAwait.EventuallyAsync(() => sink.Alerts.Count >= 2, "a force-release must alert in addition to the timeout alert", Deadline);
        var forced = sink.Alerts[1];
        forced.ConversationId.Value.ShouldBe(AlertConversationId);
        forced.Alert.JobId.ShouldBe(JobId.From("stuck"));
        forced.Alert.Error.ShouldNotBeNull();
        forced.Alert.Error.ShouldContain("force-released");
        stuck.ReleaseAll();
    }
    [Fact]
    public async Task TimedOut_EmitsFailureAlert()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        var slow = new CooperativeSlowAction("slow-action");
        await SeedJobAsync(store, "slow", slow.ActionType, timeoutSeconds: 1, alerting: true);
        var scheduler = CreateScheduler(store.Store, [slow], Options(graceSeconds: 300), sink);
        var run = await scheduler.RunNowAsync(JobId.From("slow")).WaitAsync(Deadline);
        run.Status.ShouldBe(CronRunStatus.TimedOut);
        var alert = sink.Alerts.ShouldHaveSingleItem("a timeout is a failure and must alert like one");
        alert.Alert.JobId.ShouldBe(JobId.From("slow"));
        alert.Alert.ConsecutiveErrorCount.ShouldBe(1);
        scheduler.IsQuarantined(JobId.From("slow")).ShouldBeFalse("a cooperative timeout is not abandoned");
    }
    [Fact]
    public async Task QuarantineSkips_CountTowardFailureStreak()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        var stuck = new GatedUncooperativeAction("stuck-action");
        await SeedJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1, alerting: true);
        var scheduler = CreateScheduler(store.Store, [stuck], Options(graceSeconds: 300), sink);
        (await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.TimedOut);
        (await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.Skipped);
        (await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.Skipped);
        var streak = await CountConsecutiveErrorsAsync(scheduler, JobId.From("stuck"));
        streak.ShouldBe(3, "timed_out plus every fire lost to quarantine extend the streak instead of ending it");
        sink.Alerts.Count.ShouldBe(1, "only the timeout alerted; skips use the streak backoff, not per-fire alerts");
        stuck.ExecutionCount.ShouldBe(1);
        stuck.ReleaseAll();
    }
    [Fact]
    public async Task ActionExitingWithinGrace_ClearsQuarantine_NoForceAlert()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var sink = new RecordingAlertSink();
        var stuck = new GatedUncooperativeAction("stuck-action");
        await SeedJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1, alerting: true);
        var scheduler = CreateScheduler(store.Store, [stuck], Options(graceSeconds: 60), sink);
        (await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.TimedOut);
        scheduler.IsQuarantined(JobId.From("stuck")).ShouldBeTrue();
        stuck.ReleaseAll();
        await TestAwait.EventuallyAsync(() => !scheduler.IsQuarantined(JobId.From("stuck")), "a real exit inside the grace clears the quarantine", Deadline);
        sink.Alerts.Count.ShouldBe(1, "only the timeout alert; an action that exits within the grace is not force-released");
        sink.Alerts.ShouldNotContain(a => a.Alert.Error != null && a.Alert.Error.Contains("force-released"));
    }
    [Fact]
    public async Task ForceRelease_DoesNotDisposeScopeUntilActionExits()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var stuck = new GatedUncooperativeAction("stuck-action", resolveProbe: true);
        await SeedJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1);
        var scheduler = CreateScheduler(store.Store, [stuck], Options(graceSeconds: 1));
        (await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.TimedOut);
        await TestAwait.EventuallyAsync(() => !scheduler.IsQuarantined(JobId.From("stuck")), "the quarantine must be force-released after the grace", Deadline);
        var probe = stuck.Probes.ShouldHaveSingleItem();
        probe.IsDisposed.ShouldBeFalse("#4287: the still-running action can use its scope after the force-release");
        stuck.ReleaseAll();
        await probe.Disposed.Task.WaitAsync(Deadline);
    }
    [Fact]
    public async Task LateExit_DoesNotClearNewerQuarantine()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var stuck = new GatedUncooperativeAction("stuck-action", resolveProbe: true);
        await SeedJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 1);
        var options = Options(graceSeconds: 1);
        var logger = new AbandonedExitLogger();
        var scheduler = CreateScheduler(store.Store, [stuck], options, logger: logger);
        // Run A times out and is force-released after a 1s grace.
        (await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.TimedOut);
        await TestAwait.EventuallyAsync(() => !scheduler.IsQuarantined(JobId.From("stuck")), "run A must be force-released", Deadline);
        // Run B times out and owns a new quarantine. Widen the grace so B is not force-released mid-test.
        options.TimedOutActionGraceSeconds = 120;
        (await scheduler.RunNowAsync(JobId.From("stuck")).WaitAsync(Deadline)).Status.ShouldBe(CronRunStatus.TimedOut);
        scheduler.IsQuarantined(JobId.From("stuck")).ShouldBeTrue();
        stuck.Probes.Count.ShouldBe(2);
        // A finally exits. Its cleanup must not clear B's quarantine.
        stuck.Release(0);
        await stuck.Probes[0].Disposed.Task.WaitAsync(Deadline);
        // A's cleanup has fully run (its generation-checked quarantine release included) once the
        // scheduler logs the abandoned exit, so the check below observes the settled state.
        await logger.FirstAbandonedExit.Task.WaitAsync(Deadline);
        scheduler.IsQuarantined(JobId.From("stuck")).ShouldBeTrue("a late exit of run A must not clear run B's quarantine");
        stuck.Release(1);
        await TestAwait.EventuallyAsync(() => !scheduler.IsQuarantined(JobId.From("stuck")), "B's own exit clears its quarantine", Deadline);
    }
    [Fact]
    public async Task OperatorCancel_OfUncooperativeAction_IsBounded()
    {
        await using var store = await CronStoreTestContext.CreateAsync();
        var stuck = new GatedUncooperativeAction("stuck-action");
        await SeedJobAsync(store, "stuck", stuck.ActionType, timeoutSeconds: 600);
        var options = Options(graceSeconds: 1);
        options.ActiveRunCancellationGraceSeconds = 1;
        var scheduler = CreateScheduler(store.Store, [stuck], options);
        var runTask = scheduler.RunNowAsync(JobId.From("stuck"));
        await stuck.Started.Task.WaitAsync(Deadline);
        await scheduler.CancelActiveRunAsync(JobId.From("stuck")).WaitAsync(Deadline);
        // Before the fix this awaited the action with no time limit: the run stayed `running` until
        // the action exited (run 62eed194 on 2026-10-09).
        var run = await runTask.WaitAsync(Deadline);
        run.Status.ShouldBe(CronRunStatus.Aborted);
        var persisted = (await store.Store.GetRunHistoryAsync(JobId.From("stuck"))).ShouldHaveSingleItem();
        persisted.Status.ShouldBe(CronRunStatus.Aborted);
        persisted.CompletedAt.ShouldNotBeNull();
        stuck.ReleaseAll();
    }
    // Helpers
    private static CronOptions Options(int graceSeconds) => new()
    {
        Enabled = true,
        TickIntervalSeconds = 1,
        DefaultJobTimeoutSeconds = 30,
        MaxConcurrentJobs = 1,
        TimedOutActionGraceSeconds = graceSeconds
    };
    private static async Task SeedJobAsync(
        CronStoreTestContext store,
        string id,
        string actionType,
        int timeoutSeconds,
        bool alerting = false)
    {
        var job = CronStoreTestContext.CreateJob(id, actionType: actionType) with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = timeoutSeconds }
        };
        if (alerting)
        {
            job = job with
            {
                FailureAlertsEnabled = true,
                FailureAlertConversationId = ConversationId.From(AlertConversationId)
            };
        }
        await store.Store.CreateAsync(job);
    }
    private static CronScheduler CreateScheduler(
        ICronStore store,
        IReadOnlyList<ICronAction> actions,
        CronOptions options,
        ICronFailureAlertSink? sink = null,
        ILogger<CronScheduler>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<DisposalProbe>();
        services.AddSingleton<ISecretRedactor>(new PassThroughRedactor());
        if (sink is not null)
            services.AddSingleton(sink);
        return new CronScheduler(
            store,
            actions,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new MutableOptionsMonitor<CronOptions>(options),
            logger ?? NullLogger<CronScheduler>.Instance);
    }
    private static async Task<int> CountConsecutiveErrorsAsync(CronScheduler scheduler, JobId jobId)
    {
        var method = typeof(CronScheduler).GetMethod("CountConsecutiveErrorsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull();
        var task = method.Invoke(scheduler, [jobId, CancellationToken.None]) as Task<int>;
        Assert.NotNull(task);
        return await task;
    }
    /// <summary>
    /// Signals when the scheduler logs that an abandoned action has exited. The scheduler logs that
    /// line after its cleanup finally block, so the run's quarantine release has already happened.
    /// </summary>
    private sealed class AbandonedExitLogger : ILogger<CronScheduler>
    {
        public TaskCompletionSource FirstAbandonedExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Abandoned cron action exited;", StringComparison.Ordinal))
                FirstAbandonedExit.TrySetResult();
        }
    }
    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private sealed record CapturedAlert(ConversationId ConversationId, CronFailureAlert Alert);
    private sealed class RecordingAlertSink : ICronFailureAlertSink
    {
        private readonly List<CapturedAlert> _alerts = [];
        public IReadOnlyList<CapturedAlert> Alerts
        {
            get { lock (_alerts) { return _alerts.ToList(); } }
        }
        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
        {
            lock (_alerts) { _alerts.Add(new CapturedAlert(conversationId, alert)); }
            return Task.CompletedTask;
        }
    }
    private sealed class PassThroughRedactor : ISecretRedactor
    {
        public string Redact(string input) => input;
        public string RedactForExternalDelivery(string input) => input;
    }
    /// <summary>Ignores cancellation. Each invocation has its own release gate.</summary>
    private sealed class GatedUncooperativeAction(string actionType, bool resolveProbe = false) : ICronAction
    {
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<DisposalProbe> _probes = [];
        private int _executionCount;
        public string ActionType => actionType;
        public int ExecutionCount => Volatile.Read(ref _executionCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<DisposalProbe> Probes
        {
            get { lock (_gates) { return _probes.ToList(); } }
        }
        public void Release(int invocation)
        {
            lock (_gates) { _gates[invocation].TrySetResult(); }
        }
        public void ReleaseAll()
        {
            lock (_gates) { foreach (var gate in _gates) gate.TrySetResult(); }
        }
        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gates)
            {
                _gates.Add(gate);
                if (resolveProbe)
                    _probes.Add(context.Services.GetRequiredService<DisposalProbe>());
            }
            Interlocked.Increment(ref _executionCount);
            Started.TrySetResult();
            await gate.Task.ConfigureAwait(false);
        }
    }
    /// <summary>Honours cancellation: a timeout ends it promptly, so it is never abandoned.</summary>
    private sealed class CooperativeSlowAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;
        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
    private sealed class DisposalProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()
        {
            IsDisposed = true;
            Disposed.TrySetResult();
        }
    }
}
