using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Projects a conversation's durable pending <c>ask_user</c> checkpoint onto the non-sensitive
/// <see cref="AwaitingUserConversation"/> read model used by the Ops dashboard "Waiting on You" row.
/// </summary>
/// <remarks>
/// Kept as a separate static class rather than inlined in the controller so the truncation and
/// redaction rules are unit-testable and applied identically wherever the projection is emitted.
/// </remarks>
public static class AwaitingUserProjection
{
    /// <summary>Maximum prompt characters emitted on the wire.</summary>
    public const int MaxPromptLength = 400;

    /// <summary>
    /// Attempts to project a pending prompt for <paramref name="conversation"/>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when nothing is pending, or when the persisted checkpoint is missing,
    /// malformed, or forward-versioned. A checkpoint that cannot be parsed is deliberately treated
    /// as "not waiting" rather than surfaced as a broken row: the dashboard's job is to show
    /// answerable questions, and an unparseable payload is not one.
    /// </returns>
    public static AwaitingUserConversation? TryProject(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (string.IsNullOrEmpty(conversation.PendingAskUserJson))
            return null;

        if (!AskUserPromptNormalizer.TryBuildFromPersistedJson(
                conversation.PendingAskUserJson,
                conversation.ConversationId.Value,
                out var prompt))
        {
            return null;
        }

        var text = prompt.Prompt ?? string.Empty;
        var truncated = text.Length > MaxPromptLength;
        if (truncated)
            text = text[..MaxPromptLength];

        // Labels only. Values are machine tokens and descriptions can restate tool internals.
        var choices = prompt.Choices is { Count: > 0 }
            ? prompt.Choices.Select(c => c.Label).ToList()
            : (IReadOnlyList<string>)[];

        return new AwaitingUserConversation(
            ConversationId: conversation.ConversationId.Value,
            AgentId: conversation.AgentId.Value,
            Title: conversation.Title,
            PromptText: text,
            Truncated: truncated,
            InputType: prompt.InputType,
            Choices: choices,
            AwaitingSince: conversation.UpdatedAt,
            Source: conversation.Source.ToString());
    }
}
