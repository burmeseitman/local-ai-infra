using System.Runtime.CompilerServices;
using System.Text;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalAI.Core.Services;

/// <summary>
/// Orchestrates a chat generation: model resolution, metrics, and streaming from the
/// provider. Prompt and response text are never logged.
/// </summary>
public sealed class ChatService(
    IChatProvider provider,
    ModelCatalog catalog,
    InferenceMetrics metrics,
    ILogger<ChatService> logger)
{
    /// <summary>
    /// Resolves the requested model before any response is started, so failures can be
    /// reported as regular HTTP errors rather than mid-stream events.
    /// </summary>
    public async Task<ChatCompletionRequest> PrepareAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        metrics.RecordRequest();
        try
        {
            var model = await catalog.ResolveAsync(request.Model, cancellationToken);
            return request with { Model = model.Name };
        }
        catch (InferenceException ex)
        {
            metrics.RecordFailure(ex.Code, request.Model);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Starting generation with model {Model}: {MessageCount} messages, {CharCount} chars, maxTokens {MaxTokens}",
            request.Model, request.Messages.Count, request.Messages.Sum(m => m.Content.Length), request.Options.MaxTokens);

        using var scope = metrics.BeginGeneration(request.Model);
        await using var stream = provider.StreamChatAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatStreamChunk chunk;
            try
            {
                if (!await stream.MoveNextAsync())
                {
                    // Providers must end with a final chunk; treat silence as a protocol error.
                    throw InferenceException.InvalidResponse("Stream completed without a final chunk.");
                }

                chunk = stream.Current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Generation with model {Model} was cancelled by the client", request.Model);
                scope.Cancelled();
                throw;
            }
            catch (InferenceException ex)
            {
                logger.LogWarning(
                    "Generation with model {Model} failed: {Code} ({Detail})",
                    request.Model, ex.Code, ex.ProviderDetail ?? ex.Message);
                scope.Failed(InferenceMetrics.ToCode(ex.Code));
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure during generation with model {Model}", request.Model);
                scope.Failed("internal_error");
                throw;
            }

            if (chunk.IsFinal)
            {
                scope.Succeeded(chunk.Stats);
                logger.LogInformation(
                    "Generation with model {Model} completed: reason {Reason}, {Tokens} tokens, {TokensPerSecond:0.0} tok/s",
                    request.Model, chunk.FinishReason, chunk.Stats?.CompletionTokens, chunk.Stats?.TokensPerSecond);
            }

            yield return chunk;

            if (chunk.IsFinal)
            {
                yield break;
            }
        }
    }

    /// <summary>Non-streaming convenience wrapper that buffers the full response.</summary>
    public async Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        ChatStreamChunk? final = null;
        await foreach (var chunk in StreamAsync(request, cancellationToken))
        {
            content.Append(chunk.Content);
            if (chunk.IsFinal)
            {
                final = chunk;
            }
        }

        return new ChatCompletionResult(request.Model, content.ToString(), final?.FinishReason, final?.Stats);
    }
}
