namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Non-sensitive projection of a conversation's durable pending <c>ask_user</c> checkpoint
/// (<c>Conversation.PendingAskUserJson</c>), for the "waiting on you" surface (Ops dashboard).
/// </summary>
/// <remarks>
/// <para>
/// The raw checkpoint is a serialized <c>AskUserRequest</c> and may carry tool/session internals,
/// so it is deliberately NOT surfaced here. Only the fields a human needs in order to recognise
/// the question and decide to answer it are projected: the prompt text, the input type, and the
/// choice labels. The correlation <c>requestId</c> is withheld - answering happens through the
/// conversation, not through this read model.
/// </para>
/// <para>
/// <see cref="PromptText"/> is truncated to <see cref="AwaitingUserProjection.MaxPromptLength"/>
/// characters. A pending prompt can be arbitrarily long (an agent may paste a diff into it) and
/// this payload is polled; the dashboard renders a summary line and links through for the full
/// text.
/// </para>
/// </remarks>
/// <param name="ConversationId">Conversation holding the pending prompt.</param>
/// <param name="AgentId">Agent that asked the question.</param>
/// <param name="Title">Conversation display title, so the row is recognisable without a second call.</param>
/// <param name="PromptText">The question presented to the user, truncated.</param>
/// <param name="Truncated">True when <paramref name="PromptText"/> was shortened.</param>
/// <param name="InputType">Raw input-type token (<c>FreeForm</c>, <c>SingleChoice</c>, ...).</param>
/// <param name="Choices">Display labels of the offered choices, when the prompt is choice-based.</param>
/// <param name="AwaitingSince">
/// Best available approximation of when the prompt was raised. The persisted checkpoint carries no
/// asked-at timestamp, so this is the conversation's <c>UpdatedAt</c> - writing the checkpoint is
/// what last touched the row. It is therefore an upper bound on freshness, not an exact ask time,
/// and it moves if the conversation is touched again while still waiting.
/// </param>
/// <param name="Source">Origination trigger of the conversation (<c>Channel</c>, <c>Cron</c>, <c>Webhook</c>, <c>Agent</c>).</param>
public sealed record AwaitingUserConversation(
    string ConversationId,
    string AgentId,
    string Title,
    string PromptText,
    bool Truncated,
    string InputType,
    IReadOnlyList<string> Choices,
    DateTimeOffset AwaitingSince,
    string Source);

/// <summary>Response body for <c>GET /api/conversations/awaiting</c>.</summary>
/// <param name="Count">Number of conversations with a pending prompt.</param>
/// <param name="Conversations">Oldest-waiting first, so the most overdue question is at the top.</param>
public sealed record AwaitingUserResponse(
    int Count,
    IReadOnlyList<AwaitingUserConversation> Conversations);
