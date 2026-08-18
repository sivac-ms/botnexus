namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Records the clean-shutdown marker exactly once, from whichever teardown path fires first.
/// <para>
/// The gateway can learn it is stopping from several places that overlap unpredictably:
/// <c>ApplicationStopping</c> (a drained host stop), a POSIX signal / Ctrl+C, and
/// <c>AppDomain.ProcessExit</c>. Registering all of them is what makes the marker actually get
/// written - <c>ApplicationStopped</c>, the only writer before this change, does not run when the
/// process is killed, which is exactly what the restart path does, so the marker was never written
/// and every boot reported "terminated uncleanly".
/// </para>
/// <para>
/// First writer wins: the earliest path carries the most accurate provenance (a signal knows it was
/// a signal; <c>ProcessExit</c> only knows the process is going away). Later paths are no-ops, so
/// they can never downgrade a recorded <see cref="ShutdownSource"/> to
/// <see cref="ShutdownSource.Unknown"/>. Nothing here throws: diagnostics must never destabilise
/// shutdown.
/// </para>
/// </summary>
public sealed class ShutdownMarkerRecorder
{
    private readonly CleanShutdownMarker _marker;
    private readonly List<IDisposable> _retained = new();
    private int _recorded;

    /// <summary>Creates a recorder that writes at most one marker through <paramref name="marker"/>.</summary>
    public ShutdownMarkerRecorder(CleanShutdownMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        _marker = marker;
    }

    /// <summary><c>true</c> once a shutdown has been recorded by any path.</summary>
    public bool HasRecorded => Volatile.Read(ref _recorded) != 0;

    /// <summary>
    /// Keeps <paramref name="registration"/> alive for the lifetime of this recorder. Signal
    /// registrations unregister themselves when collected, so one created inside a startup method
    /// must be rooted somewhere or the handler silently stops firing.
    /// </summary>
    public void Retain(IDisposable registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_retained)
            _retained.Add(registration);
    }

    /// <summary>
    /// Records a clean shutdown attributed to <paramref name="source"/> if no other path has
    /// already recorded one. Returns <c>true</c> when this call was the one that wrote the marker.
    /// Never throws.
    /// </summary>
    public bool Record(ShutdownSource source, string? reason = null)
    {
        if (Interlocked.Exchange(ref _recorded, 1) != 0)
            return false;

        try
        {
            _marker.MarkCleanShutdown(source, reason);
            return true;
        }
        catch
        {
            // Best effort - a missed marker only risks a false unclean warning on the next boot,
            // and a diagnostics failure must never propagate out of a shutdown callback.
            return false;
        }
    }
}
