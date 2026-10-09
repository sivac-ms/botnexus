using System.Diagnostics.Metrics;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression guard for the orphaned-instrument defect: <see cref="HotPathMetrics.RecordCronRun"/>
/// was defined and unit-tested from the moment the counter was introduced, but had ZERO production
/// call sites. The consequence was that <c>botnexus.cron.runs</c> read 0 whether the scheduler was
/// healthy or completely dead - so a 100%-dark day (2026-09-02) and a 73%-missed-fire day (09-03)
/// both had to be discovered by hand-querying cron.sqlite.
/// <para>
/// A passing unit test on the metric method is exactly what let it sit unwired: it proved the
/// method worked, not that anything called it. These tests assert the <b>wiring</b>, and fail if
/// the call sites are ever removed.
/// </para>
/// </summary>
public sealed class CronSchedulerRunMetricsTests
{
    [Fact]
    public async Task ReapOrphanedRunsAsync_EmitsCronRunCounter()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-metric-1"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-metric-1"));
        await SetRunStartedAtAsync(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(-48));

        using var meter = new Meter("BotNexus.Test.CronRuns." + Guid.NewGuid().ToString("N"));
        var metrics = new HotPathMetrics(new BotNexusMetrics(meter));
        var measurements = new List<(long Value, string Job, string Status)>();
        using var listener = ListenForCronRuns(meter, measurements);

        var scheduler = CreateScheduler(context.Store, metrics);
        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);

        listener.Dispose();

        var measurement = measurements.ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Job.ShouldBe("job-metric-1");
        measurement.Status.ShouldBe(CronRunStatus.Error);
    }

    /// <summary>
    /// The scheduler must remain constructible without telemetry: the parameter is optional so that
    /// every pre-existing call site and test keeps working, and a null metrics instance must never
    /// throw on a terminal path.
    /// </summary>
    [Fact]
    public async Task ReapOrphanedRunsAsync_WithoutMetrics_DoesNotThrow()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        await context.Store.CreateAsync(CronStoreTestContext.CreateJob("job-metric-2"));
        var run = await context.Store.RecordRunStartAsync(JobId.From("job-metric-2"));
        await SetRunStartedAtAsync(context.DbPath, run.Id, DateTimeOffset.UtcNow.AddHours(-48));

        var scheduler = CreateScheduler(context.Store, metrics: null);

        (await scheduler.ReapOrphanedRunsAsync()).ShouldBe(1);
    }

    private static MeterListener ListenForCronRuns(
        Meter meter,
        List<(long Value, string Job, string Status)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == meter && instrument.Name == "botnexus.cron.runs")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string job = string.Empty, status = string.Empty;
            foreach (var tag in tags)
            {
                if (tag.Key == "job")
                {
                    job = tag.Value?.ToString() ?? string.Empty;
                }
                else if (tag.Key == "status")
                {
                    status = tag.Value?.ToString() ?? string.Empty;
                }
            }

            lock (sink)
            {
                sink.Add((value, job, status));
            }
        });

        listener.Start();
        return listener;
    }

    private static CronScheduler CreateScheduler(ICronStore store, HotPathMetrics? metrics)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            store,
            [],
            scopeFactory,
            new MetricsTestOptionsMonitor<CronOptions>(new CronOptions
            {
                Enabled = true,
                TickIntervalSeconds = 1,
                OrphanedRunThresholdSeconds = 3600
            }),
            NullLogger<CronScheduler>.Instance,
            timeProvider: null,
            metrics: metrics);
    }

    private static async Task SetRunStartedAtAsync(string dbPath, RunId runId, DateTimeOffset value)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE cron_runs SET started_at = $value WHERE id = $runId";
        cmd.Parameters.AddWithValue("$value", value.ToString("o"));
        cmd.Parameters.AddWithValue("$runId", runId.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class MetricsTestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
