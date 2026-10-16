namespace LocalAI.Core.Models;

/// <summary>Outcome of a stored assistant turn. User messages are always <see cref="Complete"/>.</summary>
public enum MessageStatus
{
    Complete,
    Stopped,
    Error,
}

public sealed record StoredMessage(
    Guid Id,
    int Sequence,
    ChatRole Role,
    string Content,
    MessageStatus Status,
    string? Model,
    string? ErrorCode,
    string? ErrorMessage,
    ChatCompletionStats? Stats,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MessageSource>? Sources = null);

public sealed record NewMessage(
    ChatRole Role,
    string Content,
    MessageStatus Status,
    string? Model = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    ChatCompletionStats? Stats = null,
    IReadOnlyList<MessageSource>? Sources = null);

public sealed record Conversation(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<StoredMessage> Messages);

public sealed record ConversationSummary(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? LastModel);
