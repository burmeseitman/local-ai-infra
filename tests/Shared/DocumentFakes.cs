using System.Globalization;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;

namespace LocalAI.Tests.Shared;

/// <summary>
/// Deterministic bag-of-words embedder: texts sharing words get similar vectors, so retrieval
/// tests exercise real cosine ranking without a model.
/// </summary>
public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    public const int Dimensions = 64;

    public int Calls { get; private set; }

    public List<string> Inputs { get; } = [];

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        Calls++;
        Inputs.AddRange(inputs);
        if (Failure is not null)
        {
            return Task.FromException<IReadOnlyList<float[]>>(Failure);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(Embed).ToArray());
    }

    public static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var word in text.ToLower(CultureInfo.InvariantCulture)
                     .Split([' ', '\n', '.', ',', ':', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
                     .Where(w => w.Length > 2 && w is not "search_document" and not "search_query" and not "the" and not "and" and not "what"))
        {
            vector[(int)((uint)StableHash(word) % Dimensions)] += 1;
        }

        return vector;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s)
            {
                h = (h * 31) + c;
            }

            return h;
        }
    }
}

/// <summary>Simple text extractor for tests: every extension in <see cref="Extensions"/>, UTF-8.</summary>
public sealed class FakeTextExtractor(params string[] extensions) : ITextExtractor
{
    public string[] Extensions { get; } = extensions;

    public bool Supports(string extension) => Extensions.Contains(extension);

    public async Task<IReadOnlyList<TextSegment>> ExtractAsync(Stream content, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, leaveOpen: true);
        return [new TextSegment(1, await reader.ReadToEndAsync(cancellationToken))];
    }
}

/// <summary>In-memory <see cref="IDocumentStore"/> with brute-force cosine search.</summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (Guid Owner, DocumentInfo Info, IReadOnlyList<TextSegment> Content, List<EmbeddedChunk> Chunks)> _docs = new();

    public IReadOnlyList<EmbeddedChunk> ChunksOf(Guid id)
    {
        lock (_gate)
        {
            return _docs[id].Chunks;
        }
    }

    public Task<DocumentInfo> CreateAsync(Guid ownerId, string title, string fileName, string contentType, long sizeBytes, IReadOnlyList<TextSegment> content, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var info = new DocumentInfo(Guid.NewGuid(), title, fileName, contentType, sizeBytes, content.Sum(s => s.Text.Length), 0,
                DocumentStatus.Processing, null, DateTimeOffset.UnixEpoch, null);
            _docs[info.Id] = (ownerId, info, content, []);
            return Task.FromResult(info);
        }
    }

    public Task<IReadOnlyList<DocumentInfo>> ListAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DocumentInfo>>(_docs.Values.Where(d => d.Owner == ownerId).Select(d => d.Info).ToArray());
        }
    }

    public Task<DocumentInfo?> GetAsync(Guid ownerId, Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.TryGetValue(id, out var d) && d.Owner == ownerId ? d.Info : null);
        }
    }

    public Task<bool> DeleteAsync(Guid ownerId, Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.TryGetValue(id, out var d) && d.Owner == ownerId && _docs.Remove(id));
        }
    }

    public Task<int> CountAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.Values.Count(d => d.Owner == ownerId));
        }
    }

    public Task<bool> HasReadyDocumentsAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.Values.Any(d => d.Owner == ownerId && d.Info.Status == DocumentStatus.Ready));
        }
    }

    public Task<IReadOnlyList<Guid>> ListProcessingIdsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Guid>>(_docs.Values.Where(d => d.Info.Status == DocumentStatus.Processing).Select(d => d.Info.Id).ToArray());
        }
    }

    public Task<IReadOnlyList<TextSegment>?> GetContentForProcessingAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.TryGetValue(id, out var d) ? d.Content : null);
        }
    }

    public Task SaveChunksAsync(Guid id, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_docs.TryGetValue(id, out var d))
            {
                _docs[id] = d with { Info = d.Info with { Status = DocumentStatus.Ready, ChunkCount = chunks.Count }, Chunks = chunks.ToList() };
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_docs.TryGetValue(id, out var d))
            {
                _docs[id] = d with { Info = d.Info with { Status = DocumentStatus.Failed, Error = error } };
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(Guid ownerId, float[] queryEmbedding, int topK, double minScore, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<SearchHit> hits = _docs.Values
                .Where(d => d.Owner == ownerId && d.Info.Status == DocumentStatus.Ready)
                .SelectMany(d => d.Chunks.Select(c => new SearchHit(d.Info.Id, d.Info.Title, c.Chunk.Index, c.Chunk.Page, c.Chunk.Text, Cosine(queryEmbedding, c.Embedding))))
                .Where(h => h.Score >= minScore)
                .OrderByDescending(h => h.Score)
                .Take(topK)
                .ToArray();
            return Task.FromResult(hits);
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}
