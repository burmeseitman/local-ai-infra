using LocalAI.Core.Models;

namespace LocalAI.Core.Interfaces;

/// <summary>
/// Abstraction over a local inference backend. Implementations must translate
/// transport/protocol failures into <see cref="Exceptions.InferenceException"/> and
/// must surface caller cancellation as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IChatProvider
{
    /// <summary>Stable provider identifier, e.g. "ollama".</summary>
    string Name { get; }

    /// <summary>Cheap connectivity probe. Never throws for connectivity failures.</summary>
    Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<ChatStreamChunk> StreamChatAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken);
}
