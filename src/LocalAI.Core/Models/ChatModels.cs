namespace LocalAI.Core.Models;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

public sealed record ChatMessage(ChatRole Role, string Content);

/// <summary>Sampling options forwarded to the provider. Null means "provider default".</summary>
public sealed record GenerationOptions(double? Temperature, int MaxTokens);

/// <summary>A validated, provider-agnostic chat completion request.</summary>
public sealed record ChatCompletionRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    GenerationOptions Options);

/// <summary>Untrusted chat input as received from a client, before validation.</summary>
public sealed record UnvalidatedChatRequest(
    string? Model,
    IReadOnlyList<UnvalidatedChatMessage>? Messages,
    double? Temperature,
    int? MaxTokens);

public sealed record UnvalidatedChatMessage(string? Role, string? Content);

/// <summary>
/// Measurements reported by the provider for a finished generation. Every field is
/// nullable because providers are not required to report them.
/// </summary>
public sealed record ChatCompletionStats(
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan? TotalDuration,
    TimeSpan? LoadDuration,
    TimeSpan? GenerationDuration)
{
    public double? TokensPerSecond =>
        CompletionTokens is > 0 && GenerationDuration is { TotalSeconds: > 0 } d
            ? CompletionTokens.Value / d.TotalSeconds
            : null;
}

/// <summary>
/// One incremental update from a streaming generation. The final chunk has
/// <see cref="IsFinal"/> set and may carry completion statistics.
/// </summary>
public sealed record ChatStreamChunk(
    string Content,
    bool IsFinal = false,
    string? FinishReason = null,
    ChatCompletionStats? Stats = null);

public sealed record ChatCompletionResult(
    string Model,
    string Content,
    string? FinishReason,
    ChatCompletionStats? Stats);
