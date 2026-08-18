using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Cron;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Outcome of inspecting the clean-shutdown marker at boot.
/// </summary>
/// <param name="WasClean">
/// <c>true</c> when the previous run wrote a shutdown marker (it stopped gracefully);
/// <c>false</c> when no marker was found, meaning the prior run terminated uncleanly.
/// </param>
/// <param name="LastKnownUtc">
/// The last instant the previous run is known to have been alive.
/// <para>
/// For a clean stop this is the timestamp recorded in the shutdown marker. For an unclean
/// termination it is the last liveness stamp the dying run managed to write, which is what
/// makes the crash-window diagnostic useful: it is accurate to within one
/// <see cref="CleanShutdownMarker.DefaultLivenessRefreshInterval"/>.
/// </para>
/// Null only when no stamp of either kind is available (genuine first boot, or a data
/// directory that could not be read) - callers must then omit the timestamp entirely rather
/// than reporting a placeholder.
/// </param>
/// <param name="Source">
/// Who initiated the recorded shutdown. <see cref="ShutdownSource.Unknown"/> for a marker written
/// by an older build (bare-timestamp format) or one whose contents could not be parsed.
/// </param>
/// <param name="Reason">Free-text reason recorded alongside the source, when present.</param>
public readonly record struct PreviousRunResult(
    bool WasClean,
    DateTimeOffset? LastKnownUtc,
    ShutdownSource Source = ShutdownSource.Unknown,
    string? Reason = null);

/// <summary>
/// Tracks whether the gateway shut down cleanly using a marker file
/// (<c>~/.botnexus/.gateway-clean-shutdown</c>).
/// <para>
/// Boot sequence: call <see cref="DetectPreviousRun"/> to learn how the last run ended,
/// then <see cref="MarkRunning"/> to clear the marker for the current run. A graceful stop
/// calls <see cref="MarkCleanShutdown(ShutdownSource, string?)"/>. If the process dies hard, the
/// marker is absent on next boot and the prior run is reported as unclean.
/// </para>
/// <para>
/// <b>Why the writer is not only in-process.</b> Until this change the marker was written solely
/// from <c>ApplicationStopped</c>. Every supported stop path (<c>botnexus gateway stop|restart</c>,
/// <c>update</c>, the availability watchdog) terminates the process with a hard kill rather than
/// draining the host, so <c>ApplicationStopped</c> never ran, the marker was never written, and
/// <i>100% of boots</i> reported "terminated uncleanly". A detector that is stuck on carries zero
/// information: a real crash and a clean restart looked identical. The marker is therefore now
/// written from two places - the host's <c>ApplicationStopping</c> callback (covers SIGTERM, Ctrl+C
/// and host/service shutdown, which really do drain) and the CLI process manager immediately
/// <i>after</i> it has confirmed the gateway process exited (covers the hard-kill paths). Writing
/// it post-exit rather than pre-kill means a kill that fails leaves no marker, so a subsequent
/// genuine crash is still reported as unclean.
/// </para>
/// </summary>
public sealed class CleanShutdownMarker : IPlannedShutdownState
{
    private const string MarkerFileName = ".gateway-clean-shutdown";
    private const string LivenessFileName = ".gateway-liveness";
    private const string PlannedFileName = ".gateway-planned-shutdown";

    /// <summary>
    /// How often the liveness stamp is rewritten while the gateway runs. This is the accuracy
    /// bound on the "last known alive" instant reported after an unclean termination: the real
    /// death happened somewhere in the interval starting at the reported stamp. Kept coarse on
    /// purpose - the diagnostic wants a crash *window*, not a heartbeat, and a tighter interval
    /// would buy no investigative value while writing to disk more often.
    /// </summary>
    public static readonly TimeSpan DefaultLivenessRefreshInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions MarkerJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IFileSystem _fileSystem;
    private readonly string _markerPath;
    private readonly string _livenessPath;
    private readonly string _plannedPath;
    private readonly string _dataDirectory;

    /// <summary>Cached boot-time classification retained after MarkRunning clears on-disk evidence.</summary>
    public bool PreviousShutdownWasPlanned { get; private set; }

    /// <summary>Whether this process has durably accepted a planned shutdown request.</summary>
    public bool CurrentShutdownIsPlanned { get; private set; }

    /// <summary>
    /// Creates a marker manager rooted at <paramref name="dataDirectory"/> (the writable
    /// BotNexus data directory). The marker file lives directly inside that directory.
    /// </summary>
    public CleanShutdownMarker(IFileSystem fileSystem, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _fileSystem = fileSystem;
        _dataDirectory = dataDirectory;
        _markerPath = _fileSystem.Path.Combine(dataDirectory, MarkerFileName);
        _livenessPath = _fileSystem.Path.Combine(dataDirectory, LivenessFileName);
        _plannedPath = _fileSystem.Path.Combine(dataDirectory, PlannedFileName);
    }

