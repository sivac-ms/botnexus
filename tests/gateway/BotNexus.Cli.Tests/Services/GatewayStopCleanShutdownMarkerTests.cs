using BotNexus.Cli.Services;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Pins the CLI half of the clean-shutdown marker fix.
/// <para>
/// The gateway writes the marker from its own lifetime/signal callbacks, but
/// <c>botnexus gateway stop|restart|update</c> terminates it with a hard kill, which on Windows
/// raises no signal and runs neither <c>ApplicationStopping</c> nor <c>ProcessExit</c> in the
/// target. So on the most common stop path NONE of the in-process writers can fire, and every
/// subsequent boot warned "previous gateway run terminated uncleanly". The CLI must therefore write
/// the marker itself - but only AFTER it has confirmed the process actually exited.
/// </para>
/// <para>
/// Every process here is an <see cref="IGatewayProcessHandle"/> fake: nothing is spawned,
/// enumerated or signalled for real.
/// </para>
/// </summary>
public sealed class GatewayStopCleanShutdownMarkerTests : IDisposable
{
    private readonly string _home;
    private readonly IHealthChecker _healthChecker = Substitute.For<IHealthChecker>();

    public GatewayStopCleanShutdownMarkerTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"bn-marker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeProcessHandle(
        int id,
        string? executablePath,
        Exception? killException = null,
        bool exits = true)
        : IGatewayProcessHandle
    {
        public int Id { get; } = id;

        public int KillCount { get; private set; }

        public string? ExecutablePath => executablePath;

        public void Kill()
        {
            KillCount++;
            if (killException is not null)
                throw killException;
        }

        public bool WaitForExit(int milliseconds) => exits;
    }

    private GatewayProcessManager NewManager(params IGatewayProcessHandle[] processes)
        => new(
            _healthChecker,
            NullLogger<GatewayProcessManager>.Instance,
            waitForExitTimeout: TimeSpan.FromMilliseconds(50),
            processEnumerator: () => processes);

    private string GatewayDll => Path.Combine(_home, "bin", "BotNexus.Gateway.Api.dll");

    /// <summary>
    /// The directory the CLI resolves for the marker. Asserted against the gateway's own boot-time
    /// rule so the two can never drift apart silently.
    /// </summary>
    private string MarkerDirectory => GatewayProcessManager.ResolveMarkerDirectory(_home);

    private string MarkerPath => Path.Combine(MarkerDirectory, ".gateway-clean-shutdown");

    private CleanShutdownMarker Marker
        => new(new System.IO.Abstractions.FileSystem(), MarkerDirectory);

    [Fact]
    public void ResolveMarkerDirectory_MatchesTheRuleTheGatewayUsesAtBoot()
    {
        // The gateway host resolves its data directory as
        //   BotNexusHome.ResolveDataPath() ?? BotNexusHome.ResolveHomePath()
        // and the CLI exports the same home to the child as BOTNEXUS_HOME on start.
        var expected = BotNexusHome.ResolveDataPath() ?? BotNexusHome.ResolveHomePath(_home);

        GatewayProcessManager.ResolveMarkerDirectory(_home).ShouldBe(expected);
    }

    [Fact]
    public async Task StopAsync_WritesCleanShutdownMarker_WhenProcessKilledAndConfirmedExited()
    {
        var gateway = new FakeProcessHandle(4242, GatewayDll);
        var manager = NewManager(gateway);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
        gateway.KillCount.ShouldBe(1);

        File.Exists(MarkerPath).ShouldBeTrue(
            $"a confirmed stop must leave a clean-shutdown marker at {MarkerPath}");

        var previous = Marker.DetectPreviousRun();
        previous.WasClean.ShouldBeTrue();
        previous.Source.ShouldBe(ShutdownSource.Cli);
    }

    [Fact]
    public async Task StopAsync_WritesCleanShutdownMarker_WhenProcessHadAlreadyExited()
    {
        // Kill() throwing InvalidOperationException means the process was already gone - still a
        // successful stop, so still a clean shutdown.
        var gateway = new FakeProcessHandle(
            4243,
            GatewayDll,
            killException: new InvalidOperationException("process has exited"));
        var manager = NewManager(gateway);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        result.Outcome.ShouldBe(GatewayStopOutcome.Stopped);
        File.Exists(MarkerPath).ShouldBeTrue("an already-exited gateway is a clean stop, not a crash");
        Marker.DetectPreviousRun().Source.ShouldBe(ShutdownSource.Cli);
    }

    [Fact]
    public async Task StopAsync_DoesNotWriteMarker_WhenKillFails()
    {
        // Win32Exception from Kill() = the stop FAILED. Recording a clean shutdown here would mask
        // a genuine subsequent crash.
        var gateway = new FakeProcessHandle(
            4244,
            GatewayDll,
            killException: new System.ComponentModel.Win32Exception("access is denied"));
        var manager = NewManager(gateway);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        result.Outcome.ShouldBe(GatewayStopOutcome.Failed);
        result.Success.ShouldBeFalse();
        File.Exists(MarkerPath).ShouldBeFalse("a failed kill must never be recorded as a clean shutdown");
    }

    [Fact]
    public async Task StopAsync_DoesNotWriteMarker_WhenProcessDoesNotExitInTime()
    {
        var gateway = new FakeProcessHandle(4245, GatewayDll, exits: false);
        var manager = NewManager(gateway);

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        result.Outcome.ShouldBe(GatewayStopOutcome.Failed);
        File.Exists(MarkerPath).ShouldBeFalse(
            "the process may still be running - exit was never confirmed");
    }

    [Fact]
    public async Task StopAsync_DoesNotWriteMarker_WhenNothingWasRunning()
    {
        var manager = NewManager(new FakeProcessHandle(1, Path.Combine(_home, "foreign.exe")));

        var result = await manager.StopAsync(_home, GatewayDll, CancellationToken.None);

        result.Outcome.ShouldBe(GatewayStopOutcome.NotRunning);
        File.Exists(MarkerPath).ShouldBeFalse("nothing was stopped, so nothing should be recorded");
    }
}
