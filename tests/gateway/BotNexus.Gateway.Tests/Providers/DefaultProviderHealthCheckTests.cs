using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class DefaultProviderHealthCheckTests
{
    private readonly ModelRegistry _modelRegistry = new();
    private readonly GatewayAuthManager _authManager;

    public DefaultProviderHealthCheckTests()
    {
        var config = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        config.CurrentValue.Returns(new PlatformConfig());
        var fileSystem = Substitute.For<IFileSystem>();
        _authManager = new GatewayAuthManager(config, NullLogger<GatewayAuthManager>.Instance, fileSystem);
    }

    private DefaultProviderHealthCheck CreateSut() =>
        new(_modelRegistry, _authManager, NullLogger<DefaultProviderHealthCheck>.Instance);

    [Fact]
    public async Task CheckAsync_UnknownProvider_ReturnsUnhealthyNoModels()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync("nonexistent");

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
        Assert.Equal(0, result.ModelCount);
        Assert.Contains("No models registered", result.Error);
        Assert.Equal(ProviderHealthReason.NoModels, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_EmptyProviderId_ReturnsUnhealthy()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync("");

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
        Assert.Contains("required", result.Error);
    }

    [Fact]
    public async Task CheckAsync_NullProviderId_ReturnsUnhealthy()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync(null!);

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ModelsRegisteredButNoCredentials_ReturnsUnhealthy()
    {
        _modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "Test Model",
            Api: "openai-completions",
            Provider: "test-provider",
            BaseUrl: "https://api.test.com",
            Reasoning: false,
            Input: new[] { "text" },
            Cost: new ModelCost(1m, 2m, 0.5m, 0.5m),
            ContextWindow: 128000,
            MaxTokens: 4096));
        var sut = CreateSut();

        var result = await sut.CheckAsync("test-provider");

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
        Assert.Equal(1, result.ModelCount);
        Assert.False(result.HasCredentials);
        Assert.Contains("credential", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProviderHealthReason.NotConfigured, result.Reason);
    }

    // ── Reason arms ───────────────────────────────────────────────────────
    //
    // `HasCredentials == false` is emitted for two disjoint causes - a provider nobody ever gave a
    // key to, and a provider whose key resolution THREW. The second is an incident (the 2026-08-17
    // token-refresh outage) and the first is a deployment choice; a consumer that cannot tell them
    // apart renders the outage as benign. These tests pin the distinction at its source.

    [Fact]
    public async Task CheckAsync_WhenCredentialResolutionThrows_ReportsCredentialFailureNotNotConfigured()
    {
        RegisterModel("broken-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry,
            UnrefreshableCredentialAuthManager(),
            NullLogger<DefaultProviderHealthCheck>.Instance);

        var result = await sut.CheckAsync("broken-provider");

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
        Assert.Equal(ProviderHealthReason.CredentialFailure, result.Reason);
        Assert.False(result.HasCredentials);
    }

    [Fact]
    public async Task CheckAsync_WhenCredentialResolutionThrowsAndNoModelsAreRegistered_StillReportsCredentialFailure()
    {
        // The credential fault is the actionable one; a model count of zero is usually a
        // consequence of it, so it must not mask the cause.
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry,
            UnrefreshableCredentialAuthManager(),
            NullLogger<DefaultProviderHealthCheck>.Instance);

        var result = await sut.CheckAsync("broken-provider");

        Assert.Equal(ProviderHealthReason.CredentialFailure, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_WhenNoModelsAreRegistered_ReportsNoModels()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync("nonexistent");

        Assert.Equal(ProviderHealthReason.NoModels, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_WhenCredentialsResolve_ReportsHealthy()
    {
        RegisterModel("keyed-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry,
            ConfiguredKeyAuthManager("keyed-provider", "a-real-key"),
            NullLogger<DefaultProviderHealthCheck>.Instance);

        var result = await sut.CheckAsync("keyed-provider");

        Assert.Equal(ProviderHealthStatus.Healthy, result.Status);
        Assert.Equal(ProviderHealthReason.Healthy, result.Reason);
        Assert.True(result.HasCredentials);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_EmptyProviderId_ReportsUnknownReason()
    {
        // Nothing was probed, so no cause can honestly be claimed - and Unknown is the value the
        // client treats conservatively rather than as "fine".
        var sut = CreateSut();

        var result = await sut.CheckAsync("");

        Assert.Equal(ProviderHealthReason.Unknown, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_WhenAuthStoreExistsButIsUnreadable_ReportsCredentialFailureNotNotConfigured()
    {
        // An unreadable credential store is NOT an absent one. Swallowing the parse failure made
        // GetApiKeyAsync return null without throwing, which took the !hasCredentials arm and
        // reported "notconfigured" - painted muted grey and excluded from the client rollup, so a
        // provider whose keys were unreadable sat silently under a green "Healthy" headline.
        RegisterModel("broken-store-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry,
            BuildAuthManager(new PlatformConfig(), "{ this is not valid json"),
            NullLogger<DefaultProviderHealthCheck>.Instance);

        var result = await sut.CheckAsync("broken-store-provider");

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
        Assert.Equal(ProviderHealthReason.CredentialFailure, result.Reason);
        Assert.NotEqual(ProviderHealthReason.NotConfigured, result.Reason);
        Assert.Equal("credentialfailure", result.Reason.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task CheckAsync_WhenAuthStoreIsGenuinelyAbsent_StillReportsNotConfigured()
    {
        // The recovery above must not turn every keyless deployment into a false incident.
        RegisterModel("keyless-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry,
            BuildAuthManager(new PlatformConfig(), authFileJson: null),
            NullLogger<DefaultProviderHealthCheck>.Instance);

        var result = await sut.CheckAsync("keyless-provider");

        Assert.Equal(ProviderHealthReason.NotConfigured, result.Reason);
    }

    // ── Store-scoping invariant ───────────────────────────────────────────
    //
    // A candidate store that contributed nothing must never veto one that parsed. The legacy store
    // is read FIRST, and the load failure used to be a single unscoped field, so a corrupt legacy
    // file poisoned a perfectly healthy current auth.json for the lifetime of the process (only the
    // current path is ever rewritten, so it never self-healed).

    [Fact]
    public async Task CheckAsync_WhenLegacyStoreIsCorruptButCurrentStoreIsValid_KeyedProviderIsHealthy()
    {
        RegisterModel("keyed-store-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry,
            BuildAuthManager(
                new PlatformConfig(),
                authFileJson: """
                {
                  "keyed-store-provider": { "type": "api", "access": "a-real-key" }
                }
                """,
                legacyAuthFileJson: "{ this is not valid json"),
            NullLogger<DefaultProviderHealthCheck>.Instance);

        var result = await sut.CheckAsync("keyed-store-provider");

        Assert.Equal(ProviderHealthStatus.Healthy, result.Status);
        Assert.Equal(ProviderHealthReason.Healthy, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_WhenLegacyStoreIsCorruptButCurrentStoreIsValid_KeylessProviderIsCredentialFailure()
    {
        // A keyless provider CANNOT be proven not-configured while any store is unreadable: its key
        // may have lived only in the corrupt legacy file. Reporting NotConfigured paints it muted
        // grey and EXCLUDES it from the rollup, so the headline goes green over a dead provider.
        // WITHOUT the production fix: anySucceeded (primary parsed) discards the legacy failure,
        // _loadFailure is null, GetApiKeyAsync's throw gate never fires, reason is NotConfigured.
        RegisterModel("keyless-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry,
            BuildAuthManager(
                new PlatformConfig(),
                authFileJson: """
                {
                  "keyed-store-provider": { "type": "api", "access": "a-real-key" }
                }
                """,
                legacyAuthFileJson: "{ this is not valid json"),
            NullLogger<DefaultProviderHealthCheck>.Instance);

        var result = await sut.CheckAsync("keyless-provider");

        Assert.Equal(ProviderHealthReason.CredentialFailure, result.Reason);
        Assert.NotEqual(ProviderHealthReason.NotConfigured, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_WhenNoStoreIsCorrupt_KeylessProviderStillReportsNotConfigured()
    {
        // Guards the fix against degrading into "everything is always CredentialFailure": with a
        // genuinely absent legacy store and a readable primary, the answer IS knowable and a miss
        // really does mean not configured.
        RegisterModel("keyless-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry,
            BuildAuthManager(
                new PlatformConfig(),
                authFileJson: """
                {
                  "keyed-store-provider": { "type": "api", "access": "a-real-key" }
                }
                """,
                legacyAuthFileJson: null),
            NullLogger<DefaultProviderHealthCheck>.Instance);

        var result = await sut.CheckAsync("keyless-provider");

        Assert.Equal(ProviderHealthReason.NotConfigured, result.Reason);
    }

    [Fact]
    public async Task CheckAsync_WhenLegacyStoreIsCorrupt_LoadDoesNotLatchAndTheStoreIsReReadAfterTheFloor()
    {
        // Recovery must stay reachable: a partial (legacy-only) failure must not latch _loaded, or
        // the corrupt file is cached as authoritative for the process lifetime and a repaired store
        // is never picked up. WITHOUT the production fix _loaded latches on the first resolution
        // (primary parsed => _loadFailure null => _loaded true) and the second resolution re-reads
        // nothing, so reads stays at 1.
        var reads = 0;
        var root = Path.Combine(Path.GetTempPath(), "botnexus", "no-latch", Guid.NewGuid().ToString("N"));
        var authPath = Path.Combine(root, "auth.json");
        var legacyPath = Path.Combine(root, "legacy-auth.json");

        var file = Substitute.For<IFile>();
        file.Exists(authPath).Returns(true);
        file.Exists(legacyPath).Returns(true);
        file.ReadAllText(authPath).Returns("""
        {
          "keyed-store-provider": { "type": "api", "access": "a-real-key" }
        }
        """);
        file.ReadAllText(legacyPath).Returns(_ =>
        {
            reads++;
            return "{ this is not valid json";
        });

        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);

        var monitor = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        monitor.CurrentValue.Returns(new PlatformConfig());

        var manager = new GatewayAuthManager(monitor, NullLogger<GatewayAuthManager>.Instance, fileSystem);
        typeof(GatewayAuthManager)
            .GetField("_authFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, authPath);
        typeof(GatewayAuthManager)
            .GetField("_legacyAuthFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, legacyPath);

        RegisterModel("keyless-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry, manager, NullLogger<DefaultProviderHealthCheck>.Instance);

        _ = await sut.CheckAsync("keyless-provider");
        Assert.Equal(1, reads);

        // Age the last attempt past the 30s re-read floor. The floor only throttles retries; it
        // must not stand in for the latch.
        typeof(GatewayAuthManager)
            .GetField("_lastLoadAttemptUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-5));

        _ = await sut.CheckAsync("keyless-provider");

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task CheckAsync_WhenStoreIsUnreadable_ReReadsTheStoreAtMostOnceWithinTheRetryFloor()
    {
        // An unreadable store deliberately never latches _loaded, so resolution retries. Without a
        // retry floor that meant EVERY credential resolution re-did File.Exists + ReadAllText +
        // Deserialize synchronously inside the manager's lock - a lock convoy under a dashboard
        // fan-out that starved the very threadpool the dashboard then reported as starved.
        var reads = 0;
        var authPath = Path.Combine(Path.GetTempPath(), "botnexus", "retry-floor", "auth.json");
        var legacyPath = Path.Combine(Path.GetTempPath(), "botnexus", "retry-floor", "legacy-auth.json");

        var file = Substitute.For<IFile>();
        file.Exists(authPath).Returns(true);
        file.Exists(legacyPath).Returns(false);
        file.ReadAllText(authPath).Returns(_ =>
        {
            reads++;
            return "{ this is not valid json";
        });

        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);

        var monitor = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        monitor.CurrentValue.Returns(new PlatformConfig());

        var manager = new GatewayAuthManager(monitor, NullLogger<GatewayAuthManager>.Instance, fileSystem);
        typeof(GatewayAuthManager)
            .GetField("_authFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, authPath);
        typeof(GatewayAuthManager)
            .GetField("_legacyAuthFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, legacyPath);

        RegisterModel("broken-store-provider");
        var sut = new DefaultProviderHealthCheck(
            _modelRegistry, manager, NullLogger<DefaultProviderHealthCheck>.Instance);

        var first = await sut.CheckAsync("broken-store-provider");
        var second = await sut.CheckAsync("broken-store-provider");

        Assert.Equal(1, reads);
        // The floor must not cost the diagnosis: the cached failure is still reported.
        Assert.Equal(ProviderHealthReason.CredentialFailure, first.Reason);
        Assert.Equal(ProviderHealthReason.CredentialFailure, second.Reason);
    }

    private void RegisterModel(string provider) =>
        _modelRegistry.Register(provider, new LlmModel(
            Id: "test-model",
            Name: "Test Model",
            Api: "openai-completions",
            Provider: provider,
            BaseUrl: "https://api.test.com",
            Reasoning: false,
            Input: new[] { "text" },
            Cost: new ModelCost(1m, 2m, 0.5m, 0.5m),
            ContextWindow: 128000,
            MaxTokens: 4096));

    /// <summary>
    /// <see cref="GatewayAuthManager"/> is sealed with no virtual key-resolution seam, so the two
    /// credential outcomes are driven through its real resolution path with a mock filesystem
    /// rather than through a substitute.
    /// </summary>
    private GatewayAuthManager BuildAuthManager(PlatformConfig config, string? authFileJson)
        => BuildAuthManager(config, authFileJson, legacyAuthFileJson: null);

    private GatewayAuthManager BuildAuthManager(PlatformConfig config, string? authFileJson, string? legacyAuthFileJson)
    {
        var monitor = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        monitor.CurrentValue.Returns(config);

        var fileSystem = new MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "botnexus", "provider-health-tests", Guid.NewGuid().ToString("N"));
        fileSystem.Directory.CreateDirectory(root);
        var authPath = Path.Combine(root, "auth.json");
        var legacyPath = Path.Combine(root, "legacy-auth.json");
        if (authFileJson is not null)
            fileSystem.File.WriteAllText(authPath, authFileJson);
        if (legacyAuthFileJson is not null)
            fileSystem.File.WriteAllText(legacyPath, legacyAuthFileJson);

        var manager = new GatewayAuthManager(monitor, NullLogger<GatewayAuthManager>.Instance, fileSystem);
        typeof(GatewayAuthManager)
            .GetField("_authFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, authPath);
        typeof(GatewayAuthManager)
            .GetField("_legacyAuthFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, legacyPath);
        return manager;
    }

    /// <summary>
    /// An OAuth entry that is already expired, so resolution must attempt a refresh. The refresh
    /// endpoint is unreachable, GetApiKeyAsync therefore raises
    /// ProviderCredentialUnavailableException, and the health check must classify that as an
    /// incident rather than as "nobody configured a key". No network is required: the entry has no
    /// usable endpoint, and any transport failure produces the same throw.
    /// </summary>
    private GatewayAuthManager UnrefreshableCredentialAuthManager() =>
        BuildAuthManager(new PlatformConfig(), """
        {
          "broken-provider": {
            "type": "oauth",
            "refresh": "not-a-real-refresh-token",
            "access": "stale",
            "expires": 1,
            "endpoint": ""
          }
        }
        """);

    private GatewayAuthManager ConfiguredKeyAuthManager(string provider, string key) =>
        BuildAuthManager(
            new PlatformConfig
            {
                Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    [provider] = new() { ApiKey = key }
                }
            },
            authFileJson: null);

    [Fact]
    public async Task CheckAsync_ReturnsLatencyAndTimestamp()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync("any-provider");

        Assert.True(result.LatencyMs >= 0);
        Assert.True(result.CheckedAt <= DateTimeOffset.UtcNow);
        Assert.True(result.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task CheckAsync_AlreadyCancelledToken_ReturnsANonHealthyResultForTheRequestedProvider()
    {
        // The previous version of this test asserted only Assert.NotNull on a non-nullable
        // return, so it could not fail. What actually matters on a pre-cancelled token is that
        // the check does not report an all-clear it never verified, and that the result is
        // attributed to the provider that was asked about.
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await sut.CheckAsync("test", cts.Token);

        Assert.Equal("test", result.ProviderId);
        Assert.NotEqual(ProviderHealthStatus.Healthy, result.Status);
        Assert.NotEqual(ProviderHealthReason.Healthy, result.Reason);
    }
}
