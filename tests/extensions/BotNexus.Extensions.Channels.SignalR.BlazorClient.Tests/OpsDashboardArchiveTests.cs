using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit tests for the Ops dashboard's idle/stalled split and the Archived tab.
///
/// Context, because it is the whole reason these tests exist: the first version of this board
/// classified any Active conversation silent for 15 minutes as "Stuck". On a real host that
/// labelled 214 of 412 conversations Stuck, including ones idle for 19 days and an agent that had
/// been retired on purpose - and it flagged the very conversation the operator was typing in.
/// An alarm that fires on the overwhelmingly common case trains the reader to ignore the board,
/// which is worse than having no board.
///
/// The behaviour under test is therefore not "it renders rows" but:
/// <list type="bullet">
///   <item>silence alone is idle, never an alarm;</item>
///   <item>silence WITH a live session is the genuine stall, and still alarms;</item>
///   <item>a failed archived probe never renders as "Nothing archived".</item>
/// </list>
/// </summary>
public sealed class OpsDashboardArchiveTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly OpsMockHandler _handler = new();

    public OpsDashboardArchiveTests()
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

    private static string Ago(TimeSpan span) =>
        DateTimeOffset.UtcNow.Subtract(span).ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="activeSessionId"/> is explicit in every fixture: it is the single field that
    /// separates "idle" from "stalled", so a fixture that left it implicit would re-encode the bug.
    /// </summary>
    private static string ConversationJson(
        string id,
        string agentId,
        string title,
        TimeSpan idleFor,
        string? activeSessionId = null,
        bool isAwaitingUser = false,
        string status = "Active") =>
        $$"""
        {
          "conversationId": "{{id}}",
          "agentId": "{{agentId}}",
          "title": "{{title}}",
          "status": "{{status}}",
          "source": "Channel",
          "activeSessionId": {{(activeSessionId is null ? "null" : $"\"{activeSessionId}\"")}},
          "isAwaitingUser": {{(isAwaitingUser ? "true" : "false")}},
          "updatedAt": "{{Ago(idleFor)}}"
        }
        """;

    private void SetupBackend(string conversationsJson, string? archivedJson = null)
    {
        _handler.SetJson("api/conversations", conversationsJson);
        _handler.SetJson("api/conversations/awaiting", """{"count":0,"conversations":[]}""");
        if (archivedJson is not null)
            _handler.SetJson("api/conversations?status=Archived", archivedJson);
    }

    // ── The core regression ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The exact shape that broke: idle for hours, no live session. Before the fix this rendered
    /// as an orange "Stuck" alarm. It must now read as a neutral idle chip.
    /// </summary>
    [Fact]
    public void SilentConversationWithNoLiveSession_RendersIdle_NotStuck()
    {
        SetupBackend($"[{ConversationJson("c1", "assistant", "Old thread", TimeSpan.FromHours(3))}]");

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() =>
            Assert.Contains(cut.FindAll("[data-testid=ops-status-pill]"), p => p.TextContent.Contains("idle")));

        var pill = cut.FindAll("[data-testid=ops-status-pill]")
            .First(p => p.TextContent.Contains("idle"));

        Assert.DoesNotContain("Stalled", pill.TextContent);
        Assert.Contains("ops-pill-idle", pill.ClassName);
    }

    /// <summary>
    /// The narrow case that IS worth an alarm: still bound to a live session, yet silent well past
    /// the threshold. If this ever stops alarming, the board has become decorative.
    /// </summary>
    [Fact]
    public void SilentConversationHoldingLiveSession_RendersStalled()
    {
        SetupBackend($"[{ConversationJson("c1", "assistant", "Hung run", TimeSpan.FromHours(3), activeSessionId: "s-live")}]");

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() =>
            Assert.Contains(cut.FindAll("[data-testid=ops-status-pill]"), p => p.TextContent.Contains("Stalled")));

        var pill = cut.FindAll("[data-testid=ops-status-pill]")
            .First(p => p.TextContent.Contains("Stalled"));

        Assert.Contains("ops-pill-stuck", pill.ClassName);
    }

    /// <summary>
    /// A conversation being used right now must never be flagged, which is precisely what the
    /// original heuristic did to the operator's own live session.
    /// </summary>
    [Fact]
    public void RecentlyActiveConversation_IsNeitherIdleNorStalled()
    {
        SetupBackend($"[{ConversationJson("c1", "assistant", "Live", TimeSpan.FromMinutes(1), activeSessionId: "s-live")}]");

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=ops-status-pill]")));

        var text = cut.Find("[data-testid=ops-status-pill]").TextContent;
        Assert.DoesNotContain("Stalled", text);
        Assert.DoesNotContain("idle", text);
    }

    /// <summary>
    /// Waiting-on-you outranks both: a blocked conversation is Active with a stale UpdatedAt, so
    /// evaluating idleness first would bury the one row that needs a human.
    /// </summary>
    [Fact]
    public void AwaitingUserConversation_OutranksIdle()
    {
        SetupBackend($"[{ConversationJson("c1", "assistant", "Blocked", TimeSpan.FromHours(5), isAwaitingUser: true)}]");

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() =>
            Assert.Contains(cut.FindAll("[data-testid=ops-status-pill]"), p => p.TextContent.Contains("Waiting")));
    }

    /// <summary>
    /// The notification line must not call idle conversations a problem. It is housekeeping copy,
    /// and it is what the operator reads before deciding whether to intervene.
    /// </summary>
    [Fact]
    public void IdleConversations_DoNotRaiseANeedsHelpNotification()
    {
        SetupBackend($"[{ConversationJson("c1", "assistant", "Old", TimeSpan.FromDays(9))}]");

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=ops-status-pill]")));

        var markup = cut.Markup;
        Assert.Contains("idle and still open", markup);
        Assert.DoesNotContain("produced nothing for over", markup);
    }

    // ── Archived tab ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchivedTab_ListsArchivedConversations()
    {
        SetupBackend(
            "[]",
            $"[{ConversationJson("a1", "assistant", "Swept by mistake", TimeSpan.FromMinutes(2), status: "Archived")}]");

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-tab-archived]")));

        cut.Find("[data-testid=ops-tab-archived]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Swept by mistake", cut.Find("[data-testid=ops-archived-title]").TextContent));
    }

    /// <summary>
    /// The green-when-blind guard, same family as the HealthDashboard 500-with-a-body bug: a failed
    /// probe must never be rendered as the reassuring "Nothing archived" empty state. If it is, a
    /// conversation archived by mistake becomes unfindable exactly when the backend is unwell.
    /// </summary>
    [Fact]
    public void ArchivedTab_WhenProbeFails_ReportsUnavailableRatherThanEmpty()
    {
        SetupBackend("[]");
        _handler.Throw("api/conversations?status=Archived");

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-tab-archived]")));

        cut.Find("[data-testid=ops-tab-archived]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-archived-unavailable]")));
        Assert.Empty(cut.FindAll("[data-testid=ops-archived-empty]"));
    }

    [Fact]
    public void ArchivedTab_WhenGenuinelyEmpty_SaysNothingArchived()
    {
        SetupBackend("[]", "[]");

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-tab-archived]")));

        cut.Find("[data-testid=ops-tab-archived]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-archived-empty]")));
    }

    /// <summary>
    /// Restore is the undo that makes bulk archiving defensible at all; without it the sweep is a
    /// delete with better manners.
    /// </summary>
    [Fact]
    public void Restore_RemovesTheRowFromTheArchivedList()
    {
        SetupBackend(
            "[]",
            $"[{ConversationJson("a1", "assistant", "Bring me back", TimeSpan.FromMinutes(2), status: "Archived")}]");
        _handler.SetJson("api/conversations/a1/restore", "", HttpStatusCode.NoContent);

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-tab-archived]")));
        cut.Find("[data-testid=ops-tab-archived]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-archived-restore]")));

        cut.Find("[data-testid=ops-archived-restore]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-archived-empty]")));
    }

    /// <summary>
    /// A failed restore must leave the row in place. Optimistically removing it would tell the
    /// operator the conversation is back when it is still archived - the worst outcome for a
    /// feature whose entire purpose is recovering something lost.
    /// </summary>
    [Fact]
    public void Restore_WhenServerRejects_LeavesTheRowVisible()
    {
        SetupBackend(
            "[]",
            $"[{ConversationJson("a1", "assistant", "Stays put", TimeSpan.FromMinutes(2), status: "Archived")}]");
        _handler.SetJson("api/conversations/a1/restore", "", HttpStatusCode.InternalServerError);

        var cut = _ctx.Render<OpsDashboard>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-tab-archived]")));
        cut.Find("[data-testid=ops-tab-archived]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=ops-archived-restore]")));

        cut.Find("[data-testid=ops-archived-restore]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Stays put", cut.Find("[data-testid=ops-archived-title]").TextContent));
    }

    // ── Handler ──────────────────────────────────────────────────────────────────────────

    private sealed class OpsMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _json = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HttpStatusCode> _status = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _throws = new(StringComparer.OrdinalIgnoreCase);

        public void SetJson(string path, string json) => SetJson(path, json, HttpStatusCode.OK);

        public void SetJson(string path, string json, HttpStatusCode status)
        {
            _json[path] = json;
            _status[path] = status;
            _throws.Remove(path);
        }

        public void Throw(string path) => _throws.Add(path);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Match on path+query so "api/conversations" and "api/conversations?status=Archived"
            // are distinct routes - the whole point of the archived view is that it is a different
            // query against the same path.
            var key = (request.RequestUri?.PathAndQuery ?? string.Empty).TrimStart('/');

            if (_throws.Contains(key))
                throw new HttpRequestException($"simulated transport failure for {key}");

            if (_json.TryGetValue(key, out var body))
            {
                var status = _status.TryGetValue(key, out var s) ? s : HttpStatusCode.OK;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
