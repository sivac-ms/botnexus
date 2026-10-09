using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression coverage for never-run jobs in the missed-run scan. The scan used to skip any job
/// whose <see cref="CronJob.LastRunAt"/> was null ("no baseline"), so a job whose first due
/// occurrence fell inside a scheduler dark window produced no missed row and no alert. In
/// production two one-shot agent-prompt jobs ended with zero run rows, <c>last_run_at = null</c>
/// and <c>next_run_at</c> silently rolled a year ahead. A never-run job now uses
/// <c>max(CreatedAt, ScheduleActivatedAt ?? CreatedAt)</c> as its floor.
/// </summary>
public sealed class MissedRunNeverRunJobTests
{
    private static readonly DateTimeOffset ScanTime = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    // A one-shot-style schedule: fires once, at 10:00 UTC on 11 June (next occurrence is a year out).
    private const string OneShotSchedule = "0 10 11 6 *";
    private static readonly DateTimeOffset DueOccurrence = new(2026, 6, 11, 10, 0, 0, TimeSpan.Zero);

    // (1) never-run job whose due time fell after created_at, with no run -> a missed row is recorded.
    [Fact]
    public void GetMissedRuns_NeverRun_DueAfterCreatedAt_ReturnsTheDueOccurrence()
    {
        var job = CreateJob("never-run-due") with
        {
            Schedule = OneShotSchedule,
            CreatedAt = new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero),
            LastRunAt = null
        };

        var result = MissedRunDetectionService.GetMissedRuns(job, ScanTime);

