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

    internal static CronDispatchFreshnessProbe CreateProbe(
        ICronStore store,
        TimeProvider time,
        CronOptions? options = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<CronOptions>>();
        monitor.CurrentValue.Returns(options ?? new CronOptions());
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(monitor)
            .BuildServiceProvider();
        return new CronDispatchFreshnessProbe(services, time);
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
