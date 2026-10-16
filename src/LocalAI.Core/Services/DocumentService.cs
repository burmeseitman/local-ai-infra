using System.Threading.Channels;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Services;

/// <summary>Hands document ids from the upload request to the background indexer.</summary>
public sealed class DocumentIngestionChannel
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid documentId) => _channel.Writer.TryWrite(documentId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Upload, listing and deletion of a user's documents. Uploads are validated and their text
/// extracted synchronously; chunking and embedding run in the background
/// (<see cref="DocumentIngestionWorker"/>) so large files never block the request.
/// </summary>
public sealed class DocumentService(
    IDocumentStore store,
    IEnumerable<ITextExtractor> extractors,
    ModelCatalog catalog,
    DocumentIngestionChannel queue,
    IOptions<RagOptions> options,
    ILogger<DocumentService> logger)
{
    public static readonly IReadOnlyList<string> SupportedExtensions = [".pdf", ".txt", ".md", ".markdown"];

    public Task<IReadOnlyList<DocumentInfo>> ListAsync(Guid ownerId, CancellationToken cancellationToken) =>
        store.ListAsync(ownerId, cancellationToken);

    public async Task<DocumentInfo> GetAsync(Guid ownerId, Guid id, CancellationToken cancellationToken) =>
        await store.GetAsync(ownerId, id, cancellationToken) ?? throw DocumentException.NotFound(id);

    public async Task DeleteAsync(Guid ownerId, Guid id, CancellationToken cancellationToken)
    {
        if (!await store.DeleteAsync(ownerId, id, cancellationToken))
        {
            throw DocumentException.NotFound(id);
        }
    }

    /// <summary>Validates and stores an upload, then queues it for indexing.</summary>
    public async Task<DocumentInfo> UploadAsync(
        Guid ownerId, string fileName, string? contentType, long declaredLength, Stream content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var safeName = Path.GetFileName(fileName).Trim();
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        var extractor = extractors.FirstOrDefault(e => e.Supports(extension))
            ?? throw new DocumentException(
                DocumentErrorCode.UnsupportedType,
                $"Unsupported file type '{extension}'. Supported: {string.Join(", ", SupportedExtensions)}.");

        if (declaredLength > settings.MaxFileBytes)
        {
            throw TooLarge(settings.MaxFileBytes);
        }

        if (await store.CountAsync(ownerId, cancellationToken) >= settings.MaxDocumentsPerUser)
        {
            throw new DocumentException(DocumentErrorCode.LimitReached, $"You can store at most {settings.MaxDocumentsPerUser} documents. Delete some first.");
        }

        // Fail fast (before any work) if documents could never be indexed.
        await EnsureEmbeddingModelAsync(cancellationToken);

        // Copy into memory with a hard cap: the declared length cannot be trusted.
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(content, buffer, settings.MaxFileBytes, cancellationToken);
        var sizeBytes = buffer.Length; // read before extraction: extractors may dispose the stream
        buffer.Position = 0;

        var segments = await extractor.ExtractAsync(buffer, cancellationToken);
        var characters = segments.Sum(s => s.Text.Length);
        if (segments.All(s => string.IsNullOrWhiteSpace(s.Text)))
        {
            throw new DocumentException(
                DocumentErrorCode.NoText,
                "No text could be extracted. Scanned PDFs (images of text) are not supported.");
        }

        if (characters > settings.MaxDocumentCharacters)
        {
            throw new DocumentException(
                DocumentErrorCode.TooLarge,
                $"The document contains {characters:N0} characters; the limit is {settings.MaxDocumentCharacters:N0}.");
        }

        var title = Path.GetFileNameWithoutExtension(safeName);
        var document = await store.CreateAsync(
            ownerId, title.Length > 0 ? title : safeName, safeName, contentType ?? "application/octet-stream",
            sizeBytes, segments, cancellationToken);
        queue.Enqueue(document.Id);
        logger.LogInformation(
            "Document {DocumentId} uploaded ({Bytes} bytes, {Characters} characters); queued for indexing",
            document.Id, sizeBytes, characters);
        return document;
    }

    public async Task EnsureEmbeddingModelAsync(CancellationToken cancellationToken)
    {
        var model = options.Value.EmbeddingModel;
        try
        {
            await catalog.ResolveAsync(model, cancellationToken, allowEmbedding: true);
        }
        catch (InferenceException ex) when (ex.Code == InferenceErrorCode.ModelNotFound)
        {
            throw DocumentException.EmbeddingModelMissing(model);
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw TooLarge(limit);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static DocumentException TooLarge(long limit) =>
        new(DocumentErrorCode.TooLarge, $"Files may be at most {limit / (1_024 * 1_024)} MB.");
}

/// <summary>
/// Background indexer: chunks queued documents, embeds the chunks in batches through the
/// provider, and stores them. Documents left in "processing" by a restart are resumed.
/// </summary>
public sealed class DocumentIngestionWorker(
    DocumentIngestionChannel queue,
    IServiceScopeFactory scopes,
    IOptions<RagOptions> options,
    ILogger<DocumentIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
                foreach (var id in await store.ListProcessingIdsAsync(stoppingToken))
                {
                    queue.Enqueue(id);
                }
            }

            await foreach (var id in queue.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; unfinished documents stay "processing" and resume on next start.
        }
    }

    internal async Task ProcessAsync(Guid id, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var settings = options.Value;
        try
        {
            var content = await store.GetContentForProcessingAsync(id, stoppingToken);
            if (content is null)
            {
                return; // deleted while queued
            }

            var started = DateTime.UtcNow;
            var chunks = TextChunker.Chunk(content, settings.ChunkSize, settings.ChunkOverlap);
            var embedded = new List<EmbeddedChunk>(chunks.Count);
            foreach (var batch in chunks.Chunk(settings.EmbeddingBatchSize))
            {
                var vectors = await embeddings.EmbedAsync(
                    settings.EmbeddingModel, batch.Select(c => settings.DocumentPrefix + c.Text).ToArray(), stoppingToken);
                if (vectors.Count != batch.Length)
                {
                    throw InferenceException.InvalidResponse($"Expected {batch.Length} embeddings, received {vectors.Count}.");
                }

                embedded.AddRange(batch.Zip(vectors, (chunk, vector) => new EmbeddedChunk(chunk, vector)));
            }

            await store.SaveChunksAsync(id, embedded, stoppingToken);
            logger.LogInformation(
                "Document {DocumentId} indexed: {Chunks} chunks in {Seconds:0.0}s",
                id, embedded.Count, (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                InferenceException { Code: InferenceErrorCode.ModelNotFound } => DocumentException.EmbeddingModelMissing(settings.EmbeddingModel).Message,
                InferenceException inference => inference.Message,
                _ => "Indexing failed unexpectedly. See the API logs.",
            };
            logger.LogError(ex, "Indexing document {DocumentId} failed", id);
            await store.MarkFailedAsync(id, message, CancellationToken.None);
        }
    }
}
