namespace LocalAI.Core.Models;

public enum DocumentStatus
{
    Processing,
    Ready,
    Failed,
}

public sealed record DocumentInfo(
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
    DateTimeOffset? ProcessedAt);

/// <summary>A run of extracted text. <see cref="Page"/> is 1-based for paged formats (PDF), else null.</summary>
public sealed record TextSegment(int? Page, string Text);

/// <summary>A chunk of a document, sized for embedding and for quoting in a prompt.</summary>
public sealed record TextChunk(int Index, int? Page, string Text);

public sealed record EmbeddedChunk(TextChunk Chunk, float[] Embedding);

/// <summary>A retrieved passage with its cosine similarity to the query (−1..1).</summary>
public sealed record SearchHit(Guid DocumentId, string DocumentTitle, int ChunkIndex, int? Page, string Text, double Score);

/// <summary>
/// A passage that was given to the model for an answer. <see cref="Number"/> is what the
/// model cites as [n]. Stored with the assistant message.
/// </summary>
public sealed record MessageSource(int Number, Guid DocumentId, string Title, int? Page, string Snippet, double Score);