    /// <summary>The absolute path of the marker file, exposed for logging/diagnostics.</summary>
    public string MarkerPath => _markerPath;

    /// <summary>The absolute path of the liveness stamp file, exposed for logging/diagnostics.</summary>
    public string LivenessPath => _livenessPath;

    /// <summary>
    /// Persists planned-shutdown intent before acknowledging the lifecycle request. A clean stop
    /// must still occur for the next process to classify this as a planned shutdown.
    /// </summary>
    public bool TryMarkPlannedShutdown(DateTimeOffset requestedAtUtc)
    {
        try
        {
            if (!_fileSystem.Directory.Exists(_dataDirectory))
                _fileSystem.Directory.CreateDirectory(_dataDirectory);
            var stamp = requestedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            _fileSystem.File.WriteAllText(_plannedPath, stamp);
            return CurrentShutdownIsPlanned = _fileSystem.File.ReadAllText(_plannedPath) == stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True only when both planned intent and a clean shutdown marker survived boot.</summary>
    public bool WasPreviousShutdownPlanned()
    {
        var previousRun = DetectPreviousRun();
        if (!previousRun.WasClean || previousRun.LastKnownUtc is not { } stoppedAt)
            return PreviousShutdownWasPlanned = false;
        try
        {
            return PreviousShutdownWasPlanned = _fileSystem.File.Exists(_plannedPath)
                && DateTimeOffset.TryParse(_fileSystem.File.ReadAllText(_plannedPath),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var requestedAt)
                && requestedAt <= stoppedAt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PreviousShutdownWasPlanned = false;
        }
    }

    /// <summary>
    /// Inspects the marker to determine how the previous run ended. Never throws; any I/O or
    /// parse failure degrades to a best-effort answer so boot is never blocked by diagnostics.
    /// </summary>
    public PreviousRunResult DetectPreviousRun()
    {
        try
        {
            // No shutdown marker: the previous run died without stopping gracefully. Fall back to
            // the liveness stamp it was refreshing while alive - that is the whole point of the
            // stamp, and the only way this branch can report a real instant.
            if (!_fileSystem.File.Exists(_markerPath))
                return new PreviousRunResult(WasClean: false, LastKnownUtc: ReadLiveness());

            var raw = _fileSystem.File.ReadAllText(_markerPath).Trim();

            // Current format: a small JSON object carrying timestamp, source and reason.
            if (raw.StartsWith('{'))
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<MarkerPayload>(raw, MarkerJsonOptions);
                    if (payload is not null)
                    {
                        DateTimeOffset? stampFromJson = DateTimeOffset.TryParse(
                            payload.Ts,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var parsed)
                            ? parsed
                            : null;

                        return new PreviousRunResult(
                            WasClean: true,
                            LastKnownUtc: stampFromJson,
                            Source: ShutdownSourceNames.FromWireName(payload.Source),
                            Reason: payload.Reason);
                    }
                }
                catch (JsonException)
                {
                    // Torn or truncated marker (we crashed mid-write). It still proves a graceful
                    // stop was under way, so report clean with no detail rather than claiming a crash.
                    return new PreviousRunResult(WasClean: true, LastKnownUtc: null);
                }
            }

            // Legacy format written by builds before this change: a bare round-trip timestamp.
            // A present marker means the last run reached graceful shutdown, even if we can't
            // parse the timestamp it wrote - so it's clean, just with an unknown timestamp.
            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var stamp))
            {
                return new PreviousRunResult(WasClean: true, LastKnownUtc: stamp);
            }

