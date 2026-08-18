using System.Reflection;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Build identity (version and commit SHA) resolved once and cached, for stamping onto crash
/// records.
/// <para>
/// Deliberately reflection-only. The gateway's <c>/api/version</c> endpoint shells out to
/// <c>git rev-parse</c>, which is fine for a request handler but unacceptable here: a crash record
/// is written from a dying process, and spawning a child process at that moment can block on a
/// wedged filesystem, fail because the working directory has gone, or simply not complete before
/// the runtime tears the process down. Reading attributes off an already-loaded assembly cannot
/// block and cannot fail in a way that matters.
/// </para>
/// <para>
/// Values are captured in a static constructor and are therefore resolved at most once per process.
/// Call <see cref="Warmup"/> during startup so that even the reflection happens while the process
/// is healthy rather than mid-fault.
/// </para>
/// </summary>
public static class CrashRecordBuildInfo
{
    /// <summary>Informational (or assembly) version of the running build; null when unresolvable.</summary>
    public static string? Version { get; }

    /// <summary>Git commit SHA recorded in assembly metadata; null when the build did not stamp one.</summary>
    public static string? CommitSha { get; }

    static CrashRecordBuildInfo()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(CrashRecordBuildInfo).Assembly;

            Version =
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString();

            // SourceRevisionId is appended to the informational version by the SDK as "+<sha>";
            // an explicit AssemblyMetadata entry is preferred when the build stamped one.
            CommitSha = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a =>
                    string.Equals(a.Key, "CommitSha", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Key, "GitHash", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Key, "SourceRevisionId", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            if (string.IsNullOrWhiteSpace(CommitSha) && Version is { Length: > 0 })
            {
                var plus = Version.IndexOf('+');
                if (plus >= 0 && plus < Version.Length - 1)
                    CommitSha = Version[(plus + 1)..];
            }

            if (string.IsNullOrWhiteSpace(CommitSha) || CommitSha == "unknown")
                CommitSha = null;
        }
        catch
        {
            // Build identity is a nicety on a post-mortem record. Never let resolving it throw.
            Version ??= null;
            CommitSha ??= null;
        }
    }

    /// <summary>
    /// Forces the static constructor to run now, so the reflection cost and any failure happen
    /// during healthy startup rather than inside a fault handler. Cheap and idempotent.
    /// </summary>
    public static void Warmup() => _ = Version;
}
