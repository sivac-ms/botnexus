using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit component tests for the Health dashboard. The dashboard exists because a silent
/// credential failure (2026-08-17) left the gateway answering 200 while agents produced empty
/// turns, so the behaviour under test is not "it renders" but "it keeps telling the truth when
/// pieces of the backend are down".
///
/// Three guarantees are load-bearing and each has a dedicated test below:
/// <list type="bullet">
///   <item>probe failures are isolated - one dead endpoint degrades ONE card, never the page;</item>
///   <item>a stale watchdog feed is reported as stale rather than as good news;</item>
///   <item>the pre-first-load paint says "Checking" rather than flashing a false outage.</item>
/// </list>
/// </summary>
public sealed class HealthDashboardComponentTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly HealthMockHandler _handler = new();

    public HealthDashboardComponentTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _handler.Dispose();
    }

    // ── Fixtures ───────────────────────────────────────────────────────────

    /// <summary>
    /// Timestamps are always derived from <see cref="DateTimeOffset.UtcNow"/> so the stale/fresh
    /// split is a property of the fixture, not of the wall clock on the machine running the suite.
    /// </summary>
    private static string AgeSeconds(int seconds) =>
        DateTimeOffset.UtcNow.AddSeconds(-seconds).ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static string AvailabilityJson(
        string status = "healthy",
        int checkedSecondsAgo = 30,
        long tokenValidForSeconds = 3600,
        bool signInRequired = false,
        string? signInUri = null,
        string? signInCode = null,
        int restartsLastHour = 0) =>
        $$"""
        {
          "status": "{{status}}",
          "checkedAt": "{{AgeSeconds(checkedSecondsAgo)}}",
          "tokenValidForSeconds": {{tokenValidForSeconds}},
          "refreshMarginSeconds": 300,
          "signInRequired": {{(signInRequired ? "true" : "false")}},
          "signInUri": {{(signInUri is null ? "null" : $"\"{signInUri}\"")}},
          "signInCode": {{(signInCode is null ? "null" : $"\"{signInCode}\"")}},
          "restartsLastHour": {{restartsLastHour}}
        }
        """;

    private const string MemoryJson =
        """{"workingSetReadable":"412 MB","pressurePercent":0.184,"gen2Collections":7,"level":"Normal"}""";

    private const string ThreadPoolJson =
        """{"pendingWorkItems":2,"workerAvailable":32000,"isHealthy":true,"queueDepthThreshold":100}""";

    private const string ActivityJson =
        """{"inactivitySeconds":45,"isHealthy":true,"warningThresholdSeconds":900}""";

    private const string LoopsJson = """{"activeCount":1,"peakCount":9,"totalCompleted":420}""";

    private const string LogPatternsJson = """{"patterns":[],"total":0}""";

    private const string GatewayInfoJson = """{"uptimeSeconds":7200}""";

    private const string ProviderListJson = """[{"providerId":"openai"},{"providerId":"anthropic"}]""";

    /// <summary>
    /// <paramref name="reason"/> is the field the dashboard classifies on. It is explicit in every
    /// fixture because <c>hasCredentials</c> cannot separate "never configured" from "credential
    /// resolution failed", and a fixture that leaves it implicit re-encodes exactly that bug.
    /// Passing <c>omitReason</c> models an older gateway that predates the field.
    /// </summary>
    private static string ProviderHealthJson(
        string id,
        string status = "healthy",
        int? models = 12,
        bool hasCredentials = true,
        string? reason = "healthy",
        bool omitReason = false,
        string? error = null) =>
        $$"""
        {
          "providerId": "{{id}}",
          "status": "{{status}}",
          "latencyMs": 42,
          "models": {{models?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}},
          "hasCredentials": {{(hasCredentials ? "true" : "false")}},
          {{(omitReason ? "" : $"\"reason\": {(reason is null ? "null" : $"\"{reason}\"")},")}}
          "error": {{(error is null ? "null" : System.Text.Json.JsonSerializer.Serialize(error))}}
        }
        """;

    /// <summary>Every probe answering with a good payload - the all-green baseline.</summary>
    private void SetupHealthyBackend(string availability)
    {
        _handler.SetJson("api/health/availability", availability);
        _handler.SetJson("api/diagnostics/memory-pressure", MemoryJson);
        _handler.SetJson("api/diagnostics/threadpool", ThreadPoolJson);
        _handler.SetJson("api/diagnostics/activity", ActivityJson);
        _handler.SetJson("api/diagnostics/loops", LoopsJson);
        _handler.SetJson("api/diagnostics/log-patterns", LogPatternsJson);
        _handler.SetJson("api/gateway/info", GatewayInfoJson);
        _handler.SetJson("api/providers", ProviderListJson);
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson("openai"));
        _handler.SetJson("api/providers/anthropic/health", ProviderHealthJson("anthropic", models: 8));
    }

    private IRenderedComponent<HealthDashboard> RenderLoaded()
    {
        var cut = _ctx.Render<HealthDashboard>();
        // The rollup leaves "checking" only once the first full pass has completed, so this is the
        // component's own first-load signal rather than an arbitrary settling delay.
        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("checking", cut.Find("[data-testid='health-rollup-dot']").GetAttribute("class")));
        return cut;
    }

    private static string DotClass(IRenderedComponent<HealthDashboard> cut) =>
        cut.Find("[data-testid='health-rollup-dot']").GetAttribute("class") ?? string.Empty;

    /// <summary>
    /// Token-exact, not substring: "bad" is a substring of nothing here today but "ok" is a
    /// substring of a great many future class names, and a substring assertion would keep passing
    /// while the dot turned the wrong colour.
    /// </summary>
    private static void AssertDotToken(string expected, string classAttribute) =>
        Assert.Contains(expected, (classAttribute ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void AssertNotDotToken(string unexpected, string classAttribute) =>
        Assert.DoesNotContain(unexpected, (classAttribute ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ProviderDot(IRenderedComponent<HealthDashboard> cut, string id) =>
        cut.Find($"[data-testid='health-provider-{id}'] .health-dot").GetAttribute("class") ?? string.Empty;

    private static string Text(IRenderedComponent<HealthDashboard> cut, string testId) =>
        cut.Find($"[data-testid='{testId}']").TextContent.Trim();

    // ── 1. Loaded / healthy ────────────────────────────────────────────────

    [Fact]
    public void Renders_healthy_rollup_when_every_probe_succeeds()
    {
        SetupHealthyBackend(AvailabilityJson());

        var cut = RenderLoaded();

        cut.Find("[data-testid='health-dashboard']");
        AssertDotToken("ok", DotClass(cut));
        Assert.Equal("Healthy", Text(cut, "health-overall-status"));
        Assert.Equal("Reachable", Text(cut, "health-gateway-reachable"));
        Assert.Equal("2/2 usable", Text(cut, "health-providers-usable"));
    }

    [Fact]
    public void Credentials_card_renders_token_ttl_and_restart_count()
    {
        SetupHealthyBackend(AvailabilityJson(tokenValidForSeconds: 3600, restartsLastHour: 2));

        var cut = RenderLoaded();

        cut.Find("[data-testid='health-card-credentials']");
        Assert.Empty(cut.FindAll("[data-testid='health-credentials-unavailable']"));
        Assert.Equal("1h 0m", Text(cut, "health-token-ttl"));
        Assert.Equal("2", Text(cut, "health-restarts"));
        Assert.Equal("healthy", Text(cut, "health-credentials-status"));
    }

    [Fact]
    public void Last_check_reports_a_fresh_watchdog_without_the_stale_marker()
    {
        SetupHealthyBackend(AvailabilityJson(checkedSecondsAgo: 30));

        var cut = RenderLoaded();

        var lastCheck = Text(cut, "health-last-check");
        Assert.Contains("ago", lastCheck);
        Assert.DoesNotContain("STALE", lastCheck);
    }

    [Fact]
    public void Renders_a_card_row_per_provider_ordered_unhealthy_first()
    {
        SetupHealthyBackend(AvailabilityJson());
        // hasCredentials and reason are both explicit: relying on the fixture default to keep this
        // provider broken made the ordering assertion hostage to an unrelated default.
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "unhealthy", hasCredentials: true, reason: "credentialfailure",
            error: "upstream 502"));

        var cut = RenderLoaded();

        cut.WaitForAssertion(() => cut.Find("[data-testid='health-provider-openai']"));
        cut.Find("[data-testid='health-provider-anthropic']");
        Assert.Empty(cut.FindAll("[data-testid='health-providers-unavailable']"));

        // A broken provider must never be buried under healthy ones.
        var rows = cut.FindAll(".health-provider");
        Assert.Equal("health-provider-openai", rows[0].GetAttribute("data-testid"));
        AssertDotToken("bad", rows[0].QuerySelector(".health-dot")!.GetAttribute("class") ?? string.Empty);
        AssertDotToken("ok", rows[1].QuerySelector(".health-dot")!.GetAttribute("class") ?? string.Empty);
    }

    [Fact]
    public void Broken_provider_sorts_above_a_not_configured_one_rather_than_alphabetically_among_them()
    {
        // "zeta" is broken and sorts last alphabetically; under the old binary healthy/not-healthy
        // key it landed below the muted "alpha" row and the one actionable line on the card was the
        // hardest to find.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers", """[{"providerId":"alpha"},{"providerId":"zeta"},{"providerId":"anthropic"}]""");
        _handler.SetJson("api/providers/alpha/health", ProviderHealthJson(
            "alpha", status: "unhealthy", hasCredentials: false, reason: "notconfigured", error: "no key"));
        _handler.SetJson("api/providers/zeta/health", ProviderHealthJson(
            "zeta", status: "unhealthy", hasCredentials: true, reason: "credentialfailure", error: "502"));

        var cut = RenderLoaded();

        cut.WaitForAssertion(() => cut.Find("[data-testid='health-provider-zeta']"));
        var rows = cut.FindAll(".health-provider").Select(r => r.GetAttribute("data-testid")).ToList();
        Assert.Equal("health-provider-zeta", rows[0]);
        Assert.Equal("health-provider-anthropic", rows[1]);
        Assert.Equal("health-provider-alpha", rows[2]);
    }

    [Fact]
    public void Renders_gateway_uptime_and_diagnostics_metrics()
    {
        SetupHealthyBackend(AvailabilityJson());

        var cut = RenderLoaded();

        Assert.Equal("2h 0m", Text(cut, "health-uptime"));
        Assert.Equal("1", Text(cut, "health-active-loops"));
        Assert.Equal("420", Text(cut, "health-completed-loops"));
        Assert.Equal("Normal", Text(cut, "health-memory-level"));
        Assert.Equal("412 MB", Text(cut, "health-working-set"));
        Assert.Equal("Healthy", Text(cut, "health-threadpool-status"));
        Assert.Equal("Responsive", Text(cut, "health-activity-status"));
    }

    // ── 2. Empty / unavailable ─────────────────────────────────────────────

    [Fact]
    public void Every_card_shows_its_unavailable_placeholder_when_all_probes_404()
    {
        // No routes registered: the handler 404s everything.
        var cut = RenderLoaded();

        // The page still renders. A health page that blanks out when the backend is down is worse
        // than no health page at all, so this assertion is the point of the test.
        cut.Find("[data-testid='health-dashboard']");
        cut.Find("[data-testid='health-credentials-unavailable']");
        cut.Find("[data-testid='health-providers-unavailable']");
        cut.Find("[data-testid='health-activity-unavailable']");
        cut.Find("[data-testid='health-memory-unavailable']");
        cut.Find("[data-testid='health-threadpool-unavailable']");
        cut.Find("[data-testid='health-errors-unavailable']");

        Assert.Equal("Unreachable", Text(cut, "health-gateway-reachable"));
        Assert.Equal("Action required", Text(cut, "health-overall-status"));
        AssertDotToken("bad", DotClass(cut));
    }

    [Fact]
    public void Page_still_renders_when_every_probe_throws()
    {
        _handler.Throw("api/health/availability");
        _handler.Throw("api/diagnostics/memory-pressure");
        _handler.Throw("api/diagnostics/threadpool");
        _handler.Throw("api/diagnostics/activity");
        _handler.Throw("api/diagnostics/loops");
        _handler.Throw("api/diagnostics/log-patterns");
        _handler.Throw("api/gateway/info");
        _handler.Throw("api/providers");

        var cut = RenderLoaded();

        cut.Find("[data-testid='health-dashboard']");
        cut.Find("[data-testid='health-credentials-unavailable']");
        cut.Find("[data-testid='health-providers-unavailable']");
        Assert.Equal("\u2014", Text(cut, "health-uptime"));
    }

    [Fact]
    public void Provider_list_that_resolves_but_whose_health_probe_fails_still_lists_the_provider()
    {
        SetupHealthyBackend(AvailabilityJson());
        _handler.Throw("api/providers/openai/health");

        var cut = RenderLoaded();

        // An unknown-status provider is surfaced, not dropped: a provider that vanishes from the
        // list is indistinguishable from one that is fine.
        cut.WaitForAssertion(() => cut.Find("[data-testid='health-provider-openai']"));
        AssertDotToken("bad", ProviderDot(cut, "openai"));
        cut.Find("[data-testid='health-provider-anthropic']");
    }

    // ── 3. Error isolation (the core design guarantee) ─────────────────────

    [Fact]
    public void One_failing_probe_degrades_only_its_own_card()
    {
        SetupHealthyBackend(AvailabilityJson());
        _handler.Throw("api/diagnostics/memory-pressure");

        var cut = RenderLoaded();

        // The dead probe's card, and only that card, shows the placeholder.
        cut.Find("[data-testid='health-memory-unavailable']");
        Assert.Empty(cut.FindAll("[data-testid='health-credentials-unavailable']"));
        Assert.Empty(cut.FindAll("[data-testid='health-threadpool-unavailable']"));
        Assert.Empty(cut.FindAll("[data-testid='health-activity-unavailable']"));
        Assert.Empty(cut.FindAll("[data-testid='health-errors-unavailable']"));
        Assert.Empty(cut.FindAll("[data-testid='health-providers-unavailable']"));

        // Every surviving card still renders its real data rather than a dash.
        Assert.Equal("1h 0m", Text(cut, "health-token-ttl"));
        Assert.Equal("Healthy", Text(cut, "health-threadpool-status"));
        Assert.Equal("Responsive", Text(cut, "health-activity-status"));
        Assert.Equal("2h 0m", Text(cut, "health-uptime"));
        cut.Find("[data-testid='health-provider-openai']");

        // The gateway is still judged reachable off the probes that did answer.
        Assert.Equal("Reachable", Text(cut, "health-gateway-reachable"));
    }

    [Fact]
    public void A_missing_watchdog_feed_leaves_the_diagnostics_cards_intact()
    {
        // The watchdog is optional and 404s when not installed - the single most common real-world
        // partial failure, and it must not colour any other card. "Not installed" is the ONLY
        // availability outcome that stays green; see the two tests below for the ones that must not.
        SetupHealthyBackend(AvailabilityJson());
        _handler.NotFound("api/health/availability");

        var cut = RenderLoaded();

        Assert.Equal("Watchdog not reporting", Text(cut, "health-credentials-unavailable"));
        Assert.Equal("Normal", Text(cut, "health-memory-level"));
        Assert.Equal("Healthy", Text(cut, "health-threadpool-status"));
        Assert.Equal("Reachable", Text(cut, "health-gateway-reachable"));
        Assert.Equal("Healthy", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void An_availability_probe_that_errors_degrades_the_rollup_rather_than_reading_as_not_installed()
    {
        // A 500 from the watchdog is not "the watchdog is not installed". The availability feed is
        // the ONE sensor for a silent credential failure; if its own failure renders as the benign
        // not-installed case the page says "Healthy" precisely when it has gone blind.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/health/availability", """{"error":"boom"}""", HttpStatusCode.InternalServerError);

        var cut = RenderLoaded();

        cut.Find("[data-testid='health-credentials-unavailable']");
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void An_availability_probe_that_throws_degrades_the_rollup()
    {
        SetupHealthyBackend(AvailabilityJson());
        _handler.Throw("api/health/availability");

        var cut = RenderLoaded();

        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Watchdog_status_action_required_degrades_the_rollup_and_never_reads_healthy()
    {
        // The watchdog publishes `action-required` as a first-class status. It painted the
        // credentials card amber while the headline stayed green - the card and the headline
        // describing the same fact differently is the failure mode this pins shut.
        SetupHealthyBackend(AvailabilityJson(status: "action-required"));

        var cut = RenderLoaded();

        Assert.Equal("action-required", Text(cut, "health-credentials-status"));
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    // ── 4. Sign-in required ────────────────────────────────────────────────

    [Fact]
    public void Sign_in_required_renders_the_banner_with_the_link_and_the_code()
    {
        SetupHealthyBackend(AvailabilityJson(
            status: "signin",
            signInRequired: true,
            signInUri: "https://github.com/login/device",
            signInCode: "ABCD-1234"));

        var cut = RenderLoaded();

        cut.Find("[data-testid='health-signin']");
        var link = cut.Find("[data-testid='health-signin-link']");
        Assert.Equal("https://github.com/login/device", link.GetAttribute("href"));
        Assert.Equal("https://github.com/login/device", link.TextContent.Trim());
        Assert.Equal("ABCD-1234", Text(cut, "health-signin-code"));
    }

    [Fact]
    public void Sign_in_required_outranks_every_other_signal_in_the_rollup()
    {
        // Everything else is green. Sign-in is the only failure that cannot self-heal, so it must
        // still drive the rollup to the hard-failure state.
        SetupHealthyBackend(AvailabilityJson(
            signInRequired: true,
            signInUri: "https://github.com/login/device",
            signInCode: "ABCD-1234"));

        var cut = RenderLoaded();

        AssertDotToken("bad", DotClass(cut));
        Assert.Equal("Action required", Text(cut, "health-overall-status"));
        AssertDotToken("bad", cut.Find("[data-testid='health-card-credentials'] .health-dot").GetAttribute("class") ?? string.Empty);
    }

    [Fact]
    public void Sign_in_banner_is_absent_when_no_sign_in_is_required()
    {
        SetupHealthyBackend(AvailabilityJson());

        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll("[data-testid='health-signin']"));
    }

    // ── 5. Stale watchdog ──────────────────────────────────────────────────

    [Fact]
    public void Stale_watchdog_feed_is_marked_stale_and_degrades_the_rollup()
    {
        // A watchdog that stopped running publishes the same "healthy" payload forever. Without the
        // age check, a dead watchdog is indistinguishable from a working one - the exact failure
        // mode the credentials card exists to catch.
        SetupHealthyBackend(AvailabilityJson(checkedSecondsAgo: 901));

        var cut = RenderLoaded();

        Assert.Contains("STALE", Text(cut, "health-last-check"));
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Watchdog_just_inside_the_stale_window_is_not_marked_stale()
    {
        // Guards the boundary from the opposite side: a threshold that fires early would train
        // users to ignore the marker.
        SetupHealthyBackend(AvailabilityJson(checkedSecondsAgo: 60));

        var cut = RenderLoaded();

        Assert.DoesNotContain("STALE", Text(cut, "health-last-check"));
        AssertDotToken("ok", DotClass(cut));
        Assert.Equal("Healthy", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Watchdog_feed_with_an_unparseable_timestamp_is_treated_as_stale()
    {
        SetupHealthyBackend(
            """{"status":"healthy","checkedAt":"not-a-timestamp","tokenValidForSeconds":3600,"refreshMarginSeconds":300,"signInRequired":false,"restartsLastHour":0}""");

        var cut = RenderLoaded();

        Assert.Contains("STALE", Text(cut, "health-last-check"));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    // ── 6. Checking (pre-first-load) ───────────────────────────────────────

    [Fact]
    public void First_paint_says_checking_rather_than_flashing_a_false_outage()
    {
        // Completion of the very first probe is held open, so this asserts the state the user
        // actually sees on every page load. Before `_firstLoadComplete` existed, that paint was a
        // full grid of "Unavailable" under a red "Degraded" rollup - a false outage on every visit.
        SetupHealthyBackend(AvailabilityJson());
        var gate = _handler.Gate("api/health/availability");

        var cut = _ctx.Render<HealthDashboard>();

        Assert.Contains("checking", DotClass(cut));
        Assert.Equal("Checking\u2026", Text(cut, "health-overall-status"));
        Assert.Equal("checking\u2026", Text(cut, "health-updated"));
        Assert.Equal("Checking\u2026", Text(cut, "health-gateway-reachable"));
        Assert.Equal("Checking\u2026", Text(cut, "health-credentials-unavailable"));
        Assert.Equal("Checking\u2026", Text(cut, "health-providers-unavailable"));
        Assert.Equal("Checking\u2026", Text(cut, "health-memory-unavailable"));
        Assert.Equal("Checking\u2026", Text(cut, "health-threadpool-unavailable"));
        Assert.Equal("Checking\u2026", Text(cut, "health-activity-unavailable"));
        Assert.Equal("Checking\u2026", Text(cut, "health-errors-unavailable"));

        // Nothing on the first paint accuses the backend of being down.
        Assert.DoesNotContain("Unavailable", cut.Markup);
        Assert.DoesNotContain("Degraded", cut.Markup);
        Assert.DoesNotContain("Unreachable", cut.Markup);

        // Release the gate and confirm the same component settles into the real state, proving the
        // "checking" paint is a transient gate and not a stuck state.
        gate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("Healthy", Text(cut, "health-overall-status")));
    }

    [Fact]
    public void Refresh_button_is_disabled_while_a_load_is_in_flight()
    {
        SetupHealthyBackend(AvailabilityJson());
        var gate = _handler.Gate("api/health/availability");

        var cut = _ctx.Render<HealthDashboard>();

        Assert.True(cut.Find("[data-testid='health-refresh']").HasAttribute("disabled"));

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid='health-refresh']").HasAttribute("disabled")));
    }

    // ── User interaction ───────────────────────────────────────────────────

    [Fact]
    public void Refresh_button_reissues_the_probes_and_picks_up_the_new_state()
    {
        SetupHealthyBackend(AvailabilityJson());
        var cut = RenderLoaded();
        Assert.Equal("Healthy", Text(cut, "health-overall-status"));

        // The backend degrades between polls; the refresh affordance must observe it.
        _handler.SetJson("api/health/availability", AvailabilityJson(
            signInRequired: true, signInUri: "https://example.test/device", signInCode: "WXYZ-9999"));

        cut.Find("[data-testid='health-refresh']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='health-signin']"));
        Assert.Equal("Action required", Text(cut, "health-overall-status"));
        Assert.Equal("WXYZ-9999", Text(cut, "health-signin-code"));
    }

    // ── Disposal ───────────────────────────────────────────────────────────

    [Fact]
    public void Disposing_the_component_cancels_in_flight_probes_without_throwing()
    {
        SetupHealthyBackend(AvailabilityJson());
        var gate = _handler.Gate("api/health/availability");

        var cut = _ctx.Render<HealthDashboard>();
        Assert.Contains("checking", DotClass(cut));

        var exception = Record.Exception(() => cut.Instance.Dispose());
        Assert.Null(exception);

        gate.SetResult();
    }

    // ── 7. Provider health drives the rollup (incident: 4/5 red, rollup green) ──

    [Fact]
    public void Broken_provider_degrades_the_rollup_even_when_everything_else_is_green()
    {
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "unhealthy", hasCredentials: true, error: "upstream 502"));

        var cut = RenderLoaded();

        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Every_provider_unhealthy_is_an_action_required_outage()
    {
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson("openai", status: "unhealthy", error: "boom"));
        _handler.SetJson("api/providers/anthropic/health", ProviderHealthJson("anthropic", status: "unhealthy", error: "boom"));

        var cut = RenderLoaded();

        AssertDotToken("bad", DotClass(cut));
        Assert.Equal("Action required", Text(cut, "health-overall-status"));
    }

    /// <summary>
    /// A mock/unused provider without a key is a deployment choice, not an outage: it must not sit
    /// in the ratio's denominator. Counting it there pinned an install with two healthy providers
    /// serving all traffic and six deliberately keyless ones permanently amber, and turned a single
    /// healthy provider plus four keyless ones into "Action required".
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    public void Not_configured_providers_alone_do_not_degrade_the_rollup(int healthy, int notConfigured)
    {
        SetupHealthyBackend(AvailabilityJson());

        var ids = Enumerable.Range(0, healthy + notConfigured).Select(i => $"p{i}").ToList();
        _handler.SetJson(
            "api/providers",
            "[" + string.Join(",", ids.Select(id => $"{{\"providerId\":\"{id}\"}}")) + "]");

        for (var i = 0; i < ids.Count; i++)
        {
            _handler.SetJson(
                $"api/providers/{ids[i]}/health",
                i < healthy
                    ? ProviderHealthJson(ids[i])
                    : ProviderHealthJson(
                        ids[i], status: "unhealthy", hasCredentials: false, reason: "notconfigured",
                        error: "No API key or credential configured for this provider."));
        }

        var cut = RenderLoaded();

        AssertDotToken("ok", DotClass(cut));
        AssertNotDotToken("warn", DotClass(cut));
        AssertNotDotToken("bad", DotClass(cut));
        Assert.Equal("Healthy", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Not_a_single_usable_provider_is_an_outage_even_if_all_are_merely_unconfigured()
    {
        // Nothing can serve traffic, so the reason each provider is down stops mattering.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson("openai", status: "unhealthy", hasCredentials: false));
        _handler.SetJson("api/providers/anthropic/health", ProviderHealthJson("anthropic", status: "unhealthy", hasCredentials: false));

        var cut = RenderLoaded();

        AssertDotToken("bad", DotClass(cut));
        Assert.Equal("Action required", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Provider_list_that_fails_to_load_is_reported_as_degraded_not_healthy()
    {
        // Unknown is not healthy. Reporting green while the provider inventory is unreadable is
        // exactly the false all-clear this dashboard exists to prevent.
        SetupHealthyBackend(AvailabilityJson());
        _handler.Throw("api/providers");

        var cut = RenderLoaded();

        cut.Find("[data-testid='health-providers-unavailable']");
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    // ── 8. Provider card is actionable ─────────────────────────────────────

    [Fact]
    public void Unconfigured_provider_renders_a_distinct_muted_state_not_a_red_outage()
    {
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "unhealthy", hasCredentials: false, reason: "notconfigured",
            error: "No API key or credential configured for this provider."));

        var cut = RenderLoaded();

        var dot = ProviderDot(cut, "openai");
        AssertDotToken("muted", dot);
        AssertNotDotToken("warn", dot);
        AssertNotDotToken("bad", dot);
        Assert.Equal("not configured", Text(cut, "health-provider-openai-state"));
    }

    [Fact]
    public void No_amber_dot_may_appear_anywhere_under_a_green_headline()
    {
        // The structural rule, not one instance of it: if the headline says Healthy then NOTHING on
        // the page may be painted amber. The not-configured provider dot broke exactly this - the
        // dot said "warn" while the rollup deliberately excluded the same provider and said "ok" -
        // and two narrower tests pinned both halves so the suite stayed green through the lie.
        // Revert ProviderDotClass to its "warn" arm and this test fails on the page-wide scan.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers", """[{"providerId":"openai"},{"providerId":"mock"}]""");
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson("openai"));
        _handler.SetJson("api/providers/mock/health", ProviderHealthJson(
            "mock", status: "unhealthy", hasCredentials: false, reason: "notconfigured",
            error: "No API key or credential configured for this provider."));

        var cut = RenderLoaded();

        AssertDotToken("ok", DotClass(cut));
        Assert.Equal("Healthy", Text(cut, "health-overall-status"));

        var amber = cut.FindAll(".health-dot")
            .Where(el => (el.GetAttribute("class") ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains("warn"))
            .Select(el => el.GetAttribute("class"))
            .ToList();

        Assert.True(amber.Count == 0, $"Amber dot(s) rendered under a green headline: {string.Join(", ", amber)}");
    }

    // ── Round-6 regression guards ──────────────────────────────────────────

    [Fact]
    public void Availability_served_503_unreadable_degrades_the_rollup()
    {
        // The 503 {"status":"unreadable"} contract means the feed EXISTS and we cannot read it - a
        // broken sensor, never an absent one. Delete the `unreadable` arm from CredentialsDotClass
        // (or stop reading the body on an error status) and the card reads green/unknown and the
        // headline returns to "Healthy", failing all three assertions below.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/health/availability", """{"status":"unreadable"}""", HttpStatusCode.ServiceUnavailable);

        var cut = RenderLoaded();

        Assert.Equal("unreadable", Text(cut, "health-credentials-status"));
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Availability_probe_that_times_out_is_degraded_not_healthy()
    {
        // HttpClient.Timeout surfaces as TaskCanceledException with an inner TimeoutException on a
        // token WE never cancelled. Change ProbeAsync's catch(OperationCanceledException) back to
        // swallowing silently instead of `onProbeOutcome(!_cts.IsCancellationRequested)` and a
        // wedged gateway renders as the benign not-installed case: this test then reads "Healthy".
        SetupHealthyBackend(AvailabilityJson());
        _handler.Timeout("api/health/availability");

        var cut = RenderLoaded();

        cut.Find("[data-testid='health-credentials-unavailable']");
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Idle_activity_degrades_the_rollup()
    {
        // "Idle" is the most direct "agents are not responding" signal on the page. Remove the
        // `_activity?.IsHealthy == false` term from RollupClass and the card still says Idle in
        // amber while the headline says Healthy - the exact forbidden render, so this test fails.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson(
            "api/diagnostics/activity",
            """{"inactivitySeconds":5400,"isHealthy":false,"warningThresholdSeconds":900}""");

        var cut = RenderLoaded();

        Assert.Equal("Idle", Text(cut, "health-activity-status"));
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Diagnostics_endpoint_answering_200_with_html_degrades_the_rollup()
    {
        // A proxy or auth portal returning an HTML page under a 200 is the ONE input that only
        // `_diagnosticsProbeFailed` catches: ProbeAsync rejects the non-JSON media type and returns
        // null, the card paints "Unavailable", and without that flag folded into RollupClass the
        // headline stays green above it. Delete the flag and this test reads "Healthy".
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetContent("api/diagnostics/threadpool", "<html><body>Sign in</body></html>", "text/html");

        var cut = RenderLoaded();

        cut.Find("[data-testid='health-threadpool-unavailable']");
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void A_refresh_requested_mid_pass_is_queued_and_actually_runs()
    {
        // State 2 of the load gate - "a pass is running AND another was requested" - had no test at
        // all. Collapse the gate to a plain bool (or let the queued state be overwritten) and the
        // second request is silently dropped: the probe count stays at 1 and this fails. The user
        // impact is a Refresh click that visibly does nothing while the backend has already changed.
        SetupHealthyBackend(AvailabilityJson());
        var gate = _handler.Gate("api/health/availability");

        var cut = _ctx.Render<HealthDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(1, _handler.Started("api/health/availability")));

        // The disabled attribute is a UI affordance against double-clicks; the gate is the actual
        // serialization primitive and it is what is under test here, so the click is dispatched
        // past the affordance rather than through it.
        var refresh = cut.Find("[data-testid='health-refresh']");
        refresh.RemoveAttribute("disabled");
        refresh.Click();

        // Still exactly one request in flight: the second caller queued rather than fanning out.
        Assert.Equal(1, _handler.Started("api/health/availability"));

        gate.SetResult();

        // The queued pass must actually run once the owner's loop drains it.
        cut.WaitForAssertion(
            () => Assert.Equal(2, _handler.Started("api/health/availability")),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Broken_provider_renders_red_with_its_error_text_and_a_full_text_tooltip()
    {
        const string Error = "Upstream returned 502 Bad Gateway while refreshing the credential; retry scheduled in 30s";
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "unhealthy", hasCredentials: true, error: Error));

        var cut = RenderLoaded();

        AssertDotToken("bad", ProviderDot(cut, "openai"));
        var errorEl = cut.Find("[data-testid='health-provider-openai-error']");        Assert.NotEmpty(errorEl.TextContent.Trim());
        // The row is one line wide, so the visible text is truncated and the tooltip carries the
        // whole message - an error you cannot read is an error you cannot act on.
        Assert.Equal(Error, errorEl.GetAttribute("title"));
    }

    [Fact]
    public void Healthy_provider_renders_no_error_row()
    {
        SetupHealthyBackend(AvailabilityJson());

        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll("[data-testid='health-provider-openai-error']"));
        Assert.Equal("healthy", Text(cut, "health-provider-openai-state"));
    }

    [Fact]
    public void Model_count_is_rendered_for_an_unhealthy_provider_when_the_api_supplied_one()
    {
        // The API reports the model count even on a 503. Showing an em-dash there threw away the
        // one piece of evidence that distinguishes "no key" from "provider gone".
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "unhealthy", models: 4, hasCredentials: false, error: "no key"));

        var cut = RenderLoaded();

        Assert.Equal("4 models", Text(cut, "health-provider-openai-models"));
    }

    [Fact]
    public void Model_count_falls_back_to_a_dash_only_when_genuinely_null()
    {
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson("openai", models: null));

        var cut = RenderLoaded();

        Assert.Equal("\u2014 models", Text(cut, "health-provider-openai-models"));
    }

    // ── 9. Probe fan-out ───────────────────────────────────────────────────

    [Fact]
    public void Per_provider_probes_are_issued_concurrently_rather_than_one_after_another()
    {
        // Serially awaiting five ~220ms probes made the card the slowest thing on the page. Both
        // probes must be in flight at once, which a foreach/await can never achieve.
        SetupHealthyBackend(AvailabilityJson());
        var openai = _handler.Gate("api/providers/openai/health");
        var anthropic = _handler.Gate("api/providers/anthropic/health");

        var cut = _ctx.Render<HealthDashboard>();

        Assert.True(
            SpinUntil(() => _handler.Started("api/providers/openai/health") == 1
                         && _handler.Started("api/providers/anthropic/health") == 1),
            "Both provider health probes should be in flight simultaneously.");

        openai.SetResult();
        anthropic.SetResult();
        cut.WaitForAssertion(() => cut.Find("[data-testid='health-provider-openai']"));
    }

    // -- Reason-driven classification --------------------------------------

    [Fact]
    public void Provider_health_from_a_gateway_that_omits_reason_paints_broken_never_benign()
    {
        // An older gateway predates the `reason` field entirely. The conservative direction is to
        // over-report a fault; inferring "not configured" from its absence would render a live
        // outage as a muted row.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "unhealthy", hasCredentials: false, omitReason: true, error: "no key"));

        var cut = RenderLoaded();

        var dot = ProviderDot(cut, "openai");
        AssertDotToken("bad", dot);
        AssertNotDotToken("warn", dot);
    }

    [Fact]
    public void Provider_health_with_an_unrecognised_reason_paints_broken_never_benign()
    {
        // A future gateway may invent enum members. Anything not positively identified as healthy
        // or as a deliberate non-deployment is broken by construction.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "unhealthy", hasCredentials: false,
            reason: "somethingnobodyhasheardof", error: "no key"));

        var cut = RenderLoaded();

        var dot = ProviderDot(cut, "openai");
        AssertDotToken("bad", dot);
        AssertNotDotToken("warn", dot);
    }

    [Fact]
    public void Degraded_provider_is_labelled_degraded_and_painted_broken()
    {
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "degraded", reason: "unknown", error: "slow upstream"));

        var cut = RenderLoaded();

        Assert.Equal("degraded", Text(cut, "health-provider-openai-state"));
        AssertDotToken("bad", ProviderDot(cut, "openai"));
    }

    [Fact]
    public void Unknown_status_with_credentials_present_is_still_broken_not_unconfigured()
    {
        // hasCredentials is deliberately true here: the classification reads `reason`, never that
        // boolean, and an "unknown" status is not evidence of an all-clear.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "unknown", hasCredentials: true, reason: "unknown"));

        var cut = RenderLoaded();

        Assert.Equal("unknown", Text(cut, "health-provider-openai-state"));
        AssertDotToken("bad", ProviderDot(cut, "openai"));
    }

    [Fact]
    public void An_unrecognised_status_is_shown_raw_rather_than_relabelled()
    {
        // The operator has to be able to see the literal value a future gateway sent, not a word
        // the client invented for it.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers/openai/health", ProviderHealthJson(
            "openai", status: "quiescing", reason: "unknown"));

        var cut = RenderLoaded();

        Assert.Equal("unknown (quiescing)", Text(cut, "health-provider-openai-state"));
    }

    [Fact]
    public void A_gateway_with_no_providers_configured_is_an_outage_not_an_empty_card()
    {
        // Count == 0 here means the gateway genuinely has nothing that can serve a turn -
        // distinct from a provider-list probe that failed, which is merely degraded.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson("api/providers", "[]");

        var cut = RenderLoaded();

        Assert.Equal("No providers configured", Text(cut, "health-providers-unavailable"));
        AssertDotToken("bad", DotClass(cut));
        Assert.Equal("Action required", Text(cut, "health-overall-status"));
    }

    [Fact]
    public void Provider_list_entries_with_no_usable_identifier_are_counted_and_degrade_the_rollup()
    {
        // Every identity field null: there is no URL to build and no element key to use, so the
        // row must be dropped instead of colliding with another on an empty testid. Dropping it
        // SILENTLY is the bug: a gateway upgrade that renames the id field for a subset would take
        // four of five providers out of the denominator and the card would read a confident
        // "1/1 usable" under a green headline while 80% of capacity was invisible.
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson(
            "api/providers",
            """[{"providerId":null,"id":null,"name":null},{"providerId":"openai"}]""");

        var cut = RenderLoaded();

        Assert.Single(cut.FindAll(".health-provider"));
        cut.Find("[data-testid='health-provider-openai']");
        Assert.Equal("1/1 usable (1 unidentified)", Text(cut, "health-providers-usable"));
        AssertDotToken("warn", DotClass(cut));
        Assert.Equal("Degraded", Text(cut, "health-overall-status"));
    }

    // ── Non-2xx provider health bodies ─────────────────────────────────────

    [Fact]
    public void Provider_health_served_at_503_still_renders_its_reason_error_and_model_count()
    {
        // The client half of the 503 fix. Every unhealthy provider used to answer 503, and reading
        // the body only on 2xx rendered it as a blank "unknown" row - green-adjacent and useless -
        // while a suite served entirely over 200 stayed green.
        const string Error = "Credential resolution failed: refresh token expired";
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson(
            "api/providers/openai/health",
            ProviderHealthJson(
                "openai", status: "unhealthy", models: 7, hasCredentials: false,
                reason: "credentialfailure", error: Error),
            HttpStatusCode.ServiceUnavailable);

        var cut = RenderLoaded();

        AssertDotToken("bad", ProviderDot(cut, "openai"));
        Assert.Equal("unhealthy", Text(cut, "health-provider-openai-state"));
        Assert.Equal(Error, Text(cut, "health-provider-openai-error"));
        Assert.Equal("7 models", Text(cut, "health-provider-openai-models"));
    }

    [Fact]
    public void Provider_health_served_as_a_401_error_envelope_paints_broken_never_benign()
    {
        // An auth proxy answers a JSON envelope with none of the health fields, so the model
        // deserialises to all-nulls. All-nulls must never read as "fine": no status is not a
        // healthy status, and no reason is not "deliberately not configured".
        SetupHealthyBackend(AvailabilityJson());
        _handler.SetJson(
            "api/providers/openai/health",
            """{"error":"unauthorized","message":"API key rejected"}""",
            HttpStatusCode.Unauthorized);

        var cut = RenderLoaded();

        var dot = ProviderDot(cut, "openai");
        AssertDotToken("bad", dot);
        AssertNotDotToken("warn", dot);
        AssertNotDotToken("ok", dot);
        Assert.Equal("unknown", Text(cut, "health-provider-openai-state"));
        Assert.NotEqual("Healthy", Text(cut, "health-overall-status"));
    }

    /// <summary>
    /// The rollup thresholds are ratios over the CONFIGURED fleet, so they must be pinned at the
    /// boundaries rather than at one convenient count. The bad arm is <c>healthy * 4 &lt; considered</c>,
    /// so (4,1) is the last warn and (5,1) the first bad; without both rows a flipped comparison
    /// goes undetected. The unhealthy reason is a parameter because "deliberately keyless" and
    /// "broken" feed two different arms, and a table that only ever produces one of them cannot
    /// tell which arm supplied the answer.
    /// </summary>
    [Theory]
    // Broken (credential failure) fixtures: they count in the denominator AND trip the broken arm.
    [InlineData(1, 1, "credentialfailure", "ok")]
    [InlineData(1, 0, "credentialfailure", "bad")]
    [InlineData(2, 1, "credentialfailure", "warn")]
    [InlineData(3, 2, "credentialfailure", "warn")]
    [InlineData(4, 1, "credentialfailure", "warn")]
    [InlineData(5, 1, "credentialfailure", "bad")]
    [InlineData(8, 3, "credentialfailure", "warn")]
    [InlineData(10, 1, "credentialfailure", "bad")]
    [InlineData(5, 3, "credentialfailure", "warn")]
    // Not-configured fixtures: excluded from the denominator entirely, so the same shapes stay
    // green. A single row differing between the two blocks proves the arms are separable.
    [InlineData(1, 1, "notconfigured", "ok")]
    [InlineData(2, 1, "notconfigured", "ok")]
    [InlineData(3, 2, "notconfigured", "ok")]
    [InlineData(4, 1, "notconfigured", "ok")]
    [InlineData(5, 1, "notconfigured", "ok")]
    [InlineData(8, 3, "notconfigured", "ok")]
    [InlineData(10, 1, "notconfigured", "ok")]
    // Nothing configured at all is not an all-clear: there is no evidence either way.
    [InlineData(1, 0, "notconfigured", "warn")]
    [InlineData(4, 0, "notconfigured", "warn")]
    public void Provider_rollup_follows_the_ratio_truth_table(int total, int healthy, string reason, string expected)
    {
        SetupHealthyBackend(AvailabilityJson());

        var ids = Enumerable.Range(0, total).Select(i => $"p{i}").ToList();
        _handler.SetJson(
            "api/providers",
            "[" + string.Join(",", ids.Select(id => $"{{\"providerId\":\"{id}\"}}")) + "]");

        for (var i = 0; i < total; i++)
        {
            _handler.SetJson(
                $"api/providers/{ids[i]}/health",
                i < healthy
                    ? ProviderHealthJson(ids[i])
                    : ProviderHealthJson(ids[i], status: "unhealthy", hasCredentials: false, reason: reason));
        }

        var cut = RenderLoaded();

        AssertDotToken(expected, DotClass(cut));
        foreach (var other in new[] { "ok", "warn", "bad" }.Where(t => t != expected))
            AssertNotDotToken(other, DotClass(cut));
    }

    private static bool SpinUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    // ── Test double ────────────────────────────────────────────────────────

    /// <summary>
    /// Route-keyed stub for the dashboard's probes. Routes are matched on the exact relative path
    /// so <c>api/providers</c> and <c>api/providers/{id}/health</c> cannot shadow one another.
    /// Unregistered routes answer 404, which is the real behaviour for the optional endpoints.
    /// </summary>
    private sealed class HealthMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _json = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HttpStatusCode> _status = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _throws = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _timeouts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _contentType = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _notFound = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _started =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Requests that have entered the handler for a route, including ones still gated.</summary>
        public int Started(string path) => _started.TryGetValue(path, out var count) ? count : 0;

        public void SetJson(string path, string json) => SetJson(path, json, HttpStatusCode.OK);

        /// <summary>
        /// Serving a route at a non-2xx status is not an exotic case: <c>api/providers/{id}/health</c>
        /// answers 503 for every unhealthy provider, and its body carries the only actionable detail
        /// the card has. A handler that could only produce 200 hid that the client threw the body away.
        /// </summary>
        public void SetJson(string path, string json, HttpStatusCode status)
        {
            _json[path] = json;
            _status[path] = status;
            _contentType[path] = "application/json";
            _throws.Remove(path);
            _timeouts.Remove(path);
            _notFound.Remove(path);
        }

        public void Throw(string path)
        {
            _throws.Add(path);
            _notFound.Remove(path);
        }

        /// <summary>
        /// How <c>HttpClient.Timeout</c> actually surfaces: a TaskCanceledException whose inner
        /// exception is a TimeoutException, on a token the caller never cancelled. It is NOT a user
        /// cancellation, and a client that treats the two alike goes silently blind against a
        /// wedged gateway.
        /// </summary>
        public void Timeout(string path)
        {
            _timeouts.Add(path);
            _notFound.Remove(path);
        }

        /// <summary>
        /// Serves a 200 whose body is NOT JSON. A gateway behind a proxy that returns an HTML error
        /// page under a success status is the shape that would turn a half-parsed model into a
        /// confident-looking card, so the client must be able to meet it in a test.
        /// </summary>
        public void SetContent(string path, string body, string contentType)
        {
            _json[path] = body;
            _status[path] = HttpStatusCode.OK;
            _contentType[path] = contentType;
            _throws.Remove(path);
            _timeouts.Remove(path);
            _notFound.Remove(path);
        }

        public void NotFound(string path)
        {
            _notFound.Add(path);
            _throws.Remove(path);
        }

        /// <summary>
        /// Holds a route open until the returned source is completed, making the pre-first-load
        /// paint observable without any timing dependency.
        /// </summary>
        public TaskCompletionSource Gate(string path)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[path] = tcs;
            return tcs;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = (request.RequestUri?.AbsolutePath ?? string.Empty).TrimStart('/');
            _started.AddOrUpdate(path, 1, static (_, current) => current + 1);

            if (_gates.TryGetValue(path, out var gate))
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_throws.Contains(path))
                throw new HttpRequestException($"probe failure: {path}");

            if (_timeouts.Contains(path))
                throw new TaskCanceledException(null, new TimeoutException());

            if (_notFound.Contains(path) || !_json.TryGetValue(path, out var json))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(_status.TryGetValue(path, out var status) ? status : HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    System.Text.Encoding.UTF8,
                    _contentType.TryGetValue(path, out var contentType) ? contentType : "application/json")
            };
        }
    }
}
