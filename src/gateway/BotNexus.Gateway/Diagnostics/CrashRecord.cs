using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// One durable record of how a gateway run ended, written to
/// <c>{data}/state/crashes/&lt;utc-ts&gt;.json</c>.
/// <para>
/// Every field is optional except <see cref="SchemaVersion"/>, <see cref="Ts"/> and
/// <see cref="Kind"/>: a record is written from a dying process where the DI container, the session
/// store and even the managed heap may be unusable, so any probe that fails simply leaves its field
/// null. A partial record is enormously more useful than no record.
/// </para>
/// </summary>
public sealed class CrashRecord
{
    /// <summary>Schema version of this record; bumped when fields change meaning.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>UTC timestamp the record was captured, round-trip format.</summary>
    [JsonPropertyName("ts")]
    public required string Ts { get; init; }

    /// <summary>How the run ended: <c>clean</c>, <c>crash</c>, or <c>unknown</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Process exit code if known at capture time.</summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    /// <summary>Short machine-readable fault reason (e.g. <c>UnhandledException</c>).</summary>
    [JsonPropertyName("faultReason")]
    public string? FaultReason { get; init; }

    /// <summary>Exception detail / stack trace, collapsed to a single string. May be null.</summary>
    [JsonPropertyName("stack")]
    public string? Stack { get; init; }

    /// <summary>Seconds the process had been running when it ended.</summary>
    [JsonPropertyName("uptimeSeconds")]
    public double? UptimeSeconds { get; init; }

    /// <summary>Git commit the running build was produced from, if known.</summary>
    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; init; }

    /// <summary>Assembly version of the running build.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Last log lines observed before the end (most recent last), capped by the writer.</summary>
    [JsonPropertyName("lastLogLines")]
    public IReadOnlyList<string> LastLogLines { get; init; } = [];

    /// <summary>Managed working set in megabytes at capture time.</summary>
    [JsonPropertyName("memoryMb")]
    public long? MemoryMb { get; init; }

    /// <summary>Thread-pool pending work item count at capture time.</summary>
    [JsonPropertyName("threadPoolPending")]
    public long? ThreadPoolPending { get; init; }

    /// <summary>Active session count if the session subsystem was reachable.</summary>
    [JsonPropertyName("activeSessions")]
    public int? ActiveSessions { get; init; }

    /// <summary>Wire name of the shutdown source, for clean records. Null for crashes.</summary>
    [JsonPropertyName("initiatedBy")]
    public string? InitiatedBy { get; init; }
}

/// <summary>Well-known values for <see cref="CrashRecord.Kind"/>.</summary>
public static class CrashRecordKinds
{
    /// <summary>The run ended through a graceful, attributed shutdown.</summary>
    public const string Clean = "clean";

    /// <summary>The run ended through a fault (unhandled exception / abrupt exit).</summary>
    public const string Crash = "crash";

    /// <summary>The run's ending could not be classified.</summary>
    public const string Unknown = "unknown";
}
