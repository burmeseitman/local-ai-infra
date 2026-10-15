using LocalAI.Api.Contracts;
using LocalAI.Api.Streaming;
using LocalAI.Core.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LocalAI.Api.Endpoints;

/// <summary>
/// Server-side conversation history. Clients send only the new message; the API loads
/// history, persists both sides of the turn and streams the reply as SSE.
/// </summary>
internal static class ConversationEndpoints
{
    private const int DefaultListLimit = 50;

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations").WithTags("Conversations");

        group.MapGet("/", ListAsync)
            .WithName("ListConversations")
            .WithSummary("Conversations, most recently updated first.");
        group.MapPost("/", CreateAsync)
            .WithName("CreateConversation")
            .ProducesValidationProblem();
        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetConversation")
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPatch("/{id:guid}", RenameAsync)
            .WithName("RenameConversation")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteConversation")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/messages/stream", SendAsync)
            .WithName("SendConversationMessage")
            .WithSummary("Stores the user message, streams the reply as SSE and stores it (complete, stopped or failed).")
            .RequireRateLimiting(ChatEndpoints.InferencePolicy)
            .Produces(StatusCodes.Status200OK, contentType: ChatStreamResult.ContentType)
            .WithStreamPreflightErrors();
        group.MapPost("/{id:guid}/regenerate/stream", RegenerateAsync)
            .WithName("RegenerateConversationReply")
            .WithSummary("Replaces the last assistant reply with a new streamed generation.")
            .RequireRateLimiting(ChatEndpoints.InferencePolicy)
            .Produces(StatusCodes.Status200OK, contentType: ChatStreamResult.ContentType)
            .WithStreamPreflightErrors();

        return app;
    }

    private static RouteHandlerBuilder WithStreamPreflightErrors(this RouteHandlerBuilder builder) => builder
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<ConversationListResponse> ListAsync(
        ConversationService conversations, CancellationToken cancellationToken, int? limit = null)
    {
        var items = await conversations.ListAsync(limit ?? DefaultListLimit, cancellationToken);
        return new ConversationListResponse(items.Select(ConversationSummaryDto.From).ToArray());
    }

    private static async Task<Created<ConversationDto>> CreateAsync(
        CreateConversationRequest? body, ConversationService conversations, CancellationToken cancellationToken)
    {
        var conversation = await conversations.CreateAsync(body?.Title, cancellationToken);
        return TypedResults.Created($"/api/conversations/{conversation.Id}", ConversationDto.From(conversation));
    }

    private static async Task<ConversationDto> GetAsync(
        Guid id, ConversationService conversations, CancellationToken cancellationToken) =>
        ConversationDto.From(await conversations.GetAsync(id, cancellationToken));

    private static async Task<Results<NoContent, ValidationProblem>> RenameAsync(
        Guid id, RenameConversationRequest body, ConversationService conversations, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Trim().Length > ConversationService.MaxTitleLength)
        {
            return Validation("title", $"Title must be 1–{ConversationService.MaxTitleLength} characters.");
        }

        await conversations.RenameAsync(id, body.Title, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DeleteAsync(
        Guid id, ConversationService conversations, CancellationToken cancellationToken)
    {
        await conversations.DeleteAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> SendAsync(
        Guid id,
        SendMessageRequest body,
        ChatRequestValidator validator,
        ConversationService conversations,
        CancellationToken cancellationToken)
    {
        var validation = validator.ValidateTurn(body.Model, body.Content, body.Temperature, body.MaxTokens);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation);
        }

        var turn = await conversations.BeginTurnAsync(id, validation.Request!, cancellationToken);
        return new ChatStreamResult(turn.Request.Model, ct => conversations.StreamTurnAsync(turn, ct), id, turn);
    }

    private static async Task<IResult> RegenerateAsync(
        Guid id,
        RegenerateRequest body,
        ChatRequestValidator validator,
        ConversationService conversations,
        CancellationToken cancellationToken)
    {
        var validation = validator.ValidateGeneration(body.Model, body.Temperature, body.MaxTokens);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation);
        }

        var request = validation.Request!;
        var turn = await conversations.BeginRegenerateAsync(id, request.Model, request.Options, cancellationToken);
        return new ChatStreamResult(turn.Request.Model, ct => conversations.StreamTurnAsync(turn, ct), id, turn);
    }

    private static ValidationProblem ValidationFailed(ChatValidationResult validation) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(validation.Errors),
            detail: "The request is invalid.",
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed", ["retryable"] = false });

    private static ValidationProblem Validation(string key, string message) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]> { [key] = [message] },
            detail: "The request is invalid.",
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed", ["retryable"] = false });
}
