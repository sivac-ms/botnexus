using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Optional, individually-failable probes the crash-record writer queries at capture time.
/// <para>
/// Each is a <see cref="Func{TResult}"/> rather than a value because a crash record is written
/// from a dying process: the DI container may already be disposed and any probe may throw. They are
/// invoked lazily, one at a time, each inside its own try/catch, so one dead subsystem costs one
/// null field instead of the whole record.
/// </para>
/// </summary>
/// <param name="ActiveSessionCount">Returns the active session count, or null when unavailable.</param>
/// <param name="RecentLogLines">Returns the most recent log lines, oldest first.</param>
/// <param name="ProcessStartedAtUtc">When the current run began, used to compute uptime.</param>
public sealed record CrashRecordProbes(
    Func<int?>? ActiveSessionCount = null,
    Func<IReadOnlyList<string>>? RecentLogLines = null,
    DateTimeOffset? ProcessStartedAtUtc = null);

/// <summary>
/// Writes one <see cref="CrashRecord"/> per gateway run-ending to
/// <c>{data}/state/crashes/&lt;utc-ts&gt;.json</c>, and prunes the directory so an unattended host
/// cannot accumulate crash records without bound.
/// <para>
/// <b>Why this exists.</b> Before this, the only thing a boot could say about the previous run was
/// a single "terminated uncleanly" warning with no timestamp — it knew <i>that</i> the gateway died
/// but nothing about <i>why</i>. <see cref="CrashRecord"/> already described the shape of the
/// answer, but nothing in the repository constructed or wrote one, so the shape was never filled
/// in. This class is the writer.
/// </para>
/// <para>
/// <b>Discipline.</b> Every public method is total: it catches everything, it never throws, and it
/// never blocks on anything unbounded. It is called from
/// <see cref="LastChanceFaultHandler"/> and from shutdown callbacks — paths on which an exception
/// or a hang would convert a diagnosable crash into a silent one, which is precisely the failure
/// this code was written to remove. Writes are first-write-wins per process so a crash that both
/// throws and exits leaves one record, not two.
/// </para>
/// </summary>
public sealed class CrashRecordWriter
{
    /// <summary>Directory name, under the data root's <c>state</c> folder, holding crash records.</summary>
    public const string CrashesDirectoryName = "crashes";

    /// <summary>Maximum number of records retained; the oldest beyond this are pruned.</summary>
    public const int MaxRecords = 50;

    /// <summary>Maximum total bytes retained across all records; oldest are pruned to fit.</summary>
    public const long MaxTotalBytes = 10L * 1024 * 1024;

    /// <summary>Maximum log lines embedded in a record.</summary>
    public const int MaxLogLines = 50;

    /// <summary>Maximum characters of stack text embedded in a record.</summary>
    public const int MaxStackLength = 16 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IFileSystem _fileSystem;
    private readonly string _crashesDirectory;
    private readonly CrashRecordProbes _probes;
    private int _written;

    /// <summary>
    /// Creates a writer rooted at <paramref name="dataDirectory"/> (the writable BotNexus data
    /// directory). Records land in <c>{dataDirectory}/state/crashes</c>.
    /// </summary>
    /// <param name="fileSystem">Filesystem abstraction, so the writer is testable without real I/O.</param>
    /// <param name="dataDirectory">Writable BotNexus data root.</param>
    /// <param name="probes">Optional context probes; omitted probes simply leave fields null.</param>
    public CrashRecordWriter(IFileSystem fileSystem, string dataDirectory, CrashRecordProbes? probes = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _fileSystem = fileSystem;
        _crashesDirectory = fileSystem.Path.Combine(dataDirectory, "state", CrashesDirectoryName);
        _probes = probes ?? new CrashRecordProbes();
    }

    /// <summary>Absolute path of the crash-record directory, exposed for logging and tests.</summary>
    public string CrashesDirectory => _crashesDirectory;

    /// <summary><c>true</c> once this process has written a record through any path.</summary>
    public bool HasWritten => Volatile.Read(ref _written) != 0;

    /// <summary>
    /// Records a fault-terminated run. Intended to be called from the last-chance fault handler.
    /// Returns the path written, or null when nothing was written (already written, or the write
    /// failed). Never throws.
    /// </summary>
    /// <param name="faultReason">Short machine-readable reason, e.g. <c>UnhandledException</c>.</param>
    /// <param name="stack">Exception detail / stack trace, truncated on write.</param>
    /// <param name="exitCode">Process exit code if known.</param>
    public string? WriteCrash(string faultReason, string? stack, int? exitCode = null)
        => Write(CrashRecordKinds.Crash, faultReason, stack, exitCode, initiatedBy: null);

    /// <summary>
    /// Records an attributed graceful shutdown. Returns the path written, or null when nothing was
    /// written. Never throws.
    /// </summary>
    /// <param name="source">Who initiated the stop.</param>
    /// <param name="reason">Optional free-text reason recorded as the fault reason field.</param>
    public string? WriteClean(ShutdownSource source, string? reason = null)
        => Write(CrashRecordKinds.Clean, reason, stack: null, exitCode: SafeExitCode(), initiatedBy: source.ToWireName());

