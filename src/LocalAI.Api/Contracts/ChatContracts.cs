using LocalAI.Core.Models;

namespace LocalAI.Api.Contracts;

/// <summary>Body of POST /api/chat and POST /api/chat/stream.</summary>
public sealed record ChatRequest(
    string? Model,
    IReadOnlyList<ChatMessageDto>? Messages,
    double? Temperature = null,
    int? MaxTokens = null)
{
    public UnvalidatedChatRequest ToUnvalidated() => new(
        Model,
        Messages?.Select(m => m is null ? null! : new UnvalidatedChatMessage(m.Role, m.Content)).ToArray(),
        Temperature,
        MaxTokens);
}

public sealed record ChatMessageDto(string? Role, string? Content);

public sealed record ChatResponse(
    string Model,
    ChatMessageDto Message,
    string? FinishReason,
    CompletionStatsDto? Stats);

/// <summary>Provider-reported measurements. Null means the provider did not report the value.</summary>
public sealed record CompletionStatsDto(
    int? PromptTokens,
    int? CompletionTokens,
    double? TotalDurationMs,
    double? LoadDurationMs,
    double? GenerationDurationMs,
    double? TokensPerSecond)
{
    public static CompletionStatsDto? From(ChatCompletionStats? stats) => stats is null
        ? null
        : new CompletionStatsDto(
            stats.PromptTokens,
            stats.CompletionTokens,
            stats.TotalDuration?.TotalMilliseconds,
            stats.LoadDuration?.TotalMilliseconds,
            stats.GenerationDuration?.TotalMilliseconds,
            stats.TokensPerSecond is { } tps ? Math.Round(tps, 2) : null);
}

// Server-Sent Event payloads for POST /api/chat/stream (see docs/api.md).

/// <summary><see cref="ConversationId"/> is set only for conversation streams.</summary>
public sealed record StreamStartEvent(string RequestId, string Model, DateTimeOffset CreatedAt, Guid? ConversationId = null);

public sealed record StreamDeltaEvent(string Content);

public sealed record StreamDoneEvent(string? FinishReason, CompletionStatsDto? Stats);

public sealed record StreamErrorEvent(string Code, string Message, bool Retryable);
