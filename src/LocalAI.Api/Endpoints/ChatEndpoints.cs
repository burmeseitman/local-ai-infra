using LocalAI.Api.Contracts;
using LocalAI.Api.Streaming;
using LocalAI.Core.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LocalAI.Api.Endpoints;

internal static class ChatEndpoints
{
    public const string InferencePolicy = "inference";

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat")
            .WithTags("Chat")
            .RequireRateLimiting(InferencePolicy);

        group.MapPost("/stream", StreamAsync)
            .WithName("StreamChat")
            .WithSummary("Streams an assistant reply as Server-Sent Events (start, delta*, done | error).")
            .Produces(StatusCodes.Status200OK, contentType: ChatStreamResult.ContentType)
            .WithPreflightErrors();

        group.MapPost("/", CompleteAsync)
            .WithName("CompleteChat")
            .WithSummary("Returns the full assistant reply once generation finishes (non-streaming).")
            .Produces<ChatResponse>()
            .WithPreflightErrors()
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return app;
    }

    /// <summary>Errors that can occur before any response body is written.</summary>
    private static RouteHandlerBuilder WithPreflightErrors(this RouteHandlerBuilder builder) => builder
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> StreamAsync(
        ChatRequest body, ChatRequestValidator validator, ChatService chat, CancellationToken cancellationToken)
    {
        var validation = validator.Validate(body.ToUnvalidated());
        if (!validation.IsValid)
        {
            return ValidationProblem(validation);
        }

        // Model resolution happens before the stream starts so failures get a proper status code.
        var prepared = await chat.PrepareAsync(validation.Request!, cancellationToken);
        return new ChatStreamResult(prepared.Model, ct => chat.StreamAsync(prepared, ct));
    }

    private static async Task<IResult> CompleteAsync(
        ChatRequest body, ChatRequestValidator validator, ChatService chat, CancellationToken cancellationToken)
    {
        var validation = validator.Validate(body.ToUnvalidated());
        if (!validation.IsValid)
        {
            return ValidationProblem(validation);
        }

        var prepared = await chat.PrepareAsync(validation.Request!, cancellationToken);
        var result = await chat.CompleteAsync(prepared, cancellationToken);
        return TypedResults.Ok(new ChatResponse(
            result.Model,
            new ChatMessageDto("assistant", result.Content),
            result.FinishReason,
            CompletionStatsDto.From(result.Stats)));
    }

    private static ValidationProblem ValidationProblem(ChatValidationResult validation) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(validation.Errors),
            detail: "The chat request is invalid.",
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed", ["retryable"] = false });
}
