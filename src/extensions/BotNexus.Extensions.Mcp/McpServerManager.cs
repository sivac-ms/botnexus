using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Mcp.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Manages multiple MCP server connections and provides bridged tools for an agent session.
/// </summary>
public sealed class McpServerManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpServerManager"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostics. If null, uses NullLogger.</param>
    public McpServerManager(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Starts all configured MCP servers, initializes them, and returns bridged tools.
    /// Failures on individual servers are logged as warnings and skipped; successful servers continue.
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> StartServersAsync(
        McpExtensionConfig config,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Start every server concurrently. Servers are independent stdio/HTTP processes, and a
        // sequential loop let one slow server (e.g. a 90s init timeout) delay every server after it
        // and push the whole warmup past the agent-handle wait. StartSingleServerAsync carries the
        // same per-server timeout, skip-and-warn handling and locked client registration.
        var servers = config.Servers.ToList();
        var starts = servers
            .Select(server => StartServerIsolatedAsync(server.Key, server.Value, config.ToolPrefix, ct))
            .ToArray();
        var results = await Task.WhenAll(starts).ConfigureAwait(false);

        // Flatten in configuration order so the tool list is deterministic regardless of which
        // server finished first.
        var tools = new List<IAgentTool>();
        foreach (var serverTools in results)
        {
            tools.AddRange(serverTools);
        }

        return tools;
    }

    private async Task<IReadOnlyList<IAgentTool>> StartServerIsolatedAsync(
        string serverId,
        McpServerConfig serverConfig,
        bool useToolPrefix,
        CancellationToken ct)
    {
        try
        {
            return await StartSingleServerAsync(serverId, serverConfig, useToolPrefix, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // One server's unexpected failure (transport creation, ListTools, ...) must never fault
            // the others or the warmup as a whole.
            _logger.LogWarning(
                ex,
                "Failed to start MCP server '{ServerId}': {ErrorMessage}. Skipping server.",
                serverId,
                ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Starts a single MCP server, initializes it, and returns bridged tools.
    /// Thread-safe — can be called concurrently for multiple servers.
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> StartSingleServerAsync(
        string serverId,
        McpServerConfig serverConfig,
        bool useToolPrefix = true,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryCreateTransport(serverConfig, out var transport, out var transportError))
        {
            _logger.LogWarning(
                "MCP server '{ServerId}' has an insecure or invalid url: {ErrorMessage} Skipping server.",
                serverId,
                transportError);
            return [];
        }

        if (transport is null)
            return [];

        var client = new McpClient(transport, serverId, _logger);

        using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initCts.CancelAfter(serverConfig.InitTimeoutMs);

        try
        {
            await client.InitializeAsync(initCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning(
                "MCP server '{ServerId}' did not initialize within {TimeoutMs}ms. Skipping server.",
                serverId, serverConfig.InitTimeoutMs);
            return [];
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning(ex,
                "MCP server '{ServerId}' failed to initialize: {ErrorMessage}. Skipping server.",
                serverId, ex.Message);
            return [];
        }

        lock (_clients)
        {
            _clients.Add(client);
        }

        var mcpTools = await client.ListToolsAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "MCP server '{ServerId}' initialized successfully with {ToolCount} tool(s).",
            serverId, mcpTools.Count);

        return mcpTools.Select(t => (IAgentTool)new McpBridgedTool(client, t, useToolPrefix)).ToList();
    }

    /// <summary>
    /// Returns all bridged tools across all connected servers.
    /// </summary>
    public IReadOnlyList<McpClient> GetClients()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _clients;
    }

    /// <summary>
    /// Gracefully stops and drops the connections whose server id satisfies <paramref name="predicate"/>.
    /// </summary>
    /// <remarks>
    /// Selective teardown exists so an owner that registered a subset of servers - a plugin, for
    /// instance - can retire exactly its own without stopping servers it does not own. Matching is
    /// by the id the client was registered under, which is the only identity the manager holds.
    /// </remarks>
    /// <param name="predicate">Selects server ids to stop.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The server ids that were actually stopped.</returns>
    public async Task<IReadOnlyList<string>> StopServersAsync(
        Func<string, bool> predicate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        List<McpClient> matched;
        lock (_clients)
        {
            matched = _clients.Where(c => predicate(c.ServerId)).ToList();
            foreach (var client in matched)
            {
                _clients.Remove(client);
            }
        }

        var stopped = new List<string>(matched.Count);
        foreach (var client in matched)
        {
            stopped.Add(client.ServerId);
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup — the client is already off the roster either way, so a
                // faulting dispose must not leave a stopped server still looking registered.
            }
        }

        return stopped;
    }

    /// <summary>
    /// Gracefully stops all MCP server connections.
    /// </summary>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _clients.Clear();
    }

    /// <summary>
    /// Creates the appropriate transport for a server configuration.
    /// Returns null if neither command nor URL is configured, or if the URL fails
    /// <see cref="McpUrlSecurity"/> validation.
    /// </summary>
    public static IMcpTransport? CreateTransport(McpServerConfig serverConfig)
        => TryCreateTransport(serverConfig, out var transport, out _) ? transport : null;

    /// <summary>
    /// Creates the appropriate transport for a server configuration, reporting why the URL was
    /// rejected when validation fails so the caller can log a warning naming the server id.
    /// </summary>
    /// <param name="serverConfig">The server configuration.</param>
    /// <param name="transport">The created transport, or <c>null</c>.</param>
    /// <param name="error">The rejection reason when a URL was configured but refused.</param>
    /// <returns><c>false</c> only when a configured URL was rejected by validation.</returns>
    public static bool TryCreateTransport(
        McpServerConfig serverConfig,
        out IMcpTransport? transport,
        out string? error)
    {
        transport = null;
        error = null;

        if (!string.IsNullOrWhiteSpace(serverConfig.Url))
        {
            // A transport built here carries whatever headers the caller merged in, which for the
            // auth path includes a resolved provider API key. Validate the scheme at the single
            // seam before any credential can leave the process (#3012).
            var carriesCredentials =
                McpUrlSecurity.HeadersCarryCredentials(serverConfig.Headers)
                || !string.IsNullOrWhiteSpace(serverConfig.Auth);

            if (!McpUrlSecurity.TryValidate(serverConfig.Url, carriesCredentials, out var endpoint, out error))
                return false;

            transport = new HttpSseMcpTransport(endpoint!, serverConfig.Headers?.AsReadOnly());
            return true;
        }

        if (!string.IsNullOrWhiteSpace(serverConfig.Command))
        {
            transport = new StdioMcpTransport(
                serverConfig.Command,
                serverConfig.Args,
                serverConfig.Env,
                serverConfig.WorkingDirectory,
                serverConfig.InheritEnv);
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAllAsync().ConfigureAwait(false);
    }
}
