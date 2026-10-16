using LocalAI.Core.Models;

namespace LocalAI.Core.Interfaces;

/// <summary>Per-user document storage and vector search. Other users' documents behave as missing.</summary>
public interface IDocumentStore
{
    Task<DocumentInfo> CreateAsync(
        Guid ownerId, string title, string fileName, string contentType, long sizeBytes,
        IReadOnlyList<TextSegment> content, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentInfo>> ListAsync(Guid ownerId, CancellationToken cancellationToken);

    Task<DocumentInfo?> GetAsync(Guid ownerId, Guid id, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid ownerId, Guid id, CancellationToken cancellationToken);

    Task<int> CountAsync(Guid ownerId, CancellationToken cancellationToken);

    Task<bool> HasReadyDocumentsAsync(Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Ids of documents still in <see cref="DocumentStatus.Processing"/> (to resume after a restart).</summary>
    Task<IReadOnlyList<Guid>> ListProcessingIdsAsync(CancellationToken cancellationToken);

    /// <summary>The stored text of a document awaiting processing; null if it no longer exists.</summary>
    Task<IReadOnlyList<TextSegment>?> GetContentForProcessingAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Replaces the document's chunks and marks it <see cref="DocumentStatus.Ready"/>.</summary>
    Task SaveChunksAsync(Guid id, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken);

    /// <summary>Top <paramref name="topK"/> chunks of the owner's ready documents by cosine similarity.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid ownerId, float[] queryEmbedding, int topK, double minScore, CancellationToken cancellationToken);
}

/// <summary>Produces embedding vectors. Implemented by the inference provider.</summary>
public interface IEmbeddingProvider
{
    Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}

/// <summary>Extracts plain text from an uploaded file.</summary>
public interface ITextExtractor
{
    /// <summary>True when this extractor handles the given (lower-case) file extension, e.g. ".pdf".</summary>
    bool Supports(string extension);

    /// <exception cref="Exceptions.DocumentException">The file is unreadable or contains no text.</exception>
    Task<IReadOnlyList<TextSegment>> ExtractAsync(Stream content, CancellationToken cancellationToken);
}
