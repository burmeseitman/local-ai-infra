using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LocalAI.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Providers.Ollama;

/// <summary>
/// Thin HTTP client for the Ollama API. Owns timeouts and translates every transport or
/// protocol failure into <see cref="InferenceException"/>. Caller cancellation is always
/// surfaced as <see cref="OperationCanceledException"/>, never as a timeout.
/// </summary>
internal sealed class OllamaClient(
    IHttpClientFactory httpClientFactory,
    IOptions<OllamaOptions> options,
    ILogger<OllamaClient> logger)
{
    public const string HttpClientName = "ollama";
    private const int MaxErrorBodyBytes = 16 * 1024;

    // Factory-created clients are cheap; the pooled handler is shared and rotated.
    private HttpClient Http => httpClientFactory.CreateClient(HttpClientName);

    public Task<OllamaVersionResponse> GetVersionAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<OllamaVersionResponse>("api/version", cancellationToken);

    public Task<OllamaTagsResponse> ListModelsAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<OllamaTagsResponse>("api/tags", cancellationToken);

    public async Task<OllamaEmbedResponse> EmbedAsync(OllamaEmbedRequest request, CancellationToken cancellationToken)
    {
        var limit = options.Value.InferenceTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        return await Guard(
            async () =>
            {
                using var response = await Http.PostAsJsonAsync("api/embed", request, OllamaJson.Options, timeout.Token);
                await EnsureSuccessAsync(response, request.Model, timeout.Token);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                try
                {
                    return await JsonSerializer.DeserializeAsync<OllamaEmbedResponse>(stream, OllamaJson.Options, timeout.Token)
                        ?? throw InferenceException.InvalidResponse("Ollama returned an empty embedding response.");
                }
                catch (JsonException ex)
                {
                    throw InferenceException.InvalidResponse($"Malformed embedding response: {ex.Message}", ex);
                }
            },
            limit,
            cancellationToken);
    }

    public async IAsyncEnumerable<OllamaChatChunk> StreamChatAsync(
        OllamaChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.InferenceTimeout);

        using var response = await SendChatAsync(request, settings.InferenceTimeout, cancellationToken, timeout.Token);
        await using var body = await Guard(
            () => response.Content.ReadAsStreamAsync(timeout.Token), settings.InferenceTimeout, cancellationToken);
        var reader = new NdjsonLineReader(body, settings.MaxResponseLineBytes);

        while (true)
        {
            var line = await Guard(
                () => reader.ReadLineAsync(timeout.Token).AsTask(), settings.InferenceTimeout, cancellationToken);
            if (line is null)
            {
                throw InferenceException.InvalidResponse("Ollama stream ended before a 'done' message.");
            }

            if (line.Length == 0)
            {
                continue;
            }

            var chunk = ParseChunk(line);
            if (!string.IsNullOrEmpty(chunk.Error))
            {
                throw MapError(HttpStatusCode.InternalServerError, chunk.Error, request.Model);
            }

            yield return chunk;

            if (chunk.Done)
            {
                yield break;
            }
        }
    }

    private async Task<HttpResponseMessage> SendChatAsync(
        OllamaChatRequest request, TimeSpan limit, CancellationToken callerToken, CancellationToken timeoutToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(request, options: OllamaJson.Options),
        };

        var response = await Guard(
            () => Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutToken), limit, callerToken);
        try
        {
            await EnsureSuccessAsync(response, request.Model, timeoutToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        var limit = options.Value.MetadataTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);

        return await Guard(
            async () =>
            {
                using var response = await Http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                await EnsureSuccessAsync(response, model: null, timeout.Token);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                try
                {
                    return await JsonSerializer.DeserializeAsync<T>(stream, OllamaJson.Options, timeout.Token)
                        ?? throw InferenceException.InvalidResponse($"Ollama returned an empty body for {path}.");
                }
                catch (JsonException ex)
                {
                    throw InferenceException.InvalidResponse($"Malformed JSON from {path}: {ex.Message}", ex);
                }
            },
            limit,
            cancellationToken);
    }

    /// <summary>Normalizes transport exceptions and distinguishes timeouts from caller cancellation.</summary>
    private async Task<T> Guard<T>(Func<Task<T>> action, TimeSpan limit, CancellationToken callerToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Ollama request exceeded its {Timeout} time limit", limit);
            throw InferenceException.Timeout(limit);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Ollama is unreachable at {BaseUrl}: {Error}", options.Value.BaseUrl, ex.Message);
            throw InferenceException.Unavailable(ex);
        }
        catch (IOException ex) when (!callerToken.IsCancellationRequested)
        {
            // Connection reset mid-stream (e.g. the Ollama container was stopped).
            throw InferenceException.Unavailable(ex);
        }
        catch (InvalidDataException ex)
        {
            throw InferenceException.InvalidResponse(ex.Message, ex);
        }
    }

    private static OllamaChatChunk ParseChunk(byte[] line)
    {
        try
        {
            return JsonSerializer.Deserialize<OllamaChatChunk>(line, OllamaJson.Options)
                ?? throw InferenceException.InvalidResponse("Ollama sent a null stream message.");
        }
        catch (JsonException ex)
        {
            throw InferenceException.InvalidResponse($"Malformed stream message: {ex.Message}", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string? model, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await ReadErrorAsync(response, cancellationToken);
        throw MapError(response.StatusCode, detail, model);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxErrorBodyBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
            {
                total += read;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, total);
            try
            {
                var parsed = JsonSerializer.Deserialize<OllamaErrorResponse>(text, OllamaJson.Options);
                if (!string.IsNullOrWhiteSpace(parsed?.Error))
                {
                    return parsed.Error;
                }
            }
            catch (JsonException)
            {
                // Fall through to the raw text.
            }

            return string.IsNullOrWhiteSpace(text) ? $"HTTP {(int)response.StatusCode}" : text;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    internal static InferenceException MapError(HttpStatusCode status, string detail, string? model)
    {
        if (model is not null
            && (status == HttpStatusCode.NotFound || detail.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return new InferenceException(
                InferenceErrorCode.ModelNotFound,
                $"Model '{model}' is not installed in the local inference service.",
                detail);
        }

        if (LooksLikeLoadFailure(detail))
        {
            return new InferenceException(
                InferenceErrorCode.ModelLoadFailed,
                "The model could not be loaded. The host may not have enough memory for this model; try a smaller one.",
                detail);
        }

        return new InferenceException(
            InferenceErrorCode.ProviderError,
            "The local inference service reported an error while processing the request.",
            $"HTTP {(int)status}: {detail}");
    }

    private static bool LooksLikeLoadFailure(string detail) =>
        detail.Contains("memory", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("llama runner", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("failed to load", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("unable to load", StringComparison.OrdinalIgnoreCase);
}
