using System.Diagnostics;
using System.Runtime.CompilerServices;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using Microsoft.Extensions.Options;

namespace LocalAI.Providers.Ollama;

/// <summary>Maps the Ollama wire protocol onto the provider-agnostic <see cref="IChatProvider"/>.</summary>
internal sealed class OllamaProvider(OllamaClient client, IOptions<OllamaOptions> options) : IChatProvider
{
    public string Name => "ollama";

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var version = await client.GetVersionAsync(cancellationToken);
            return new ProviderHealth(true, version.Version, stopwatch.Elapsed, null);
        }
        catch (InferenceException ex)
        {
            return new ProviderHealth(false, null, stopwatch.Elapsed, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var response = await client.ListModelsAsync(cancellationToken);
        if (response.Models is null)
        {
            throw InferenceException.InvalidResponse("Ollama /api/tags response has no 'models' array.");
        }

        return response.Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Name ?? m.Model))
            .Select(ToModelInfo)
            .ToArray();
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamChatAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wireRequest = new OllamaChatRequest
        {
            Model = request.Model,
            Messages = request.Messages
                .Select(m => new OllamaMessage { Role = ToWireRole(m.Role), Content = m.Content })
                .ToArray(),
            Options = new OllamaGenerationOptions
            {
                Temperature = request.Options.Temperature,
                NumPredict = request.Options.MaxTokens,
            },
            KeepAlive = string.IsNullOrWhiteSpace(options.Value.KeepAlive) ? null : options.Value.KeepAlive,
        };

        await foreach (var chunk in client.StreamChatAsync(wireRequest, cancellationToken))
        {
            var content = chunk.Message?.Content ?? string.Empty;
            yield return chunk.Done
                ? new ChatStreamChunk(content, IsFinal: true, chunk.DoneReason, ToStats(chunk))
                : new ChatStreamChunk(content);
        }
    }

    internal static ModelInfo ToModelInfo(OllamaModelEntry entry)
    {
        var name = (entry.Name ?? entry.Model)!;
        var separator = name.LastIndexOf(':');
        var (displayName, tag) = separator > 0 ? (name[..separator], name[(separator + 1)..]) : (name, null);
        var digest = entry.Digest is { Length: >= 12 } d ? d[..12] : entry.Digest;
        var isRemote = !string.IsNullOrEmpty(entry.RemoteHost) || !string.IsNullOrEmpty(entry.RemoteModel);

        return new ModelInfo(
            name,
            displayName,
            tag,
            NullIfEmpty(entry.Details?.Family),
            NullIfEmpty(digest),
            // Remote models report a placeholder size; don't present it as a local footprint.
            isRemote || entry.Size is null or <= 0 ? null : entry.Size,
            entry.ModifiedAt,
            NullIfEmpty(entry.Details?.ParameterSize),
            NullIfEmpty(entry.Details?.QuantizationLevel),
            isRemote);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ChatCompletionStats ToStats(OllamaChatChunk chunk) => new(
        chunk.PromptEvalCount,
        chunk.EvalCount,
        FromNanoseconds(chunk.TotalDuration),
        FromNanoseconds(chunk.LoadDuration),
        FromNanoseconds(chunk.EvalDuration));

    private static TimeSpan? FromNanoseconds(long? value) =>
        value is > 0 ? TimeSpan.FromTicks(value.Value / 100) : null;

    private static string ToWireRole(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };
}
