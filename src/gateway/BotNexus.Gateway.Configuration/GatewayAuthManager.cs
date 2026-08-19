using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Abstractions;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Core;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Resolves provider API keys for Gateway-hosted agents.
/// </summary>
public sealed class GatewayAuthManager
{
    private const string AuthFileName = "auth.json";

    /// <summary>
    /// How far ahead of expiry a stored OAuth credential is renewed, in milliseconds. This is the
    /// margin that actually governs the gateway refresh path (<see cref="NeedsRefresh"/>); it
    /// mirrors <see cref="CopilotOAuth.RefreshMarginSeconds"/>. The original 60s was a cliff: on
    /// 2026-08-17 GitHub returned 502s for ~18 minutes and the first failed refresh left the turn
    /// with no key almost immediately. Ten minutes gives the retrying exchange a wide window to
    /// succeed before the credential genuinely lapses (#2977).
    /// </summary>
    private const long RefreshMarginMs = CopilotOAuth.RefreshMarginSeconds * 1000;
    private readonly IOptionsMonitor<PlatformConfig> _platformConfig;
    private readonly ILogger<GatewayAuthManager> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly string _authFilePath;
    private readonly string _legacyAuthFilePath;
    private readonly IProviderHealthObserver _healthObserver;

    /// <summary>
    /// The credential-refresh call, indirected so that a test can drive the upstream-failure path.
    ///
    /// <para>
    /// The refresh goes through a static <c>CopilotOAuth</c> call, which cannot be made to return a
    /// 502/503 from a test. Without this seam the entire refresh-failure branch - the branch the
    /// whole of #3281 is about - would be unreachable by any test, and its coverage would be a claim
    /// rather than a demonstration. Production wiring is unchanged: the default is the real call.
    /// </para>
    /// </summary>
    private readonly Func<AuthEntry, CancellationToken, Task<AuthEntry>> _refreshEntry;
    private readonly object _sync = new();
    private Dictionary<string, AuthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    /// <summary>
    /// #3673: the observed on-disk state of the auth files at the moment the cache was populated.
    ///
    /// <para>
    /// The cache used to be a one-shot latch (<see cref="_loaded"/> set once, never reset), so a
    /// credential written <b>out of process</b> - which is exactly what <c>botnexus provider login</c>
    /// does - was invisible to a running gateway. It kept serving the revoked token and every call
    /// failed <c>HTTP 403</c> until someone restarted the daemon. Recording the files' last-write
    /// stamp and size lets the next resolution notice the rewrite and re-read.
    /// </para>
    /// <para>
    /// A stat is deliberately used rather than a read: the steady state must not perform disk I/O on
    /// every credential resolution, and an unchanged stamp is sufficient evidence that the cached
    /// entries still match the file.
    /// </para>
    /// </summary>
    private string? _loadedSignature;
    /// Non-null when a candidate auth file existed but could not be read or parsed. Distinct from
    /// "no file" so callers can report a credential failure instead of "not configured".
    /// </summary>
    private Exception? _loadFailure;

    /// <summary>
    /// Non-null when ANY candidate store was unreadable, even if another store parsed fine. A HIT
    /// proves a credential regardless of this; a MISS while this is set proves nothing, so the
    /// health surface must report unknown-and-actionable rather than "not configured".
    /// </summary>
    private Exception? _partialLoadFailure;

    /// <summary>
    /// Wall-clock stamp of the last actual store read attempt. Used to enforce the re-read floor
    /// (<see cref="LoadRetryFloor"/>) while <see cref="_loaded"/> stays unlatched because a store
    /// is unreadable.
    /// </summary>
    private DateTimeOffset? _lastLoadAttemptUtc;

    /// <summary>
    /// Minimum interval between re-reads of the auth stores while a load failure is outstanding.
    /// Deliberately keeps the non-latching retry (transient corruption must self-heal) but stops
    /// every credential resolution from doing synchronous disk IO inside the lock: with a broken
    /// store and a fan-out dashboard that degenerated into a lock convoy that starved the
    /// threadpool the dashboard then reported as starved.
    /// </summary>
    private static readonly TimeSpan LoadRetryFloor = TimeSpan.FromSeconds(30);

    public GatewayAuthManager(IOptionsMonitor<PlatformConfig> platformConfig, ILogger<GatewayAuthManager> logger, IFileSystem fileSystem)
        : this(platformConfig, logger, fileSystem, NullProviderHealthObserver.Instance)
    {
    }