            return new PreviousRunResult(WasClean: true, LastKnownUtc: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable marker: fail safe as "unclean" so an operator investigates. The liveness
            // stamp is a separate file, so try it anyway - it may still be readable.
            return new PreviousRunResult(WasClean: false, LastKnownUtc: ReadLiveness());
        }
    }

    /// <summary>
    /// Reads the liveness stamp left behind by a previous run, or null when there is none or it
    /// cannot be read/parsed. Never throws.
    /// </summary>
    private DateTimeOffset? ReadLiveness()
    {
        try
        {
            if (!_fileSystem.File.Exists(_livenessPath))
                return null;

            var raw = _fileSystem.File.ReadAllText(_livenessPath).Trim();
            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var stamp)
                ? stamp
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the liveness stamp for the current run. Called once at boot and then on every tick
    /// of the refresh timer, so a hard kill leaves the most recent write behind as evidence of
    /// when the process was last alive. Best effort: never throws.
    /// </summary>
    public void RefreshLiveness() => RefreshLiveness(DateTimeOffset.UtcNow);

    /// <summary>
    /// Overload accepting an explicit timestamp; used by tests and callers that already hold the
    /// instant to record.
    /// </summary>
    public void RefreshLiveness(DateTimeOffset timestampUtc)
    {
        try
        {
            if (!_fileSystem.Directory.Exists(_dataDirectory))
                _fileSystem.Directory.CreateDirectory(_dataDirectory);
            _fileSystem.File.WriteAllText(
                _livenessPath,
                timestampUtc.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - a missed refresh only widens the reported crash window.
        }
    }

    /// <summary>
    /// Starts refreshing the liveness stamp every <paramref name="interval"/> (defaulting to
    /// <see cref="DefaultLivenessRefreshInterval"/>). Dispose the returned handle to stop the
    /// refresh - typically on graceful shutdown, after which the clean-shutdown marker takes over
    /// as the authoritative record.
    /// </summary>
    public IDisposable StartLivenessRefresh(TimeSpan? interval = null)
    {
        var period = interval ?? DefaultLivenessRefreshInterval;
        RefreshLiveness();
        return new Timer(_ => RefreshLiveness(), state: null, dueTime: period, period: period);
    }

    /// <summary>
    /// Builds the operator-facing unclean-termination warning for <paramref name="previousRun"/>.
    /// <para>
    /// Kept here rather than inline at the call site so the message shape is directly testable:
    /// the whole point of #3680 is that the timestamp clause must carry a real instant when one
    /// exists and must be <em>absent</em> when one does not, never a placeholder.
    /// </para>
    /// </summary>
    /// <returns>The warning text, or null when the previous run was clean (no warning is due).</returns>
    public static string? BuildUncleanWarning(PreviousRunResult previousRun)
    {
        if (previousRun.WasClean)
            return null;

        return previousRun.LastKnownUtc is { } lastKnown
            ? $"previous gateway run terminated uncleanly (last known alive: "
              + $"{lastKnown.ToString("o", CultureInfo.InvariantCulture)}, accurate to within "
              + $"{DefaultLivenessRefreshInterval})"
            : "previous gateway run terminated uncleanly (no liveness stamp available)";
    }

    /// <summary>
    /// Clears the marker for the current run. Called immediately after
    /// <see cref="DetectPreviousRun"/> so that any subsequent hard exit leaves no marker and
    /// is therefore detectable as unclean on the following boot.
    /// </summary>
    public void MarkRunning()
    {
        try
        {
            if (_fileSystem.File.Exists(_markerPath))
                _fileSystem.File.Delete(_markerPath);
            if (_fileSystem.File.Exists(_plannedPath))
                _fileSystem.File.Delete(_plannedPath);
            // Seed the liveness stamp immediately so even a crash seconds into boot reports a
            // real instant instead of leaving the previous run's stamp in place.
            RefreshLiveness();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - a leftover marker only risks a missed unclean-death warning.
        }
    }

    /// <summary>
    /// Records a graceful shutdown of unknown provenance at the current UTC time.
    /// </summary>
    public void MarkCleanShutdown() => MarkCleanShutdown(DateTimeOffset.UtcNow);

    /// <summary>
    /// Overload accepting an explicit timestamp; used by tests and callers that already hold
    /// the shutdown time.
    /// </summary>
    public void MarkCleanShutdown(DateTimeOffset timestampUtc)
        => MarkCleanShutdown(timestampUtc, ShutdownSource.Unknown, reason: null);

    /// <summary>
    /// Records a graceful shutdown attributed to <paramref name="source"/> at the current UTC time.
    /// </summary>
    /// <param name="source">Who initiated the stop.</param>
    /// <param name="reason">Optional free-text reason, recorded verbatim.</param>
    public void MarkCleanShutdown(ShutdownSource source, string? reason = null)
        => MarkCleanShutdown(DateTimeOffset.UtcNow, source, reason);

    /// <summary>
    /// Records a graceful shutdown by writing a small JSON marker containing the timestamp, the
    /// initiating source and an optional reason, creating the data directory if necessary.
    /// Never throws.
    /// </summary>
    /// <param name="timestampUtc">Shutdown timestamp to record.</param>
    /// <param name="source">Who initiated the stop.</param>
    /// <param name="reason">Optional free-text reason, recorded verbatim.</param>
    public void MarkCleanShutdown(DateTimeOffset timestampUtc, ShutdownSource source, string? reason)
    {
        try
        {
            if (!_fileSystem.Directory.Exists(_dataDirectory))
                _fileSystem.Directory.CreateDirectory(_dataDirectory);

            var payload = new MarkerPayload
            {
                Ts = timestampUtc.ToString("o", CultureInfo.InvariantCulture),
                Source = source.ToWireName(),
                Reason = reason
            };

            _fileSystem.File.WriteAllText(_markerPath, JsonSerializer.Serialize(payload, MarkerJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Best effort - inability to write the marker only risks a false unclean warning.
        }
    }

    /// <summary>On-disk shape of the marker file.</summary>
    private sealed class MarkerPayload
    {
        public string? Ts { get; init; }
        public string? Source { get; init; }
        public string? Reason { get; init; }
    }
}
