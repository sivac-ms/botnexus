using System.Diagnostics;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// Ensures the out-of-process availability watchdog is installed and scheduled whenever the
/// gateway starts.
/// <para>
/// The watchdog deliberately does NOT run inside this process. It exists to detect and recover
/// from conditions in which the gateway is unresponsive or its provider credentials are dead, so
/// hosting it here would make it share the fate of the thing it monitors. Instead a Windows
/// Scheduled Task owns the 5-minute timer and reboot-survival; this class only guarantees the task
/// exists, which makes monitoring self-installing rather than a documented manual step that is
/// easy to skip (on 2026-08-17 the watchdog was never installed and the portal showed a stale
/// snapshot for 37 minutes).
/// </para>
/// <para>
/// Best-effort by construction: every failure path is swallowed and logged. A monitoring
/// component must never be able to prevent the gateway from starting.
/// </para>
/// </summary>
public sealed class AvailabilityWatchdogInstaller(
    BotNexusHome home,
    ILogger<AvailabilityWatchdogInstaller> logger) : IHostedService
{
    private static readonly string[] CandidateInterpreters =
    [
        @"C:\Program Files\Python313\python.exe",
        @"%LOCALAPPDATA%\Programs\Python\Python314\python.exe",
        @"%LOCALAPPDATA%\Programs\Python\Python313\python.exe",
    ];

    /// <summary>
    /// Invokes <c>watchdog.py --ensure</c>, which installs the scheduled task if it is absent and
    /// then runs one immediate pass so the portal has fresh data instead of a stale snapshot.
    /// </summary>
    /// <param name="cancellationToken">Token observed while waiting for the helper to exit.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var script = Path.Combine(
                home.RootPath, "agents", "assistant", "workspace",
                "ops", "availability", "watchdog.py");

            if (!File.Exists(script))
            {
                logger.LogDebug("Availability watchdog script not present at {Script}; skipping.", script);
                return;
            }

            var interpreter = ResolveInterpreter();
            if (interpreter is null)
            {
                logger.LogWarning("No Python interpreter found; availability watchdog not installed.");
                return;
            }

            var startInfo = new ProcessStartInfo(interpreter)
            {
                // --ensure is idempotent and always exits 0.
                ArgumentList = { script, "--ensure" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            // Bounded wait: startup must not block on a wedged helper.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                logger.LogInformation(
                    "Availability watchdog ensured (exit {ExitCode}).", process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Availability watchdog install timed out; continuing startup.");
            }
        }
        catch (Exception ex)
        {
            // Monitoring must never break the thing it monitors.
            logger.LogWarning(ex, "Could not ensure the availability watchdog; continuing startup.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Resolves a concrete interpreter path. The bare name <c>python</c> is never used because on
    /// Windows it resolves to the WindowsApps alias stub, which is not a real interpreter.
    /// </summary>
    private static string? ResolveInterpreter()
    {
        foreach (var candidate in CandidateInterpreters)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
            {
                return expanded;
            }
        }

        return null;
    }
}
