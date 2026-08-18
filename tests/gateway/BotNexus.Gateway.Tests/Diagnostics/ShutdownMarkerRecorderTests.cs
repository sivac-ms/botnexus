using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// Covers the bug that made the crash detector useless: the marker used to be written only from
/// <c>ApplicationStopped</c>, which does not run when the process is killed, so every boot warned
/// "terminated uncleanly". These tests assert both directions - a graceful stop must NOT warn on
/// the next boot, and a hard kill MUST.
/// </summary>
public sealed class ShutdownMarkerRecorderTests
{
    private static string NewDataDir() =>
        Path.Combine(Path.GetTempPath(), "botnexus-recorder-tests", Guid.NewGuid().ToString("N"));

    private static (MockFileSystem Fs, CleanShutdownMarker Marker, string DataDir) NewMarker()
    {
        var dataDir = NewDataDir();
        var fs = new MockFileSystem();
        return (fs, new CleanShutdownMarker(fs, dataDir), dataDir);
    }

    [Fact]
    public void Record_FirstCall_WritesMarkerWithSource()
    {
        var (fs, marker, dataDir) = NewMarker();
        var recorder = new ShutdownMarkerRecorder(marker);

        recorder.Record(ShutdownSource.Sigterm, "SIGTERM").ShouldBeTrue();

        recorder.HasRecorded.ShouldBeTrue();
        fs.FileExists(Path.Combine(dataDir, ".gateway-clean-shutdown")).ShouldBeTrue();

        var result = marker.DetectPreviousRun();
        result.WasClean.ShouldBeTrue();
        result.Source.ShouldBe(ShutdownSource.Sigterm);
        result.Reason.ShouldBe("SIGTERM");
    }

    [Fact]
    public void Record_SecondCall_IsNoOpAndDoesNotOverwriteSource()
    {
        var (_, marker, _) = NewMarker();
        var recorder = new ShutdownMarkerRecorder(marker);

        recorder.Record(ShutdownSource.Sigterm, "SIGTERM").ShouldBeTrue();
        // A later, less-specific path (ProcessExit) must not downgrade the recorded provenance.
        recorder.Record(ShutdownSource.Unknown, "process exit").ShouldBeFalse();

        marker.DetectPreviousRun().Source.ShouldBe(ShutdownSource.Sigterm);
    }

    [Fact]
    public void Record_ConcurrentCalls_WriteExactlyOnce()
    {
        var (_, marker, _) = NewMarker();
        var recorder = new ShutdownMarkerRecorder(marker);

        var winners = 0;
        Parallel.For(0, 32, _ =>
        {
            if (recorder.Record(ShutdownSource.HostShutdown))
                Interlocked.Increment(ref winners);
        });

        winners.ShouldBe(1);
    }

    [Fact]
    public void GracefulStopThenReboot_DoesNotReportUncleanTermination()
    {
        // Boot 1: previous state cleared, run starts.
        var (fs, marker, dataDir) = NewMarker();
        marker.MarkRunning();

        // Graceful stop through the host-stopping path (this is what now actually fires).
        new ShutdownMarkerRecorder(marker).Record(ShutdownSource.HostShutdown, "host application stopping");

        // Boot 2: a fresh marker instance over the same directory, as a new process would see it.
        var nextBoot = new CleanShutdownMarker(fs, dataDir).DetectPreviousRun();

        nextBoot.WasClean.ShouldBeTrue();
        nextBoot.Source.ShouldBe(ShutdownSource.HostShutdown);
    }

    [Fact]
    public void HardKillWithoutRecording_ReportsUncleanTermination()
    {
        // Boot 1: run starts and clears the marker...
        var (fs, marker, dataDir) = NewMarker();
        marker.MarkRunning();

        // ...then the process dies hard: no shutdown callback ever runs, so nothing is recorded.

        var nextBoot = new CleanShutdownMarker(fs, dataDir).DetectPreviousRun();

        nextBoot.WasClean.ShouldBeFalse();
        nextBoot.LastKnownUtc.ShouldBeNull();
    }

    [Fact]
    public void Constructor_NullMarker_Throws()
    {
        Action act = () => _ = new ShutdownMarkerRecorder(null!);
        Should.Throw<ArgumentNullException>(act);
    }

    [Fact]
    public void Retain_KeepsRegistrationReferenced_AndRejectsNull()
    {
        var (_, marker, _) = NewMarker();
        var recorder = new ShutdownMarkerRecorder(marker);

        var registration = new TrackedDisposable();
        recorder.Retain(registration);
        registration.Disposed.ShouldBeFalse();

        Action act = () => recorder.Retain(null!);
        Should.Throw<ArgumentNullException>(act);
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
