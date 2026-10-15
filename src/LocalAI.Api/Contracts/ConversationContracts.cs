using LocalAI.Core.Models;

namespace LocalAI.Api.Contracts;

public sealed record CreateConversationRequest(string? Title = null);

public sealed record RenameConversationRequest(string? Title);

/// <summary>Body of POST /api/conversations/{id}/messages/stream. Only the new message is sent.</summary>
public sealed record SendMessageRequest(string? Model, string? Content, double? Temperature = null, int? MaxTokens = null);

/// <summary>Body of POST /api/conversations/{id}/regenerate/stream.</summary>
public sealed record RegenerateRequest(string? Model, double? Temperature = null, int? MaxTokens = null);

public sealed record ConversationSummaryDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? LastModel)
{
    public static ConversationSummaryDto From(ConversationSummary s) =>
        new(s.Id, s.Title, s.CreatedAt, s.UpdatedAt, s.MessageCount, s.LastModel);
}

public sealed record ConversationListResponse(IReadOnlyList<ConversationSummaryDto> Conversations);

public sealed record MessageErrorDto(string Code, string? Message);

public sealed record StoredMessageDto(
    Guid Id,
    int Sequence,
    ChatRole Role,
    string Content,
    MessageStatus Status,
    string? Model,
    MessageErrorDto? Error,
    CompletionStatsDto? Stats,
    DateTimeOffset CreatedAt)
{
    public static StoredMessageDto From(StoredMessage m) => new(
        m.Id,
        m.Sequence,
        m.Role,
        m.Content,
        m.Status,
        m.Model,
        m.ErrorCode is null ? null : new MessageErrorDto(m.ErrorCode, m.ErrorMessage),
        CompletionStatsDto.From(m.Stats),
        m.CreatedAt);
}

public sealed record ConversationDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<StoredMessageDto> Messages)
{
    public static ConversationDto From(Conversation c) =>
        new(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages.Select(StoredMessageDto.From).ToArray());
}
