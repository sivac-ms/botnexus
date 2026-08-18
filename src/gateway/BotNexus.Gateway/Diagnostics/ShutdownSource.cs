namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Who asked the gateway to stop. Recorded in the clean-shutdown marker and in the clean crash
/// record so that a boot which follows a deliberate stop is distinguishable from one that follows
/// a real crash - the distinction that #1901's marker was meant to provide but could not, because
/// the marker was only written from <c>ApplicationStopped</c> and the restart path kills the
/// process instead of draining the host.
/// </summary>
public enum ShutdownSource
{
    /// <summary>Source could not be determined.</summary>
    Unknown = 0,

    /// <summary>An operator ran <c>botnexus gateway stop</c> or <c>gateway restart</c>.</summary>
    Cli,

    /// <summary>The availability watchdog initiated a recovery restart.</summary>
    Watchdog,

    /// <summary>A POSIX signal / Ctrl+C reached the host and it began draining.</summary>
    Sigterm,

    /// <summary>The host itself began shutting down (service stop, container stop, machine shutdown).</summary>
    HostShutdown,

    /// <summary>An in-place update replaced the binaries and restarted the process.</summary>
    Update
}

/// <summary>
/// Canonical wire strings for <see cref="ShutdownSource"/>. These values are written to the marker
/// file and to crash records and are asserted on by tests and by the watchdog, so they are treated
/// as a stable contract rather than as incidental enum names.
/// </summary>
public static class ShutdownSourceNames
{
    /// <summary>Returns the stable lower-kebab wire name for <paramref name="source"/>.</summary>
    public static string ToWireName(this ShutdownSource source) => source switch
    {
        ShutdownSource.Cli => "cli",
        ShutdownSource.Watchdog => "watchdog",
        ShutdownSource.Sigterm => "sigterm",
        ShutdownSource.HostShutdown => "host-shutdown",
        ShutdownSource.Update => "update",
        _ => "unknown"
    };

    /// <summary>
    /// Parses a wire name back to a <see cref="ShutdownSource"/>. Unrecognised input degrades to
    /// <see cref="ShutdownSource.Unknown"/> rather than throwing: this parses attacker-irrelevant
    /// but crash-time-written data, and a diagnostics read must never fail a boot.
    /// </summary>
    public static ShutdownSource FromWireName(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cli" => ShutdownSource.Cli,
        "watchdog" => ShutdownSource.Watchdog,
        "sigterm" => ShutdownSource.Sigterm,
        "host-shutdown" => ShutdownSource.HostShutdown,
        "update" => ShutdownSource.Update,
        _ => ShutdownSource.Unknown
    };
}
