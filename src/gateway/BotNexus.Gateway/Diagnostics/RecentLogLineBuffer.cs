using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// A fixed-capacity ring of the most recent rendered log lines, kept purely so that a crash
/// record can answer "what was the gateway saying immediately before it died".
/// <para>
/// This is deliberately <i>not</i> <see cref="LogDiagnosticsRingBuffer"/>. That buffer aggregates
/// Warning+ entries by message-template fingerprint, which is the right shape for "what keeps going
/// wrong" but the wrong shape for post-mortem: it discards ordering, discards Information-level
/// context, and collapses repeats. A crash is usually explained by the ordinary lines around it,
/// not by the deduplicated warning population, so this keeps raw lines in order instead.
/// </para>
/// <para>
/// The ring is pre-allocated once and never grows: a diagnostics buffer that allocates
/// proportionally to log volume would itself become a memory-pressure fault, which is one of the
/// failures it exists to explain. Writes take a short lock over an array slot assignment; reads
/// copy out under the same lock. Nothing here throws.
/// </para>
/// </summary>
public sealed class RecentLogLineBuffer
{
    /// <summary>Default number of lines retained, matching the crash-record schema's cap of 50.</summary>
    public const int DefaultCapacity = 50;

    /// <summary>Maximum characters retained per line; longer lines are truncated.</summary>
    public const int MaxLineLength = 512;

    private readonly string?[] _lines;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    /// <summary>Creates a ring retaining at most <paramref name="capacity"/> lines.</summary>
    /// <param name="capacity">Number of lines to retain. Values below 1 are clamped to 1.</param>
    public RecentLogLineBuffer(int capacity = DefaultCapacity)
    {
        _lines = new string?[Math.Max(1, capacity)];
    }

    /// <summary>Number of lines currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>
    /// Appends one already-rendered log line. Never throws; a diagnostics buffer that could throw
    /// from inside a logger would turn every log call into a potential fault.
    /// </summary>
    /// <param name="level">Severity, prefixed onto the retained line.</param>
    /// <param name="message">Rendered message text.</param>
    public void Record(LogLevel level, string? message)
    {
        try
        {
            var text = message ?? string.Empty;
            if (text.Length > MaxLineLength)
                text = string.Concat(text.AsSpan(0, MaxLineLength), "…");

            var line = string.Concat(
                DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff"),
                " [",
                Abbreviate(level),
                "] ",
                text);

            lock (_gate)
            {
                _lines[_next] = line;
                _next = (_next + 1) % _lines.Length;
                if (_count < _lines.Length)
                    _count++;
            }
        }
        catch
        {
            // Losing a breadcrumb line is always preferable to destabilising the logging path.
        }
    }

    /// <summary>
    /// Returns the retained lines oldest-first. Safe to call from a fault handler: it allocates a
    /// single small list and never throws.
    /// </summary>
    public IReadOnlyList<string> Snapshot()
    {
        try
        {
            lock (_gate)
            {
                if (_count == 0)
                    return [];

                var result = new List<string>(_count);
                var start = _count < _lines.Length ? 0 : _next;
                for (var i = 0; i < _count; i++)
                {
                    var line = _lines[(start + i) % _lines.Length];
                    if (line is not null)
                        result.Add(line);
                }

                return result;
            }
        }
        catch
        {
            return [];
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "FTL",
        _ => "___"
    };
}

/// <summary>
/// <see cref="ILoggerProvider"/> that feeds <see cref="RecentLogLineBuffer"/>.
/// <para>
/// Registered separately from <c>LogDiagnosticsProvider</c> rather than folded into it, because the
/// two want different minimum levels. Raising that provider's threshold to Information would change
/// the population of the log-patterns API — an observable behaviour change to an existing endpoint —
/// so the new capture is a new provider instead, and the existing one is left exactly as it was.
/// </para>
/// </summary>
[ProviderAlias("RecentLogLines")]
public sealed class RecentLogLineProvider : ILoggerProvider
{
    private readonly RecentLogLineBuffer _buffer;
    private readonly LogLevel _minimumLevel;

    /// <summary>Creates a provider writing into <paramref name="buffer"/>.</summary>
    /// <param name="buffer">Destination ring buffer.</param>
    public RecentLogLineProvider(RecentLogLineBuffer buffer)
        : this(buffer, LogLevel.Information)
    {
    }

    /// <summary>Creates a provider with an explicit minimum level (used by tests).</summary>
    /// <param name="buffer">Destination ring buffer.</param>
    /// <param name="minimumLevel">Lowest severity captured.</param>
    public RecentLogLineProvider(RecentLogLineBuffer buffer, LogLevel minimumLevel)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
        _minimumLevel = minimumLevel;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RecentLogLineLogger(_buffer, _minimumLevel);

    /// <inheritdoc />
    public void Dispose()
    {
        // The ring buffer is owned by the DI container; there is nothing else to release.
    }
}

internal sealed class RecentLogLineLogger(RecentLogLineBuffer buffer, LogLevel minimumLevel) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        try
        {
            buffer.Record(logLevel, formatter(state, exception));
        }
        catch
        {
            // Never let breadcrumb capture surface as a logging failure.
        }
    }
}