        result.ShouldBe([DueOccurrence]);
        MissedRunDetectionService.GetScanFloorUtc(job).ShouldBe(job.CreatedAt.UtcDateTime);
    }

    [Fact]
    public async Task StartAsync_NeverRun_DueAfterCreatedAt_RecordsMissedRowAndWarns()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("never-run-e2e") with
        {
            Schedule = OneShotSchedule,
            CreatedAt = new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero),
            LastRunAt = null
        });
        created.LastRunAt.ShouldBeNull();
        (await context.Store.GetRunHistoryAsync(created.Id, limit: 50)).ShouldBeEmpty();

        var logger = new CapturingLogger();
        var service = new MissedRunDetectionService(context.Store, null!, logger, new ManualTimeProvider(ScanTime));

        await service.StartAsync(CancellationToken.None);

        var history = await context.Store.GetRunHistoryAsync(created.Id, limit: 50);
        var missed = history.Where(r => r.Status == MissedRunDetectionService.MissedStatus).ToList();
        missed.Count.ShouldBe(1);
        missed[0].StartedAt.ToUniversalTime().ShouldBe(DueOccurrence);
        logger.Messages.ShouldContain(m => m.Contains("missed scheduled run", StringComparison.Ordinal));

        // Idempotent across restarts: the never-run path does not advance last_run_at either.
        await service.StartAsync(CancellationToken.None);
        (await context.Store.GetRunHistoryAsync(created.Id, limit: 50))
            .Count(r => r.Status == MissedRunDetectionService.MissedStatus).ShouldBe(1);
    }

    // (2) never-run job created after the due occurrence -> not flagged.
    [Fact]
    public void GetMissedRuns_NeverRun_CreatedAfterDueOccurrence_ReturnsEmpty()
    {
        var job = CreateJob("never-run-late") with
        {
            Schedule = OneShotSchedule,
            CreatedAt = new DateTimeOffset(2026, 6, 11, 10, 30, 0, TimeSpan.Zero),
            LastRunAt = null
        };

        MissedRunDetectionService.GetMissedRuns(job, ScanTime).ShouldBeEmpty();
        MissedRunDetectionService.WasTruncated(job, ScanTime).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_NeverRun_CreatedAfterDueOccurrence_RecordsNothing()
    {
        await using var context = await CronStoreTestContext.CreateAsync();

        var created = await context.Store.CreateAsync(CronStoreTestContext.CreateJob("never-run-late-e2e") with
        {
            Schedule = OneShotSchedule,
            CreatedAt = new DateTimeOffset(2026, 6, 11, 10, 30, 0, TimeSpan.Zero),
            LastRunAt = null
        });

        var logger = new CapturingLogger();
        var service = new MissedRunDetectionService(context.Store, null!, logger, new ManualTimeProvider(ScanTime));

        await service.StartAsync(CancellationToken.None);

        (await context.Store.GetRunHistoryAsync(created.Id, limit: 50)).ShouldBeEmpty();
        logger.Messages.ShouldNotContain(m => m.Contains("missed scheduled run", StringComparison.Ordinal));
    }

    [Fact]
    public void GetMissedRuns_NeverRun_ScheduleActivatedAfterDueOccurrence_ReturnsEmpty()
    {
        // floor = max(CreatedAt, ScheduleActivatedAt ?? CreatedAt): created before the due time,
        // but the current schedule only took effect after it, so the occurrence never belonged
        // to the active schedule.
        var job = CreateJob("never-run-reactivated") with
        {
            Schedule = OneShotSchedule,
            CreatedAt = new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero),
            ScheduleActivatedAt = new DateTimeOffset(2026, 6, 11, 10, 30, 0, TimeSpan.Zero),
            LastRunAt = null
        };

        MissedRunDetectionService.GetScanFloorUtc(job).ShouldBe(job.ScheduleActivatedAt!.Value.UtcDateTime);
        MissedRunDetectionService.GetMissedRuns(job, ScanTime).ShouldBeEmpty();
    }

    [Fact]
    public void GetScanFloorUtc_NeverRun_DefaultCreatedAt_IsUnknown()
    {
        // A row with no creation instant has no defensible baseline; it must not replay from year 1.
        var job = CreateJob("never-run-no-created") with { CreatedAt = default, LastRunAt = null };

        MissedRunDetectionService.GetScanFloorUtc(job).ShouldBeNull();
        MissedRunDetectionService.GetMissedRuns(job, ScanTime).ShouldBeEmpty();
    }

    // (3) the existing LastRunAt path is unchanged: CreatedAt is ignored once the job has run.
    [Fact]
    public void GetMissedRuns_WithLastRunAt_FloorIsLastRunAtNotCreatedAt()
    {
        var job = CreateJob("has-run") with
        {
            Schedule = "*/15 * * * *",
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            LastRunAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero)
        };

        MissedRunDetectionService.GetScanFloorUtc(job).ShouldBe(job.LastRunAt!.Value.UtcDateTime);
        MissedRunDetectionService.GetMissedRuns(job, ScanTime).ShouldBe(
        [
            new DateTimeOffset(2026, 6, 11, 11, 15, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 11, 11, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 11, 11, 45, 0, TimeSpan.Zero)
        ]);
    }

    [Fact]
    public void GetMissedRuns_WithLastRunAt_ActivationClampStillApplies()
    {
        var job = CreateJob("has-run-activated") with
        {
            Schedule = "*/15 * * * *",
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            LastRunAt = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.Zero),
            ScheduleActivatedAt = new DateTimeOffset(2026, 6, 11, 11, 20, 0, TimeSpan.Zero)
        };

        MissedRunDetectionService.GetMissedRuns(job, ScanTime).ShouldBe(
        [
            new DateTimeOffset(2026, 6, 11, 11, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 11, 11, 45, 0, TimeSpan.Zero)
        ]);
    }

    private static CronJob CreateJob(string id) => new()
    {
        Id = JobId.From(id),
        Name = $"Job {id}",
        Schedule = "*/5 * * * *",
        ActionType = "agent-prompt",
        AgentId = AgentId.From("test-agent"),
        Enabled = true,
        CreatedBy = "test",
        CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private sealed class CapturingLogger : ILogger<MissedRunDetectionService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
