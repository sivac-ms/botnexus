using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the archived-conversation view and its restore path on
/// <see cref="ConversationsController"/>.
///
/// These exist because bulk-archiving idle conversations is only defensible if it can be undone:
/// an archive with no way back is a delete wearing a softer word. The archived list is how a
/// conversation swept up by mistake is found again, and restore is how it comes back - so the
/// ordering guarantee and the undo are both load-bearing, not cosmetic.
/// </summary>
public sealed class ConversationsControllerArchivedListingTests
{
    private const string TestAgentId = "test-agent";

    [Fact]
    public async Task List_WithoutStatus_ReturnsOnlyActive()
    {
        var (controller, _) = await CreateControllerAsync();

        var result = await controller.List(TestAgentId, status: null, CancellationToken.None);

        var summaries = ExtractSummaries(result);
        summaries.ShouldNotBeEmpty();
        summaries.ShouldAllBe(s => s.Status == ConversationStatus.Active.ToString());
    }

    [Fact]
    public async Task List_WithArchivedStatus_ReturnsOnlyArchived()
    {
        var (controller, _) = await CreateControllerAsync();

        var result = await controller.List(TestAgentId, status: "Archived", CancellationToken.None);

        var summaries = ExtractSummaries(result);
        summaries.ShouldNotBeEmpty();
        summaries.ShouldAllBe(s => s.Status == ConversationStatus.Archived.ToString());
    }

    /// <summary>
    /// Recency order is the entire point of the archived view: the reason to open it is almost
    /// always that something was archived by mistake, and the mistake is nearly always the most
    /// recent thing in the list.
    /// </summary>
    [Fact]
    public async Task List_Archived_IsOrderedMostRecentlyArchivedFirst()
    {
        var (controller, _) = await CreateControllerAsync();

        var result = await controller.List(TestAgentId, status: "Archived", CancellationToken.None);

        var summaries = ExtractSummaries(result);
        summaries.Count.ShouldBeGreaterThan(1);
        summaries
            .Select(s => s.UpdatedAt)
            .ShouldBe(summaries.Select(s => s.UpdatedAt).OrderByDescending(x => x));
    }

    /// <summary>
    /// A typo must not silently return the active list. If it did, "the archive looks empty" and
    /// "you misspelled the filter" would be indistinguishable - and the operator would conclude
    /// their conversation was gone for good.
    /// </summary>
    [Fact]
    public async Task List_WithUnknownStatus_ReturnsBadRequest()
    {
        var (controller, _) = await CreateControllerAsync();

        var result = await controller.List(TestAgentId, status: "Archvied", CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task List_StatusFilterIsCaseInsensitive()
    {
        var (controller, _) = await CreateControllerAsync();

        var result = await controller.List(TestAgentId, status: "archived", CancellationToken.None);

        ExtractSummaries(result).ShouldAllBe(s => s.Status == ConversationStatus.Archived.ToString());
    }

    // ── Restore ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_ReturnsArchivedConversationToActive()
    {
        var (controller, store) = await CreateControllerAsync();

        var result = await controller.Restore("c_archived_1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        var restored = await store.GetAsync(ConversationId.From("c_archived_1"));
        restored!.Status.ShouldBe(ConversationStatus.Active);
    }

    /// <summary>
    /// Archiving sealed the active session. Re-pointing the conversation at a sealed session would
    /// hand the agent a transcript that ends mid-turn, so restore must leave it null and let the
    /// next inbound message open a fresh one.
    /// </summary>
    [Fact]
    public async Task Restore_DoesNotResurrectTheSealedSession()
    {
        var (controller, store) = await CreateControllerAsync();

        await controller.Restore("c_archived_1", CancellationToken.None);

        var restored = await store.GetAsync(ConversationId.From("c_archived_1"));
        restored!.ActiveSessionId.ShouldBeNull();
    }

    /// <summary>
    /// The restore button sits in a list that may be seconds stale, so a double click must not
    /// surface as a failure.
    /// </summary>
    [Fact]
    public async Task Restore_OnAlreadyActiveConversation_IsANoOp()
    {
        var (controller, store) = await CreateControllerAsync();

        var result = await controller.Restore("c_active_1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        var unchanged = await store.GetAsync(ConversationId.From("c_active_1"));
        unchanged!.Status.ShouldBe(ConversationStatus.Active);
    }

    [Fact]
    public async Task Restore_UnknownConversation_Returns404()
    {
        var (controller, _) = await CreateControllerAsync();

        var result = await controller.Restore("c_nonexistent", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Round-trip: a restored conversation must leave the archived list and reappear in the active
    /// one. Asserting only the stored status would pass even if the listing projections disagreed.
    /// </summary>
    [Fact]
    public async Task Restore_MovesTheConversationBetweenListings()
    {
        var (controller, _) = await CreateControllerAsync();

        await controller.Restore("c_archived_1", CancellationToken.None);

        var archived = ExtractSummaries(await controller.List(TestAgentId, "Archived", CancellationToken.None));
        var active = ExtractSummaries(await controller.List(TestAgentId, null, CancellationToken.None));

        archived.ShouldNotContain(s => s.ConversationId == "c_archived_1");
        active.ShouldContain(s => s.ConversationId == "c_archived_1");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────

    private static List<ConversationSummary> ExtractSummaries(ActionResult result)
    {
        var ok = result.ShouldBeOfType<OkObjectResult>();
        return ok.Value switch
        {
            List<ConversationSummary> list => list,
            IEnumerable<ConversationSummary> seq => seq.ToList(),
            _ => throw new InvalidOperationException($"Unexpected payload type {ok.Value?.GetType().Name}")
        };
    }

    private static async Task<(ConversationsController, InMemoryConversationStore)> CreateControllerAsync()
    {
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow;

        await store.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("c_active_1"),
            AgentId = AgentId.From(TestAgentId),
            Title = "Still active",
            Status = ConversationStatus.Active,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddMinutes(-5)
        });

        // Two archived rows with distinct timestamps so the ordering assertion is meaningful.
        await store.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("c_archived_1"),
            AgentId = AgentId.From(TestAgentId),
            Title = "Archived recently",
            Status = ConversationStatus.Archived,
            ActiveSessionId = null,
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now.AddMinutes(-2)
        });

        await store.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("c_archived_2"),
            AgentId = AgentId.From(TestAgentId),
            Title = "Archived a while ago",
            Status = ConversationStatus.Archived,
            ActiveSessionId = null,
            CreatedAt = now.AddDays(-9),
            UpdatedAt = now.AddDays(-4)
        });

        var sessions = new InMemorySessionStore();
        var controller = new ConversationsController(store, sessions);
        return (controller, store);
    }
}
