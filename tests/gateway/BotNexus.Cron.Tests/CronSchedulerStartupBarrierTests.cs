using BotNexus.Cron.Extensions;
using BotNexus.Cron.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression cover for the gateway-startup race: <see cref="CronScheduler"/> is a
/// <see cref="BackgroundService"/>, so its <c>ExecuteAsync</c> is started but NOT awaited by the
/// host and therefore runs concurrently with the remaining <c>IHostedService.StartAsync</c> calls -
/// including the one that populates <c>IAgentRegistry</c>. Firing inside that window produced
/// `Agent 'X' is not registered.` for every job in the sweep.
/// </summary>
public sealed class CronSchedulerStartupBarrierTests
{
    [Fact]
    public async Task ExecuteAsync_DoesNotTick_UntilApplicationStartedIsSignalled()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var action = new RecordingAction("test-action");

        var job = CronStoreTestContext.CreateJob("job-barrier", actionType: "test-action") with
        {
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.CreateAsync(job);

        var lifetime = new TestHostApplicationLifetime();
        var logger = new BarrierLogger();
        var scheduler = new CronScheduler(
            context.Store,
            [action],
            new NullServiceScopeFactory(),
            new OptionsMonitorStub(new CronOptions { Enabled = true, TickIntervalSeconds = 1 }),
            logger,
            timeProvider: null,
            applicationLifetime: lifetime);

        await scheduler.StartAsync(CancellationToken.None);

        // Startup has NOT completed yet: the scheduler must be parked on the barrier. Wait until it
        // reports reaching the barrier, then check that nothing fired on the way there.
        await logger.ParkedOnBarrier.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, action.Executions);

        // Releasing the barrier lets the overdue job fire.
        lifetime.SignalStarted();

        // Poll generously: the assertion is unchanged, only the patience is. A tight window
        // here would flake on a loaded host without making the test any stricter.
        await action.FirstExecuted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(action.Executions > 0, "scheduler never ticked after ApplicationStarted was signalled");

        await scheduler.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The barrier is only real if the container actually supplies the lifetime. The constructor
    /// parameter is optional, and an optional parameter that DI cannot resolve is silently left at
    /// its default - which would turn the fix into a no-op that still passes the behavioural test
    /// above. This pins the wiring itself.
    /// </summary>
    [Fact]
    public void DependencyInjection_SuppliesApplicationLifetime_ToScheduler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusCron();

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddBotNexusCron())
            .Build();

        var scheduler = host.Services.GetRequiredService<CronScheduler>();

        var field = typeof(CronScheduler).GetField(
            "_applicationLifetime",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.NotNull(field!.GetValue(scheduler));
    }

    /// <summary>Signals when the scheduler logs that it is parked on the host-startup barrier.</summary>
    private sealed class BarrierLogger : ILogger<CronScheduler>
    {
        public TaskCompletionSource ParkedOnBarrier { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Cron scheduler waiting for host startup", StringComparison.Ordinal))
                ParkedOnBarrier.TrySetResult();
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void SignalStarted() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class RecordingAction(string actionType) : ICronAction
    {
        private int _executions;
        public string ActionType { get; } = actionType;
        public int Executions => Volatile.Read(ref _executions);
        public TaskCompletionSource FirstExecuted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            FirstExecuted.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class NullServiceScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => null;
        public void Dispose() { }
    }

    private sealed class OptionsMonitorStub(CronOptions value) : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue { get; } = value;
        public CronOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }
}