    /// <summary>
    /// Creates an auth manager that reports credential outcomes to a health observer (#3281).
    /// The observer receives every refresh attempt so that repeated upstream failures can become a
    /// <c>health.degraded</c> event instead of a log line nobody sees.
    /// </summary>
    public GatewayAuthManager(
        IOptionsMonitor<PlatformConfig> platformConfig,
        ILogger<GatewayAuthManager> logger,
        IFileSystem fileSystem,
        IProviderHealthObserver healthObserver)
        : this(platformConfig, logger, fileSystem, healthObserver, RefreshEntryAsync)
    {
    }

    /// <summary>
    /// Test-facing constructor that also substitutes the credential-refresh call so the
    /// upstream-failure branch can be exercised deterministically (#3281).
    /// </summary>
    internal GatewayAuthManager(
        IOptionsMonitor<PlatformConfig> platformConfig,
        ILogger<GatewayAuthManager> logger,
        IFileSystem fileSystem,
        IProviderHealthObserver healthObserver,
        Func<AuthEntry, CancellationToken, Task<AuthEntry>> refreshEntry)
    {
        _refreshEntry = refreshEntry ?? throw new ArgumentNullException(nameof(refreshEntry));
        _platformConfig = platformConfig;
        _logger = logger;
        _fileSystem = fileSystem;
        _healthObserver = healthObserver ?? NullProviderHealthObserver.Instance;
        _authFilePath = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(_fileSystem), AuthFileName);
        _legacyAuthFilePath = Path.Combine(Environment.CurrentDirectory, ".botnexus-agent", AuthFileName);
    }

    /// <summary>
    /// Resolves a provider credential and reports why the attempt ended as it did (#3281).
    ///
    /// <para>
    /// Prefer this over <see cref="GetApiKeyAsync"/> when the caller needs to distinguish an upstream
    /// outage from a provider that was simply never configured. <see cref="GetApiKeyAsync"/> remains
    /// the convenience projection that drops the reason.
    /// </para>
    /// </summary>
    public async Task<ProviderCredentialOutcome> ResolveCredentialAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return ProviderCredentialOutcome.NotConfigured();
        }

        var authOutcome = await ResolveAuthEntryCredentialAsync(provider, cancellationToken).ConfigureAwait(false);
        if (authOutcome.Status != ProviderCredentialStatus.NotConfigured)
        {
            return authOutcome;
        }

        // No auth.json entry, but a key may still be declared in config or the environment. A
        // refresh failure above is returned as-is and never overwritten by a fallback lookup: the
        // fault is the more important fact about the provider's health.
        var fallbackKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(fallbackKey)
            ? ProviderCredentialOutcome.NotConfigured()
            : ProviderCredentialOutcome.Success(fallbackKey);
    }

    /// <summary>
    /// Returns the API endpoint override for a provider from auth.json or platform config.
    /// Used to override model BaseUrl (e.g., enterprise vs individual Copilot endpoints).
    /// </summary>
    public string? GetApiEndpoint(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        if (TryResolveAuthEntry(provider, out var entry, out _) && !string.IsNullOrWhiteSpace(entry.Endpoint))
            return entry.Endpoint;

        if (_platformConfig.CurrentValue.Providers is not null &&
            TryGetProviderConfig(_platformConfig.CurrentValue.Providers, provider, out var providerConfig))
        {
            const string AuthPrefix = "auth:";
            if (providerConfig?.ApiKey?.StartsWith(AuthPrefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                var referenceProvider = providerConfig.ApiKey[AuthPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(referenceProvider) &&
                    TryResolveAuthEntry(referenceProvider, out var referencedEntry, out _) &&
                    !string.IsNullOrWhiteSpace(referencedEntry.Endpoint))
                {
                    return referencedEntry.Endpoint;
                }
            }

            if (!string.IsNullOrWhiteSpace(providerConfig?.BaseUrl))
                return providerConfig.BaseUrl;
        }

        return null;
    }

    /// <summary>
    /// Returns the GitHub OAuth refresh credential for the Copilot account without refreshing it
    /// or exposing the short-lived Copilot session access token. This is for read-only account
    /// discovery endpoints that require the original OAuth credential.
    /// </summary>
    public Task<string?> GetCopilotOAuthTokenAsync(CancellationToken cancellationToken = default)
        => GetCopilotOAuthTokenAsync("github-copilot", cancellationToken);

    /// <summary>Resolves only the selected configured Copilot account; unknown instances never use the default.</summary>
    public Task<string?> GetCopilotOAuthTokenAsync(string instance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ResolveCopilotAccountCredential(instance)?.OAuthToken);
    }

    private readonly byte[] _quotaGenerationKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    /// <summary>Reads local auth state without token exchange. Unsupported authorities are unavailable.</summary>
    public CopilotAccountCredential? ResolveCopilotAccountCredential(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance) || instance.Length > 128) return null;
        var selected = instance.Trim();
        var providers = _platformConfig.CurrentValue.Providers;
        ProviderConfig? config = null;
        if (providers is not null)
        {
            var match = providers.FirstOrDefault(p => string.Equals(p.Key, selected, StringComparison.OrdinalIgnoreCase));
            if (match.Value is null && IsCopilotType(selected))
            {
                var alias = string.Equals(selected, "copilot", StringComparison.OrdinalIgnoreCase) ? "github-copilot" : "copilot";
                match = providers.FirstOrDefault(p => string.Equals(p.Key, alias, StringComparison.OrdinalIgnoreCase));
            }
            if (match.Value is not null) { selected = match.Key; config = match.Value; }
        }
        var canonical = string.Equals(selected, "copilot", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(selected, "github-copilot", StringComparison.OrdinalIgnoreCase);
        if (config is null && !canonical) return null;
        if (config is not null && (!config.Enabled ||
            !IsCopilotType(config.Type ?? selected) || !IsPublicCopilotEndpoint(config.BaseUrl))) return null;
        var authKey = canonical ? "copilot" : selected;
        if (config?.ApiKey is not null)
        {
            if (!config.ApiKey.StartsWith("auth:", StringComparison.OrdinalIgnoreCase)) return null;
            authKey = config.ApiKey[5..].Trim();
            if (authKey.Length == 0) return null;
        }
        LoadAuthEntries();
        if (!TryGetAuthEntry(authKey, out var entry) ||
            !string.Equals(entry.Type, "oauth", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entry.Refresh) || !IsPublicCopilotEndpoint(entry.Endpoint)) return null;
        var scope = canonical ? "github-copilot" : selected.ToLowerInvariant();
        var generation = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            _quotaGenerationKey, System.Text.Encoding.UTF8.GetBytes(authKey + "\n" + entry.Refresh)));
        return new CopilotAccountCredential(scope, generation, entry.Refresh);
    }

    private static bool IsCopilotType(string type) =>
        string.Equals(type, "copilot", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "github-copilot", StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicCopilotEndpoint(string? endpoint) => string.IsNullOrWhiteSpace(endpoint) ||
        (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
         (uri.Host == "api.githubcopilot.com" || uri.Host == "api.individual.githubcopilot.com") &&
         uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo));

    // #1797: the individual/fallback GitHub Copilot MCP host. Distinct from the chat BaseUrl host
    // (api.individual.githubcopilot.com) - the MCP surface lives on api.githubcopilot.com.
    private const string CopilotMcpFallbackEndpoint = "https://api.githubcopilot.com/mcp";

    /// <summary>
    /// Resolves the ready-to-use GitHub Copilot MCP endpoint for a provider (#1797).
    /// Derives the MCP host from the provider's configured endpoint override (enterprise host)
    /// and falls back to the individual host (<c>https://api.githubcopilot.com/mcp</c>) when no
    /// override is declared. This is the single seam for extension-facing Copilot MCP endpoint
    /// resolution - contributors consume the resolved value rather than re-deriving it from a raw
    /// endpoint override.
    /// </summary>
    public string GetCopilotMcpEndpoint(string provider)
        => DeriveCopilotMcpEndpoint(GetApiEndpoint(provider));

    // Turns a raw provider endpoint override (the chat host) into the MCP endpoint by appending the
    // /mcp path, or returns the individual fallback when no override is present. Pure and null-safe.
    private static string DeriveCopilotMcpEndpoint(string? baseEndpoint)
    {
        if (string.IsNullOrWhiteSpace(baseEndpoint))
            return CopilotMcpFallbackEndpoint;

        if (Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var absoluteUri))
        {
            var path = absoluteUri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
                return absoluteUri.ToString().TrimEnd('/');

            var builder = new UriBuilder(absoluteUri)
            {
                Path = string.IsNullOrEmpty(path) || path == "/" ? "/mcp" : $"{path}/mcp"
            };

            return builder.Uri.ToString().TrimEnd('/');
        }

        var trimmed = baseEndpoint.TrimEnd('/');
        return trimmed.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/mcp";
    }

    /// <summary>
    /// Resolves an API key from <c>~/.botnexus/auth.json</c>, environment variables, or platform config.
    /// </summary>
    /// <param name="provider">The provider whose key is required (e.g. <c>github-copilot</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved key, or null when the provider simply has no configured credential.</returns>
    /// <exception cref="ProviderCredentialUnavailableException">
    /// The provider has stored OAuth credentials but refreshing them failed and no environment or
    /// platform-config key could stand in. Previously this returned null, the turn proceeded with no
    /// key, and the run emitted an empty assistant message with a 200 health check - a silent
    /// failure that made the 2026-08-17 GitHub outage nearly undiagnosable (#2977). Failing loudly
    /// here surfaces an explicit error to the user instead.
    /// </exception>
    public async Task<string?> GetApiKeyAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        var (authOutcome, refreshFailure) = await ResolveAuthEntryWithFailureAsync(provider, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(authOutcome.ApiKey))
        {
            return authOutcome.ApiKey;
        }

        // #2807/#4043: ambient admission is gated on whether provider configuration declared a
        // credential, not whether that declaration happened to resolve to a usable value. Keep the
        // declaration bit when auth: lookup is empty, missing, unusable, or refresh-failed so those
        // states cannot silently substitute an unrelated process credential.
        var configuredCredential = await ResolveProviderConfigApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        var credential = ProviderCredentialResolver.Resolve(
            provider,
            configuredCredential.IsDeclared ? configuredCredential.ApiKey ?? string.Empty : null,
            _logger);
        var resolved = credential.Source == CredentialSource.Declared
            ? credential.Value
            : credential.HasValue ? credential.Value : null;

        if (refreshFailure is not null && !configuredCredential.IsDeclared && string.IsNullOrWhiteSpace(resolved))
        {
            // #2977: the credential exists but could not be renewed (or its store is unreadable) and
            // nothing else can stand in. Throw rather than returning null: the caller would otherwise
            // run the turn unauthenticated and produce an empty successful-looking message. A declared
            // configuration keeps its blank sentinel (#2807/#4043) instead.
            throw new ProviderCredentialUnavailableException(provider, refreshFailure);
        }

        return resolved;
    }

    /// <summary>
    /// Creates the provider-owned execution policy used by foreground and background LLM calls.
    /// The resolved credential and configured stream-idle timeout are applied without overwriting
    /// explicit base options. A blank credential sentinel is preserved to suppress ambient fallback.
    /// </summary>
    /// <param name="provider">The model provider whose execution policy is required.</param>
    /// <param name="baseOptions">Optional provider policy to copy and enrich.</param>
    /// <param name="cancellationToken">Cancellation for credential resolution.</param>
    /// <returns>A fresh execution-policy value with gateway authentication and timeout policy applied.</returns>
    public async Task<ProviderExecutionOptions> CreateExecutionOptionsAsync(
        string provider,
        ProviderExecutionOptions? baseOptions = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        var options = baseOptions ?? new ProviderExecutionOptions();
        if (options.StreamIdleTimeoutMs is null)
            options = options with { StreamIdleTimeoutMs = ResolveStreamIdleTimeoutMs(provider) };
        if (apiKey is not null)
            options = options with { ApiKey = apiKey };
        var metadata = options.Metadata is null ? new Dictionary<string, object>() : new Dictionary<string, object>(options.Metadata);
        // Never retain caller attribution after resolving a different credential. Unsupported or
        // ambiguous paths are explicitly unavailable, not legacy calls attributed to the default.
        metadata[BotNexus.Agent.Providers.Copilot.Headers.CopilotHeaderScope.MetadataKey] =
            (object?)ResolveHeaderScope(provider, options.ApiKey) ??
            BotNexus.Agent.Providers.Copilot.Headers.CopilotHeaderAttribution.Unavailable;
        return options with { Metadata = metadata };
    }
    private BotNexus.Agent.Providers.Copilot.Headers.CopilotHeaderScope? ResolveHeaderScope(string provider, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var account = ResolveCopilotAccountCredential(provider);
        if (account is null) return null;
        var providers = _platformConfig.CurrentValue.Providers;
        ProviderConfig? config = null;
        if (providers is not null) TryGetProviderConfig(providers, provider, out config);
        var authKey = config?.ApiKey?.StartsWith("auth:", StringComparison.OrdinalIgnoreCase) == true
            ? config.ApiKey[5..].Trim()
            : IsCopilotType(provider) ? "copilot" : provider;
        lock (_sync)
        {
            if (!TryGetAuthEntry(authKey, out var selected) ||
                !string.Equals(selected.Access, apiKey, StringComparison.Ordinal) ||
                !string.Equals(selected.Refresh, account.OAuthToken, StringComparison.Ordinal)) return null;
            // GetApiKeyAsync gives an exact auth entry precedence over a configured auth: reference.
            // Even equal access tokens cannot prove the selected OAuth account supplied the key.
            if (TryGetAuthEntry(provider, out var exact) && !string.IsNullOrWhiteSpace(exact.Access) &&
                (!string.Equals(exact.Access, apiKey, StringComparison.Ordinal) ||
                 !string.Equals(exact.Refresh, selected.Refresh, StringComparison.Ordinal))) return null;
            return new(account.Instance, account.Generation);
        }
    }

    private int? ResolveStreamIdleTimeoutMs(string provider)
    {
        var providers = _platformConfig.CurrentValue.Providers;
        if (providers is null)
            return null;

        foreach (var (name, providerConfig) in providers)
        {
            if (string.Equals(name, provider, StringComparison.OrdinalIgnoreCase))
                return providerConfig.ResolveStreamIdleTimeoutMs();
        }

        return null;
    }

    /// <summary>
    /// Resolves a stable, non-secret identifier for the credential currently backing a provider (#3015).
    /// </summary>
    /// <remarks>
    /// This is the <b>auth profile</b> half of a suspension's scope. A quota/billing/credential
    /// exhaustion is a property of one credential, not of the provider and not of the instance, so a
    /// suspension keyed on the provider alone would black-hole every agent the moment any one of
    /// them ran out of credit.
    /// <para>
    /// The returned value is deliberately <b>derived, never the secret itself</b>: it is a truncated
    /// SHA-256 digest of the resolved key. It is used as a dictionary key and may be
    /// logged, so returning the credential would turn a diagnostics aid into a secret leak. A digest
    /// is sufficient because the only question ever asked of it is "is this the same credential as
    /// last time".
    /// </para>
    /// <para>
    /// Returns <c>"default"</c> when no key is resolvable, so a provider using ambient/implicit
    /// credentials still gets a single consistent scope rather than a null key.
    /// </para>
    /// </remarks>
    /// <param name="provider">The provider whose credential is being identified.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> GetAuthProfileIdAsync(string provider, CancellationToken cancellationToken = default)
    {
        var apiKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return DeriveAuthProfileId(apiKey);
    }

    /// <summary>
    /// Derives the stable, non-secret auth-profile identifier from a resolved credential (#3015).
    /// Pure and separately testable so the "never emit the secret" property can be pinned directly.
    /// </summary>
    /// <param name="apiKey">The resolved credential, or null/blank when none is configured.</param>
    public static string DeriveAuthProfileId(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "default";
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private async Task<ConfiguredCredentialResolution> ResolveProviderConfigApiKeyAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        if (_platformConfig.CurrentValue.Providers is null ||
            !TryGetProviderConfig(_platformConfig.CurrentValue.Providers, provider, out var providerConfig) ||
            providerConfig?.ApiKey is null)
        {
            return ConfiguredCredentialResolution.Undeclared;
        }

        // Declaration presence is policy state independent of credential resolution. In particular,
        // an auth: reference remains declared when its target is blank, absent, unusable, or cannot
        // be refreshed; the caller must not reinterpret any of those outcomes as permission to use
        // ambient credentials.
        if (string.IsNullOrWhiteSpace(providerConfig.ApiKey))
        {
            return ConfiguredCredentialResolution.Declared(providerConfig.ApiKey);
        }

        const string AuthPrefix = "auth:";
        if (providerConfig.ApiKey.StartsWith(AuthPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var referenceProvider = providerConfig.ApiKey[AuthPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(referenceProvider))
            {
                return ConfiguredCredentialResolution.Declared(apiKey: null);
            }

            var outcome = await ResolveAuthEntryCredentialAsync(referenceProvider, cancellationToken).ConfigureAwait(false);
            return ConfiguredCredentialResolution.Declared(outcome.ApiKey);
        }

        return ConfiguredCredentialResolution.Declared(providerConfig.ApiKey);
    }

    private readonly record struct ConfiguredCredentialResolution(bool IsDeclared, string? ApiKey)
    {
        public static ConfiguredCredentialResolution Undeclared => new(false, null);

        public static ConfiguredCredentialResolution Declared(string? apiKey) => new(true, apiKey);
    }

    /// <summary>
    /// Resolves a credential from <c>auth.json</c> and reports <em>why</em> the attempt ended as it
    /// did (#3281). Only a <em>refresh failure</em> is reported to the health observer; a missing
    /// entry is a steady state, not an outage.
    /// </summary>
    private async Task<ProviderCredentialOutcome> ResolveAuthEntryCredentialAsync(string provider, CancellationToken cancellationToken)
    {
        var (outcome, _) = await ResolveAuthEntryWithFailureAsync(provider, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Resolves the stored auth-entry credential for a provider, refreshing it when due, and also
    /// returns the underlying exception when a refresh failed or a candidate store was unreadable.
    /// The failure is returned rather than swallowed so <see cref="GetApiKeyAsync"/> can decide
    /// between a legitimate "no credential configured" null and a genuine credential outage (#2977).
    /// </summary>
    private async Task<(ProviderCredentialOutcome Outcome, Exception? Failure)> ResolveAuthEntryWithFailureAsync(
        string provider, CancellationToken cancellationToken)
    {
        if (!TryResolveAuthEntry(provider, out var entry, out var loadFailure))
        {
            // #2977: an unreadable store cannot prove the provider has no credential, so it is
            // reported as a failure rather than as "not configured".
            return loadFailure is null
                ? (ProviderCredentialOutcome.NotConfigured(), null)
                : (ProviderCredentialOutcome.Failed(loadFailure.GetType().Name, null, loadFailure.Message), loadFailure);
        }

        if (!NeedsRefresh(entry))
        {
            return string.IsNullOrWhiteSpace(entry.Access)
                ? (ProviderCredentialOutcome.NotConfigured(), null)
                : (ProviderCredentialOutcome.Success(entry.Access), null);
        }

        try
        {
            var refreshed = await _refreshEntry(entry, cancellationToken).ConfigureAwait(false);
            UpdateEntry(provider, refreshed);
            var success = ProviderCredentialOutcome.Success(refreshed.Access);
            await NotifyHealthObserverAsync(provider, success, cancellationToken).ConfigureAwait(false);
            return (success, null);
        }
        catch (OperationCanceledException)
        {
            // Caller-driven cancellation is not a provider fault and must not be reported as one.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed refreshing auth credentials for provider '{Provider}'.", provider);

            var failure = ProviderCredentialOutcome.Failed(
                ex.GetType().Name,
                ExtractStatusCode(ex),
                ex.Message);

            await NotifyHealthObserverAsync(provider, failure, cancellationToken).ConfigureAwait(false);
            return (failure, ex);
        }
    }

    /// <summary>
    /// Reports an outcome to the health observer without ever letting it break credential
    /// resolution. An observer fault must not escalate a recoverable provider outage into a
    /// failure of the code attempting to report that outage.
    /// </summary>
    private async Task NotifyHealthObserverAsync(string provider, ProviderCredentialOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await _healthObserver.RecordAsync(provider, outcome, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider health observer failed for provider '{Provider}'.", provider);
        }
    }

    /// <summary>
    /// Best-effort extraction of the upstream HTTP status code from a refresh failure.
    ///
    /// <para>
    /// <see cref="HttpRequestException.StatusCode"/> is populated when the failure came from
    /// <c>EnsureSuccessStatusCode</c>, which is the path the observed outage took. It is left null
    /// for transport-level faults (DNS, connection reset) where no status exists - null means "no
    /// status observed", never zero, so that a missing measurement is not presented as a real one.
    /// </para>
    /// </summary>
    private static int? ExtractStatusCode(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                return (int)status;
            }
        }

        return null;
    }

    private static bool NeedsRefresh(AuthEntry entry)
    {
        if (!string.Equals(entry.Type, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return nowMs >= entry.Expires - RefreshMarginMs || string.IsNullOrWhiteSpace(entry.Endpoint);
    }

    private static async Task<AuthEntry> RefreshEntryAsync(AuthEntry entry, CancellationToken cancellationToken)
    {
        var credentials = new OAuthCredentials(
            AccessToken: entry.Access,
            RefreshToken: entry.Refresh,
            ExpiresAt: entry.Expires / 1000,
            ApiEndpoint: entry.Endpoint);

        var refreshed = await CopilotOAuth.RefreshAsync(credentials, cancellationToken).ConfigureAwait(false);

        return new AuthEntry
        {
            Type = entry.Type,
            Refresh = refreshed.RefreshToken,
            Access = refreshed.AccessToken,
            Expires = refreshed.ExpiresAt * 1000,
            Endpoint = refreshed.ApiEndpoint ?? entry.Endpoint
        };
    }

    private bool TryGetAuthEntryLocked(string provider, out AuthEntry entry)
        => _entries.TryGetValue(provider, out entry!) ||
           (string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase) &&
            _entries.TryGetValue("github-copilot", out entry!));

    private void UpdateEntry(string provider, AuthEntry entry)
    {
        lock (_sync)
        {
            _entries[provider] = entry;
            SaveAuthEntries();
        }
    }

    /// <summary>
    /// Runs a provider call with a single, bounded invalidate-and-retry on an authentication
    /// failure (#3833).
    ///
    /// <para>
    /// #3673 shortened the stale-credential window from "until a gateway restart" to "until the next
    /// resolution", but it could not remove the call that <i>discovers</i> the staleness: a turn
    /// already in flight when the credential rotates still spends a real provider round trip and
    /// surfaces an opaque 403 that advises rotating a key which has already been rotated. This is
    /// the seam that closes that gap - it hands the resolved credential to the operation, and on a
    /// 401/403 drops the cache via <see cref="InvalidateCache"/>, re-resolves from disk and runs the
    /// operation exactly once more.
    /// </para>
    ///
    /// <para>
    /// The bound is <b>structural, not configured</b>. There is no loop and no retry count: the
    /// second attempt is a straight-line second call, so "at most one forced reload per failed call"
    /// is a property of the shape of this method rather than of a policy object someone could later
    /// widen. A second failure propagates unmodified, so the caller sees the provider's own message
    /// and not a wrapper.
    /// </para>
    ///
    /// <para>
    /// Only <see cref="ProviderAuthenticationException"/> triggers the retry. A 500 or a timeout is
    /// not evidence that the cached credential is wrong, and spending an invalidation on one would
    /// turn every upstream outage into a disk re-read storm across every in-flight turn.
    /// </para>
    /// </summary>
    /// <typeparam name="TResult">The provider call's result type.</typeparam>
    /// <param name="provider">The provider whose credential backs the call.</param>
    /// <param name="operation">
    /// The provider call, receiving the freshly resolved API key (null when none is configured, so
    /// the provider's own environment fallback still applies) and the cancellation token.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<TResult> InvokeWithAuthRetryAsync<TResult>(
        string provider,
        Func<string?, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var apiKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderAuthenticationException ex)
        {
            _logger.LogWarning(
                "Provider '{Provider}' rejected the cached credential (HTTP {StatusCode}); invalidating " +
                "the auth cache and retrying once (#3833).",
                provider,
                ex.StatusCode is { } status ? (int)status : 0);

            InvalidateCache();
        }

        // Outside the catch deliberately: the retry's own failure must reach the caller as itself,
        // not as an exception thrown while handling another one.
        var refreshedKey = await GetApiKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return await operation(refreshedKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the cached <c>auth.json</c> entries so the next resolution re-reads from disk (#3673).
    ///
    /// <para>
    /// The mtime check below already covers the ordinary rotation case. This is the explicit escape
    /// hatch for a caller that has independent evidence the cache is wrong - most usefully a provider
    /// that just answered 401/403 - and does not want to depend on filesystem timestamp granularity
    /// to find out.
    /// </para>
    /// </summary>
    public void InvalidateCache()
    {
        lock (_sync)
        {
            _loaded = false;
            _loadedSignature = null;
        }
    }

    /// <summary>
    /// Loads (or reuses) the auth stores. Caller must hold <see cref="_sync"/>.
    /// </summary>
    /// <remarks>
    /// Escalation invariant: a candidate store that contributed nothing may never veto one that
    /// parsed successfully. A failure is surfaced to callers only when the PRIMARY store is itself
    /// unreadable, or when no store at all parsed successfully. Everything else is a Warning.
    /// </remarks>
    private Exception? LoadAuthEntriesLocked()
    {
        // #3673: the stat signature is the cache key, so an out-of-process rewrite (or an explicit
        // InvalidateCache, which clears the signature) always forces a re-read.
        var signature = ComputeAuthFileSignature();
        var signatureUnchanged = string.Equals(_loadedSignature, signature, StringComparison.Ordinal);
        if (_loaded && signatureUnchanged)
        {
            return null;
        }

        // Re-read floor (#2977): while a store stays unreadable, _loaded deliberately never latches so
        // transient corruption can self-heal. Without a floor that turns every single credential
        // resolution into synchronous disk IO inside this lock. Serve the cached failure instead
        // until the floor elapses - but only while the files are unchanged, so a repaired or
        // rotated store is still picked up immediately.
        var nowUtc = DateTimeOffset.UtcNow;
        if (!_loaded && signatureUnchanged &&
            (_loadFailure is not null || _partialLoadFailure is not null) &&
            _lastLoadAttemptUtc is { } lastAttempt && nowUtc - lastAttempt < LoadRetryFloor)
        {
            return _loadFailure;
        }

        _lastLoadAttemptUtc = nowUtc;
        _loadedSignature = signature;

        var entries = new Dictionary<string, AuthEntry>(StringComparer.OrdinalIgnoreCase);

        // The candidate set is ordered legacy-first so the primary store wins on key collisions.
        // isPrimary marks the store the gateway actually writes back to (SaveAuthEntries); only it
        // can single-handedly escalate, because only it is authoritative.
        (string Path, bool IsPrimary)[] candidates =
        [
            (_legacyAuthFilePath, false),
            (_authFilePath, true)
        ];

        Exception? primaryFailure = null;
        Exception? anyFailure = null;
        var anySucceeded = false;

        foreach (var (candidatePath, isPrimary) in candidates)
        {
            if (!_fileSystem.File.Exists(candidatePath))
                continue;

            try
            {
                var json = _fileSystem.File.ReadAllText(candidatePath);
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, AuthEntry>>(json, JsonOptions) ??
                    new Dictionary<string, AuthEntry>();

                foreach (var (key, value) in deserialized)
                {
                    entries[key] = value;
                }

                anySucceeded = true;
            }
            catch (Exception ex)
            {
                // #2977 (credential-store half): a corrupt, half-written or ACL-denied auth.json
                // used to be swallowed into an empty result, so the provider reported
                // "not configured" - painted muted grey and EXCLUDED from the rollup - while an
                // engineer pinned to it got empty turns. Unreadable is not absent.
                _logger.LogWarning(ex, "Failed to parse auth file '{AuthPath}'.", candidatePath);
                var failure = new InvalidOperationException(
                    $"Auth store '{candidatePath}' exists but could not be read or parsed: {ex.Message}", ex);
                anyFailure ??= failure;
                if (isPrimary)
                {
                    primaryFailure = failure;
                }
            }
        }

        _entries = entries;
        _loadFailure = primaryFailure ?? (anySucceeded ? null : anyFailure);
        _partialLoadFailure = anyFailure;

        // Only latch when nothing is outstanding - including a partial failure. Latching on a
        // partial failure would cache the corrupt legacy file as authoritative for the process
        // lifetime, so a provider that lives only there would read as "not configured" forever.
        // The re-read floor above keeps the resulting retry from becoming a re-read storm.
        _loaded = _loadFailure is null && _partialLoadFailure is null;
        return _loadFailure;
    }

    /// <summary>
    /// Single-acquisition resolution: loads the stores and looks the provider up under one lock so
    /// the returned failure and the returned entry always describe the same store snapshot.
    /// </summary>
    private bool TryResolveAuthEntry(string provider, out AuthEntry entry, out Exception? loadFailure)
    {
        lock (_sync)
        {
            // A hit proves the credential regardless of any other store's state; only a miss is
            // rendered unknowable by a partially unreadable candidate set.
            var hard = LoadAuthEntriesLocked();
            var found = TryGetAuthEntryLocked(provider, out entry);
            loadFailure = found ? null : (hard ?? _partialLoadFailure);
            return found;
        }
    }

    /// <summary>
    /// Stats the candidate auth files and renders their observable state as a comparison token (#3673).
    /// Last-write time alone is not enough on filesystems with coarse timestamp granularity, so the
    /// length participates too - two rewrites within the same tick that changed the token almost
    /// always differ in length, and the explicit <see cref="InvalidateCache"/> seam covers the rest.
    /// A stat failure renders as a stable marker rather than a changing one: a transient error must
    /// not be able to turn every subsequent resolution into a disk read.
    /// </summary>
    private string ComputeAuthFileSignature()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var candidatePath in new[] { _legacyAuthFilePath, _authFilePath })
        {
            builder.Append(candidatePath).Append('|');
            try
            {
                var info = _fileSystem.FileInfo.New(candidatePath);
                if (info.Exists)
                {
                    builder.Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length);
                }
                else
                {
                    builder.Append("absent");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to stat auth file '{AuthPath}' for staleness detection.", candidatePath);
                builder.Append("unknown");
            }

            builder.Append(';');
        }

        return builder.ToString();
    }

    private void SaveAuthEntries()
    {
        var directory = Path.GetDirectoryName(_authFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        _fileSystem.File.WriteAllText(_authFilePath, json);
        // #2392: auth.json holds OAuth refresh/access tokens. Narrow it to the owner on every
        // save, not just first create - a token refresh rewrites this file routinely.
        SecureFilePermissions.RestrictToOwner(_fileSystem, _authFilePath);

        // #3673: this process just rewrote the file, so the cache already matches what is on disk.
        // Re-baselining the signature keeps an in-process refresh from being mistaken for a foreign
        // rotation and forcing a pointless re-read of our own write.
        _loadedSignature = ComputeAuthFileSignature();
    }

    private static bool TryGetProviderConfig(
        IReadOnlyDictionary<string, ProviderConfig> providers,
        string provider,
        out ProviderConfig? providerConfig)
    {
        if (providers.TryGetValue(provider, out var exact))
        {
            providerConfig = exact;
            return true;
        }

        foreach (var (key, value) in providers)
        {
            if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase))
            {
                providerConfig = value;
                return true;
            }
        }

        providerConfig = null;
        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// A single credential record from <c>auth.json</c>. Internal rather than private so the
    /// refresh seam above can be substituted from tests (#3281); it is not part of the public API.
    /// </summary>
    internal sealed class AuthEntry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "oauth";

        [JsonPropertyName("refresh")]
        public string Refresh { get; set; } = string.Empty;

        [JsonPropertyName("access")]
        public string Access { get; set; } = string.Empty;

        [JsonPropertyName("expires")]
        public long Expires { get; set; }

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }
    }
}
