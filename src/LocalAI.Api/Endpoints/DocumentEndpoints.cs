using LocalAI.Api.Auth;
using LocalAI.Api.Middleware;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Core.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace LocalAI.Api.Endpoints;

public sealed record DocumentDto(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    int CharacterCount,
    int ChunkCount,
    DocumentStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt)
{
    public static DocumentDto From(DocumentInfo d) => new(
        d.Id, d.Title, d.FileName, d.ContentType, d.SizeBytes, d.CharacterCount, d.ChunkCount, d.Status, d.Error, d.CreatedAt, d.ProcessedAt);
}

/// <summary>Document library state for the UI, including whether indexing is possible at all.</summary>
public sealed record DocumentListResponse(
    IReadOnlyList<DocumentDto> Documents,
    string EmbeddingModel,
    bool EmbeddingModelInstalled,
    long MaxFileBytes,
    IReadOnlyList<string> SupportedExtensions);

/// <summary>A user's document library for retrieval-augmented chat.</summary>
internal static class DocumentEndpoints
{
    // Multipart framing overhead on top of the file itself.
    private const long MultipartOverheadBytes = 64 * 1_024;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var maxFile = app.ServiceProvider.GetRequiredService<IOptions<RagOptions>>().Value.MaxFileBytes;
        var documents = app.MapGroup("/api/documents").WithTags("Documents");

        documents.MapGet("/", ListAsync).WithName("ListDocuments");
        documents.MapPost("/", UploadAsync)
            .WithName("UploadDocument")
            .WithSummary("Uploads a PDF, TXT or Markdown file (multipart field 'file'); indexing continues in the background.")
            .WithMetadata(new RequestBodyLimit(maxFile + MultipartOverheadBytes))
            // CSRF is handled by SameSite=Strict cookies plus the required X-LocalAI-Request header (CsrfHeaderGuard).
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        documents.MapGet("/{id:guid}", GetAsync).WithName("GetDocument").ProducesProblem(StatusCodes.Status404NotFound);
        documents.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteDocument").ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<DocumentListResponse> ListAsync(
        HttpContext context, DocumentService service, IOptions<RagOptions> options, CancellationToken cancellationToken)
    {
        var items = await service.ListAsync(context.User.GetUserId(), cancellationToken);
        bool installed;
        try
        {
            await service.EnsureEmbeddingModelAsync(cancellationToken);
            installed = true;
        }
        catch (Exception ex) when (ex is DocumentException or InferenceException)
        {
            installed = false; // provider down or model missing: the list itself still works
        }

        return new DocumentListResponse(
            items.Select(DocumentDto.From).ToArray(),
            options.Value.EmbeddingModel,
            installed,
            options.Value.MaxFileBytes,
            DocumentService.SupportedExtensions);
    }

    private static async Task<Results<Accepted<DocumentDto>, ValidationProblem>> UploadAsync(
        HttpContext context, DocumentService service, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return ApiValidation.Problem(new Dictionary<string, string[]> { ["file"] = ["Send the file as multipart/form-data in a field named 'file'."] });
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (form.Files.GetFile("file") is not { Length: > 0 } file)
        {
            return ApiValidation.Problem(new Dictionary<string, string[]> { ["file"] = ["A non-empty file is required."] });
        }

        await using var stream = file.OpenReadStream();
        var document = await service.UploadAsync(
            context.User.GetUserId(), file.FileName, file.ContentType, file.Length, stream, cancellationToken);
        return TypedResults.Accepted($"/api/documents/{document.Id}", DocumentDto.From(document));
    }

    private static async Task<DocumentDto> GetAsync(Guid id, HttpContext context, DocumentService service, CancellationToken cancellationToken) =>
        DocumentDto.From(await service.GetAsync(context.User.GetUserId(), id, cancellationToken));

    private static async Task<NoContent> DeleteAsync(Guid id, HttpContext context, DocumentService service, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(context.User.GetUserId(), id, cancellationToken);
        return TypedResults.NoContent();
    }
}
