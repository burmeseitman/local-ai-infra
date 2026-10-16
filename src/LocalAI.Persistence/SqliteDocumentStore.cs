using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using LocalAI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LocalAI.Persistence;

/// <summary>
/// Documents and their embedded chunks in SQLite. Vector search is exact (brute-force cosine
/// similarity, SIMD-accelerated) over the owner's chunks: simple and correct for a personal
/// knowledge base of thousands of chunks; a dedicated vector index is a later-phase concern.
/// </summary>
internal sealed class SqliteDocumentStore(LocalAiDbContext db, TimeProvider timeProvider, ILogger<SqliteDocumentStore> logger) : IDocumentStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Ready = nameof(DocumentStatus.Ready);
    private const string Processing = nameof(DocumentStatus.Processing);

    public async Task<DocumentInfo> CreateAsync(
        Guid ownerId, string title, string fileName, string contentType, long sizeBytes,
        IReadOnlyList<TextSegment> content, CancellationToken cancellationToken)
    {
        var entity = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Title = Truncate(title, 200),
            FileName = Truncate(fileName, 260),
            ContentType = Truncate(contentType, 100),
            SizeBytes = sizeBytes,
            ContentJson = JsonSerializer.Serialize(content, Json),
            CharacterCount = content.Sum(s => s.Text.Length),
            Status = Processing,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Documents.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(entity);
    }

    public async Task<IReadOnlyList<DocumentInfo>> ListAsync(Guid ownerId, CancellationToken cancellationToken) =>
        (await Summaries().Where(d => d.OwnerId == ownerId).OrderByDescending(d => d.CreatedAt).ToListAsync(cancellationToken))
        .Select(ToInfo)
        .ToArray();

    public async Task<DocumentInfo?> GetAsync(Guid ownerId, Guid id, CancellationToken cancellationToken) =>
        await Summaries().FirstOrDefaultAsync(d => d.Id == id && d.OwnerId == ownerId, cancellationToken) is { } d ? ToInfo(d) : null;

    public async Task<bool> DeleteAsync(Guid ownerId, Guid id, CancellationToken cancellationToken) =>
        // Chunks are removed by ON DELETE CASCADE.
        await db.Documents.Where(d => d.Id == id && d.OwnerId == ownerId).ExecuteDeleteAsync(cancellationToken) > 0;

    public Task<int> CountAsync(Guid ownerId, CancellationToken cancellationToken) =>
        db.Documents.CountAsync(d => d.OwnerId == ownerId, cancellationToken);

    public Task<bool> HasReadyDocumentsAsync(Guid ownerId, CancellationToken cancellationToken) =>
        db.Documents.AnyAsync(d => d.OwnerId == ownerId && d.Status == Ready, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListProcessingIdsAsync(CancellationToken cancellationToken) =>
        await db.Documents.Where(d => d.Status == Processing).OrderBy(d => d.CreatedAt).Select(d => d.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TextSegment>?> GetContentForProcessingAsync(Guid id, CancellationToken cancellationToken)
    {
        var json = await db.Documents.Where(d => d.Id == id).Select(d => d.ContentJson).FirstOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<TextSegment[]>(json, Json);
    }

    public async Task SaveChunksAsync(Guid id, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Documents.AnyAsync(d => d.Id == id, cancellationToken))
        {
            return; // deleted while indexing
        }

        await db.Chunks.Where(c => c.DocumentId == id).ExecuteDeleteAsync(cancellationToken);
        db.Chunks.AddRange(chunks.Select(c => new ChunkEntity
        {
            Id = Guid.NewGuid(),
            DocumentId = id,
            Index = c.Chunk.Index,
            Page = c.Chunk.Page,
            Text = c.Chunk.Text,
            Embedding = MemoryMarshal.AsBytes(c.Embedding.AsSpan()).ToArray(),
        }));
        await db.SaveChangesAsync(cancellationToken);
        await db.Documents.Where(d => d.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(d => d.Status, Ready)
                .SetProperty(d => d.ChunkCount, chunks.Count)
                .SetProperty(d => d.Error, (string?)null)
                .SetProperty(d => d.ProcessedAt, timeProvider.GetUtcNow()),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken) =>
        db.Documents.Where(d => d.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(d => d.Status, nameof(DocumentStatus.Failed))
                .SetProperty(d => d.Error, Truncate(error, 500))
                .SetProperty(d => d.ProcessedAt, timeProvider.GetUtcNow()),
            cancellationToken);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid ownerId, float[] queryEmbedding, int topK, double minScore, CancellationToken cancellationToken)
    {
        // Pass 1: score every vector of the owner's ready documents (vectors only, no text).
        var vectors = await db.Chunks.AsNoTracking()
            .Where(c => c.Document!.OwnerId == ownerId && c.Document.Status == Ready)
            .Select(c => new { c.Id, c.Embedding })
            .ToListAsync(cancellationToken);

        var (scored, mismatched) = Score(vectors.Select(v => (v.Id, v.Embedding)), queryEmbedding, minScore);

        if (mismatched > 0)
        {
            logger.LogWarning(
                "{Count} chunk(s) have a different embedding size than the query; re-upload those documents after changing Rag:EmbeddingModel",
                mismatched);
        }

        var best = scored.OrderByDescending(s => s.Score).Take(topK).ToArray();
        if (best.Length == 0)
        {
            return [];
        }

        // Pass 2: fetch text and titles for the winners only.
        var ids = best.Select(b => b.Id).ToArray();
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.DocumentId, Title = c.Document!.Title, c.Index, c.Page, c.Text })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        return best
            .Where(b => rows.ContainsKey(b.Id))
            .Select(b =>
            {
                var r = rows[b.Id];
                return new SearchHit(r.DocumentId, r.Title, r.Index, r.Page, r.Text, b.Score);
            })
            .ToArray();
    }

    /// <summary>Cosine similarity of each stored vector to the query; skips vectors of another dimension.</summary>
    private static (List<(Guid Id, double Score)> Scored, int Mismatched) Score(
        IEnumerable<(Guid Id, byte[] Embedding)> vectors, float[] queryEmbedding, double minScore)
    {
        var query = queryEmbedding.AsSpan();
        var mismatched = 0;
        var scored = new List<(Guid Id, double Score)>();
        foreach (var (id, embedding) in vectors)
        {
            var vector = MemoryMarshal.Cast<byte, float>(embedding.AsSpan());
            if (vector.Length != query.Length)
            {
                mismatched++; // indexed with a different embedding model
                continue;
            }

            var score = TensorPrimitives.CosineSimilarity(query, vector);
            if (score >= minScore)
            {
                scored.Add((id, score));
            }
        }

        return (scored, mismatched);
    }

    /// <summary>Document rows without the (large) extracted text.</summary>
    private IQueryable<DocumentEntity> Summaries() =>
        db.Documents.AsNoTracking().Select(d => new DocumentEntity
        {
            Id = d.Id,
            OwnerId = d.OwnerId,
            Title = d.Title,
            FileName = d.FileName,
            ContentType = d.ContentType,
            SizeBytes = d.SizeBytes,
            CharacterCount = d.CharacterCount,
            ChunkCount = d.ChunkCount,
            Status = d.Status,
            Error = d.Error,
            CreatedAt = d.CreatedAt,
            ProcessedAt = d.ProcessedAt,
        });

    private static DocumentInfo ToInfo(DocumentEntity d) => new(
        d.Id, d.Title, d.FileName, d.ContentType, d.SizeBytes, d.CharacterCount, d.ChunkCount,
        Enum.Parse<DocumentStatus>(d.Status), d.Error, d.CreatedAt, d.ProcessedAt);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
