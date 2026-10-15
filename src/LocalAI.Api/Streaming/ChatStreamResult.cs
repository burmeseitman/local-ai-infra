using System.Text.Json;
using LocalAI.Api.Contracts;
using LocalAI.Api.Middleware;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace LocalAI.Api.Streaming;

/// <summary>
/// Streams a generation as Server-Sent Events: one <c>start</c>, zero or more <c>delta</c>,
/// then exactly one terminal <c>done</c> or <c>error</c> event (unless the client disconnects).
/// Each event is flushed immediately; nothing is buffered server-side.
/// </summary>
internal sealed class ChatStreamResult(
    string model,
    Func<CancellationToken, IAsyncEnumerable<ChatStreamChunk>> source,
    Guid? conversationId = null,
    IDisposable? owned = null) : IResult
{
    public const string ContentType = "text/event-stream";

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        try
        {
            await StreamAsync(httpContext);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private async Task StreamAsync(HttpContext httpContext)
    {
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<ChatStreamResult>>();
        var json = httpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var cancellationToken = httpContext.RequestAborted;

        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = $"{ContentType}; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering (nginx)
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var writer = new SseWriter(response, json);
        try
        {
            await writer.WriteAsync(
                "start",
                new StreamStartEvent(httpContext.TraceIdentifier, model, DateTimeOffset.UtcNow, conversationId),
                cancellationToken);

            await foreach (var chunk in source(cancellationToken))
            {
                if (chunk.Content.Length > 0)
                {
                    await writer.WriteAsync("delta", new StreamDeltaEvent(chunk.Content), cancellationToken);
                }

                if (chunk.IsFinal)
                {
                    await writer.WriteAsync(
                        "done",
                        new StreamDoneEvent(chunk.FinishReason, CompletionStatsDto.From(chunk.Stats)),
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client stopped the generation or disconnected. Not an error; nothing left to send.
        }
        catch (InferenceException ex)
        {
            var error = ErrorCatalog.For(ex);
            await TryWriteErrorAsync(writer, new StreamErrorEvent(error.Code, ex.Message, error.Retryable), logger, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected failure while streaming a chat response");
            await TryWriteErrorAsync(
                writer,
                new StreamErrorEvent(ErrorCatalog.Internal.Code, ErrorCatalog.InternalMessage, false),
                logger,
                cancellationToken);
        }
    }

    private static async Task TryWriteErrorAsync(
        SseWriter writer, StreamErrorEvent error, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync("error", error, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            logger.LogDebug("Could not deliver stream error event; client is gone");
        }
    }

    private sealed class SseWriter(HttpResponse response, JsonSerializerOptions json)
    {
        public async Task WriteAsync<T>(string eventName, T payload, CancellationToken cancellationToken)
        {
            // Compact JSON never contains raw newlines, so a single "data:" line is sufficient.
            var data = JsonSerializer.Serialize(payload, json);
            await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }
    }
}
