namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Attaches <see cref="CrashRecordWriter"/> to the process fault and teardown events so that every
/// way a gateway run can end leaves a durable, structured record on disk.
/// <para>
/// <b>Why this is a separate installer.</b> <see cref="LastChanceFaultHandler"/> already listens to
/// the same events and writes an <c>[FTL]</c> log breadcrumb. It is not extended here on purpose:
/// it is a proven, deliberately minimal component whose entire value is that it cannot fail, and
/// bolting file I/O into it would put a disk write on the one path that must still work when the
/// disk is the problem. Registering a second, independent set of handlers means a failure in
/// record-writing cannot suppress the breadcrumb, and vice versa — two independent chances to
/// explain the death rather than one shared point of failure.
/// </para>
/// <para>
/// Attribution follows the same first-write-wins rule as the shutdown marker: an unhandled
/// exception describes the death better than the <c>ProcessExit</c> that follows it, and
/// <see cref="CrashRecordWriter"/> keeps whichever arrives first.
/// </para>
/// </summary>
public sealed class CrashRecordFaultInstaller
{
    private readonly CrashRecordWriter _writer;

    /// <summary>Creates an installer writing through <paramref name="writer"/>.</summary>
    /// <param name="writer">Destination writer; its first-write-wins latch does the arbitration.</param>
    public CrashRecordFaultInstaller(CrashRecordWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <summary>
    /// Registers handlers for <see cref="AppDomain.UnhandledException"/> and
    /// <see cref="AppDomain.ProcessExit"/>. Never throws.
    /// </summary>
    public void Install()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        catch
        {
            // If we cannot even subscribe, the gateway still runs; it just stays as blind as before.
        }
    }

    /// <summary>
    /// Records a clean, attributed shutdown. Exposed so shutdown callbacks can attribute the stop
    /// before <c>ProcessExit</c> claims it as merely "unknown". Never throws.
    /// </summary>
    /// <param name="source">Who initiated the stop.</param>
    /// <param name="reason">Optional free-text reason.</param>
    public void RecordCleanShutdown(ShutdownSource source, string? reason = null)
    {
        try
        {
            _writer.WriteClean(source, reason);
        }
        catch
        {
            // Never propagate out of a shutdown callback.
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            _writer.WriteCrash(
                faultReason: ex is null ? "UnhandledException" : $"UnhandledException: {ex.GetType().Name}",
                stack: ex?.ToString() ?? e.ExceptionObject?.ToString());
        }
        catch
        {
            // A fault handler must never throw.
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            // Only lands if nothing better-attributed already wrote: an orderly exit that no
            // shutdown path claimed is genuinely of unknown provenance, and saying so is more
            // honest than labelling it clean.
            _writer.WriteUnknown("process exit");
        }
        catch
        {
            // Never throw from ProcessExit.
        }
    }
}