    /// <summary>
    /// Records a run-ending that could not be classified — an orderly process exit that no better
    /// attributed path claimed first. Never throws.
    /// </summary>
    /// <param name="reason">Optional free-text reason.</param>
    public string? WriteUnknown(string? reason = null)
        => Write(CrashRecordKinds.Unknown, reason, stack: null, exitCode: SafeExitCode(), initiatedBy: null);

    /// <summary>
    /// Core writer. Internal rather than private so the record's shape and the first-write-wins
    /// latch are directly unit-testable without provoking a real process fault.
    /// </summary>
    internal string? Write(string kind, string? faultReason, string? stack, int? exitCode, string? initiatedBy)
    {
        // First write wins. A crash frequently trips several handlers (unhandled exception, then
        // ProcessExit); the first carries the real cause and later ones would only overwrite it
        // with a less informative "process exited" record.
        if (Interlocked.Exchange(ref _written, 1) != 0)
            return null;

        try
        {
            var now = DateTimeOffset.UtcNow;

            var record = new CrashRecord
            {
                Ts = now.ToString("o", CultureInfo.InvariantCulture),
                Kind = kind,
                ExitCode = exitCode,
                FaultReason = faultReason,
                Stack = Truncate(stack, MaxStackLength),
                UptimeSeconds = SafeUptimeSeconds(now),
                CommitSha = CrashRecordBuildInfo.CommitSha,
                Version = CrashRecordBuildInfo.Version,
                LastLogLines = SafeLogLines(),
                MemoryMb = SafeMemoryMb(),
                ThreadPoolPending = SafeThreadPoolPending(),
                ActiveSessions = SafeProbe(_probes.ActiveSessionCount),
                InitiatedBy = initiatedBy
            };

            if (!_fileSystem.Directory.Exists(_crashesDirectory))
                _fileSystem.Directory.CreateDirectory(_crashesDirectory);

            // Colons are illegal in Windows filenames, so the round-trip timestamp is flattened.
            var fileName = string.Concat(now.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture), ".json");
            var path = _fileSystem.Path.Combine(_crashesDirectory, fileName);

            _fileSystem.File.WriteAllText(path, JsonSerializer.Serialize(record, WriteOptions));

            Prune();
            return path;
        }
        catch
        {
            // A post-mortem writer that throws during a crash replaces one silent death with
            // another. Losing the record is bad; losing the ability to diagnose why is worse.
            return null;
        }
    }

    /// <summary>
    /// Enforces the retention cap: at most <see cref="MaxRecords"/> files and
    /// <see cref="MaxTotalBytes"/> total, deleting oldest-first. Never throws.
    /// </summary>
    internal void Prune()
    {
        try
        {
            if (!_fileSystem.Directory.Exists(_crashesDirectory))
                return;

            // Ordered by name, which is the flattened UTC timestamp, so lexical order is
            // chronological order — no per-file metadata stat is needed to sort.
            var files = _fileSystem.Directory
                .GetFiles(_crashesDirectory, "*.json")
                .OrderBy(static f => f, StringComparer.Ordinal)
                .ToList();

            var sizes = new long[files.Count];
            long total = 0;
            for (var i = 0; i < files.Count; i++)
            {
                try
                {
                    sizes[i] = _fileSystem.FileInfo.New(files[i]).Length;
                }
                catch
                {
                    sizes[i] = 0;
                }

                total += sizes[i];
            }

            var index = 0;
            var remaining = files.Count;
            while (index < files.Count && (remaining > MaxRecords || total > MaxTotalBytes))
            {
                try
                {
                    _fileSystem.File.Delete(files[index]);
                }
                catch
                {
                    // A locked or already-removed file is not worth failing the prune over.
                }

                total -= sizes[index];
                remaining--;
                index++;
            }
        }
        catch
        {
            // Pruning is housekeeping. Failing it must never cost us the record we just wrote.
        }
    }

    private IReadOnlyList<string> SafeLogLines()
    {
        try
        {
            var lines = _probes.RecentLogLines?.Invoke();
            if (lines is null || lines.Count == 0)
                return [];

            return lines.Count <= MaxLogLines
                ? lines
                : lines.Skip(lines.Count - MaxLogLines).ToList();
        }
        catch
        {
            return [];
        }
    }

    private double? SafeUptimeSeconds(DateTimeOffset now)
    {
        try
        {
            var started = _probes.ProcessStartedAtUtc;
            if (started is null)
                return null;

            var seconds = (now - started.Value).TotalSeconds;
            return seconds >= 0 ? Math.Round(seconds, 3) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? SafeProbe(Func<int?>? probe)
    {
        if (probe is null)
            return null;
        try
        {
            return probe();
        }
        catch
        {
            return null;
        }
    }

    private static int? SafeExitCode()
    {
        try
        {
            return Environment.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static long? SafeMemoryMb()
    {
        try
        {
            return Environment.WorkingSet / (1024 * 1024);
        }
        catch
        {
            return null;
        }
    }

    private static long? SafeThreadPoolPending()
    {
        try
        {
            return ThreadPool.PendingWorkItemCount;
        }
        catch
        {
            return null;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return string.Concat(value.AsSpan(0, maxLength), "\n… (truncated)");
    }
}
