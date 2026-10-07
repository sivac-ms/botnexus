using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// #4732 review: the real <see cref="CronDispatchFreshnessProbe"/> must fail CLOSED - an unreadable
/// cron store hides exactly the kind of stall #4689 was about, so it is not evidence of health.
/// </summary>
public sealed class CronDispatchFreshnessProbeTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CronDispatchFreshness_StoreThrows_ReportsNotFresh()
    {
        var store = Substitute.For<ICronStore>();
        store.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CronJob>>>(_ => throw new InvalidOperationException("database is locked"));
        var probe = CreateProbe(store, new ManualTimeProvider(Start));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        Assert.True(result.StoreUnreadable);
    }

    [Fact]
    public async Task CronDispatchFreshness_WatchdogEscalatesWhenProbeThrows()
    {
        var logger = new RecordingLogger<LivenessWatchdogService>();
        var service = new LivenessWatchdogService(
            new StubActivityTracker(TimeSpan.FromMinutes(31)),
            new StubThreadPoolProbe(),
            Options.Create(new LivenessWatchdogOptions()),
            logger,
            new ThrowingDispatchProbe());

        await service.CheckLivenessAsync(CancellationToken.None);

        var critical = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("could not be read", critical.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly TimeSpan Grace = CronDispatchFreshnessProbe.GetGrace(new CronOptions());

    [Fact]
    public async Task CronDispatchFreshness_FreshDueJob_IsHealthy()
    {
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("due-now", nextRun: Start)), time);
        time.Now = Start + Grace - TimeSpan.FromMinutes(1);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
        Assert.False(result.StoreUnreadable);
    }

    [Fact]
    public async Task CronDispatchFreshness_OverdueEnabledJobPastGrace_IsStale()
    {
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("stuck", nextRun: Start + TimeSpan.FromMinutes(5))), time);
        time.Now = Start + TimeSpan.FromMinutes(5) + Grace + TimeSpan.FromMinutes(10);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        Assert.False(result.StoreUnreadable);
        Assert.Equal(["stuck"], result.OverdueJobIds);
        Assert.Equal(Grace + TimeSpan.FromMinutes(10), result.OldestOverdue);
    }

    [Fact]
    public async Task CronDispatchFreshness_BackoffFloorDefersDueTime()
    {
        var time = new ManualTimeProvider(Start);
        var job = Job("backing-off", nextRun: Start) with { BackoffUntil = Start + TimeSpan.FromHours(2) };
        var probe = CreateProbe(StoreWith(job), time);
        time.Now = Start + Grace + TimeSpan.FromMinutes(10);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_DisabledExpiredAndInvalidScheduleJobs_AreIgnored()
    {
        var time = new ManualTimeProvider(Start);
        var store = StoreWith(
            Job("disabled", nextRun: Start) with { Enabled = false },
            Job("expired", nextRun: Start) with { ExpiresAt = Start + TimeSpan.FromMinutes(1) },
            Job("invalid", nextRun: Start) with { Schedule = "not a cron" },
            Job("never-scheduled", nextRun: null));
        var probe = CreateProbe(store, time);
        time.Now = Start + Grace + TimeSpan.FromHours(1);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
        Assert.Empty(result.OverdueJobIds);
    }

    [Fact]
    public async Task CronDispatchFreshness_CronDisabled_IsHealthyAndDoesNotReadStore()
    {
        var time = new ManualTimeProvider(Start);
        var store = StoreWith(Job("stuck", nextRun: Start));
        var probe = CreateProbe(store, time, new CronOptions { Enabled = false });
        time.Now = Start + Grace + TimeSpan.FromHours(1);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
        await store.DidNotReceiveWithAnyArgs().ListAsync(default, default);
    }

    [Fact]
    public async Task CronDispatchFreshness_JobDueBeforeStartup_IsMeasuredFromStartup()
    {
        var time = new ManualTimeProvider(Start);
        var probe = CreateProbe(StoreWith(Job("missed-during-downtime", nextRun: Start - TimeSpan.FromDays(1))), time);
        time.Now = Start + Grace - TimeSpan.FromMinutes(1);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_NoCronStoreRegistered_IsHealthy()
    {
        var probe = new CronDispatchFreshnessProbe(new ServiceCollection().BuildServiceProvider(), new ManualTimeProvider(Start));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.False(result.IsStalled);
    }

    [Fact]
    public async Task CronDispatchFreshness_StoreThrows_LogsDistinctUnreadableWarning()
    {
        var store = Substitute.For<ICronStore>();
        store.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CronJob>>>(_ => throw new InvalidOperationException("database is locked"));
        var logger = new RecordingLogger<CronDispatchFreshnessProbe>();
        var probe = CreateProbe(store, new ManualTimeProvider(Start), logger: logger);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.IsStalled);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("could not be read", entry.Message);
    }

    private static ICronStore StoreWith(params CronJob[] jobs)
    {
        var store = Substitute.For<ICronStore>();
        store.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CronJob>>(jobs));
        return store;
    }

    private static CronJob Job(string id, DateTimeOffset? nextRun) => new()
    {
        Id = JobId.From(id),
        Name = id,
        Schedule = "*/5 * * * *",
        ActionType = "agent-prompt",
        NextRunAt = nextRun,
    };

    internal static CronDispatchFreshnessProbe CreateProbe(
        ICronStore store,
        TimeProvider time,
        CronOptions? options = null,
        ILogger<CronDispatchFreshnessProbe>? logger = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<CronOptions>>();
        monitor.CurrentValue.Returns(options ?? new CronOptions());
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(monitor)
            .BuildServiceProvider();
        return new CronDispatchFreshnessProbe(services, time, logger);
    }

    internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ThrowingDispatchProbe : IDispatchFreshnessProbe
    {
        public Task<DispatchFreshnessResult> CheckAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("database is locked");
    }

    private sealed class StubActivityTracker(TimeSpan elapsed) : IActivityTracker
    {
        public void RecordActivity() { }
        public TimeSpan TimeSinceLastActivity => elapsed;
        public DateTimeOffset LastActivityUtc => Start - elapsed;
    }

    private sealed class StubThreadPoolProbe : IThreadPoolProbe
    {
        public Task<bool> IsResponsiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    internal sealed record LogEntry(LogLevel Level, string Message);

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
