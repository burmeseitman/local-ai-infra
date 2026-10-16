using System.Text;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Core.Services;
using LocalAI.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Tests;

public class TextChunkerTests
{
    [Fact]
    public void Normalize_collapses_spaces_and_blank_lines_but_keeps_paragraphs()
    {
        Assert.Equal("a b\nc\n\nd", TextChunker.Normalize("  a \t  b\r\nc\n\n\n\n d \0"[..^2]));
    }

    [Fact]
    public void Chunks_respect_size_overlap_and_word_boundaries()
    {
        var words = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"word{i}"));
        var chunks = TextChunker.Chunk([new TextSegment(null, words)], size: 200, overlap: 40);

        Assert.True(chunks.Count > 10);
        Assert.All(chunks, c => Assert.InRange(c.Text.Length, 1, 200));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
        Assert.All(chunks, c => Assert.Matches(@"^word\d+( word\d+)*$", c.Text)); // never splits inside a word
        // Consecutive chunks overlap, so text near a boundary is retrievable from both sides.
        for (var i = 1; i < chunks.Count; i++)
        {
            var lastWord = chunks[i - 1].Text.Split(' ')[^1];
            Assert.Contains(lastWord, chunks[i].Text, StringComparison.Ordinal);
        }

        Assert.EndsWith("word399", chunks[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunks_prefer_paragraph_boundaries()
    {
        var text = new string('a', 120) + ".\n\n" + new string('b', 120) + ".";
        var chunks = TextChunker.Chunk([new TextSegment(null, text)], size: 200, overlap: 0);

        Assert.Equal(2, chunks.Count);
        Assert.EndsWith(".", chunks[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("bbb", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunks_record_the_page_where_they_start()
    {
        var segments = Enumerable.Range(1, 3).Select(p => new TextSegment(p, $"Page {p} " + new string('x', 150))).ToArray();

        var chunks = TextChunker.Chunk(segments, size: 160, overlap: 0);

        Assert.Equal([1, 2, 3], chunks.Select(c => c.Page!.Value));
        Assert.StartsWith("Page 2", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unbroken_text_still_makes_progress()
    {
        var chunks = TextChunker.Chunk([new TextSegment(null, new string('z', 1_000))], size: 300, overlap: 50);

        // One giant "word": no overlap is possible without cutting it, but everything is covered once.
        Assert.All(chunks, c => Assert.InRange(c.Text.Length, 1, 300));
        Assert.Equal(1_000, chunks.Sum(c => c.Text.Length));
    }

    [Fact]
    public void Empty_segments_produce_no_chunks() =>
        Assert.Empty(TextChunker.Chunk([new TextSegment(1, "   \n\n "), new TextSegment(2, "")], 100, 10));
}

public class DocumentServiceTests
{
    private readonly FakeChatProvider _provider = new();
    private readonly InMemoryDocumentStore _store = new();
    private readonly FakeEmbeddingProvider _embeddings = new();
    private readonly DocumentIngestionChannel _queue = new();
    private readonly RagOptions _options = new() { MaxFileBytes = 1_000, MaxDocumentCharacters = 800, MaxDocumentsPerUser = 2, ChunkSize = 200, ChunkOverlap = 20, EmbeddingBatchSize = 3 };
    private readonly DocumentService _service;
    private static readonly Guid Owner = Guid.NewGuid();

    public DocumentServiceTests()
    {
        _provider.Models.Add(FakeChatProvider.Model("nomic-embed-text:latest") with { IsEmbedding = true });
        var catalog = new ModelCatalog(_provider, Options.Create(new ChatOptions()), TimeProvider.System);
        _service = new DocumentService(_store, [new FakeTextExtractor(".txt", ".pdf")], catalog, _queue, Options.Create(_options), NullLogger<DocumentService>.Instance);
    }

    private Task<DocumentInfo> Upload(string name, string text, long? declared = null) =>
        _service.UploadAsync(Owner, name, "text/plain", declared ?? text.Length, new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);

    [Fact]
    public async Task Upload_stores_the_text_and_queues_indexing()
    {
        var document = await Upload("../../notes/Plan.txt", "Hello world");

        Assert.Equal(("Plan", "Plan.txt", DocumentStatus.Processing, 11), (document.Title, document.FileName, document.Status, document.CharacterCount));
        Assert.Equal([document.Id], await _store.ListProcessingIdsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("report.docx", DocumentErrorCode.UnsupportedType)]
    [InlineData("no-extension", DocumentErrorCode.UnsupportedType)]
    public async Task Unsupported_types_are_rejected(string name, DocumentErrorCode expected)
    {
        var ex = await Assert.ThrowsAsync<DocumentException>(() => Upload(name, "x"));
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task Size_limits_apply_to_declared_and_actual_length()
    {
        Assert.Equal(DocumentErrorCode.TooLarge, (await Assert.ThrowsAsync<DocumentException>(() => Upload("a.txt", "x", declared: 5_000))).Code);
        // A lying Content-Length does not bypass the limit.
        Assert.Equal(DocumentErrorCode.TooLarge, (await Assert.ThrowsAsync<DocumentException>(() => Upload("a.txt", new string('x', 1_500), declared: 10))).Code);
        Assert.Equal(DocumentErrorCode.TooLarge, (await Assert.ThrowsAsync<DocumentException>(() => Upload("a.txt", new string('x', 900)))).Code); // > MaxDocumentCharacters
    }

    [Fact]
    public async Task Files_without_text_are_rejected()
    {
        var ex = await Assert.ThrowsAsync<DocumentException>(() => Upload("scan.pdf", "  \n "));
        Assert.Equal(DocumentErrorCode.NoText, ex.Code);
    }

    [Fact]
    public async Task Upload_fails_fast_when_the_embedding_model_is_missing()
    {
        _provider.Models.RemoveAll(m => m.IsEmbedding);

        var ex = await Assert.ThrowsAsync<DocumentException>(() => Upload("a.txt", "text"));

        Assert.Equal(DocumentErrorCode.EmbeddingModelMissing, ex.Code);
        Assert.Contains("ollama pull nomic-embed-text", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await _store.ListAsync(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task Per_user_document_limit_is_enforced()
    {
        await Upload("1.txt", "a");
        await Upload("2.txt", "b");

        Assert.Equal(DocumentErrorCode.LimitReached, (await Assert.ThrowsAsync<DocumentException>(() => Upload("3.txt", "c"))).Code);
    }

    [Fact]
    public async Task Worker_chunks_embeds_in_batches_and_marks_ready()
    {
        var text = string.Join(". ", Enumerable.Range(0, 30).Select(i => $"Sentence number {i} about apples"));
        _options.MaxDocumentCharacters = 10_000;
        _options.MaxFileBytes = 10_000;
        var document = await Upload("fruit.txt", text);

        await CreateWorker().ProcessAsync(document.Id, CancellationToken.None);

        var stored = (await _store.GetAsync(Owner, document.Id, CancellationToken.None))!;
        Assert.Equal(DocumentStatus.Ready, stored.Status);
        var chunks = _store.ChunksOf(document.Id);
        Assert.Equal(stored.ChunkCount, chunks.Count);
        Assert.True(chunks.Count > 3);
        Assert.Equal((int)Math.Ceiling(chunks.Count / 3.0), _embeddings.Calls); // batch size 3
        Assert.All(_embeddings.Inputs, input => Assert.StartsWith("search_document: ", input, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_marks_failure_with_a_safe_message()
    {
        var document = await Upload("a.txt", "some text to index");
        _embeddings.Failure = InferenceException.Unavailable(new HttpRequestException("secret detail"));

        await CreateWorker().ProcessAsync(document.Id, CancellationToken.None);

        var stored = (await _store.GetAsync(Owner, document.Id, CancellationToken.None))!;
        Assert.Equal(DocumentStatus.Failed, stored.Status);
        Assert.DoesNotContain("secret", stored.Error, StringComparison.Ordinal);
    }

    private DocumentIngestionWorker CreateWorker()
    {
        var services = new ServiceCollection()
            .AddSingleton<LocalAI.Core.Interfaces.IDocumentStore>(_store)
            .AddSingleton<LocalAI.Core.Interfaces.IEmbeddingProvider>(_embeddings)
            .BuildServiceProvider();
        return new DocumentIngestionWorker(_queue, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options), NullLogger<DocumentIngestionWorker>.Instance);
    }
}

public class DocumentChatTests
{
    private readonly FakeChatProvider _provider = new();
    private readonly InMemoryConversationStore _conversations = new();
    private readonly InMemoryDocumentStore _documents = new();
    private readonly FakeEmbeddingProvider _embeddings = new();
    private readonly ConversationService _service;
    private static readonly Guid Owner = Guid.NewGuid();

    public DocumentChatTests()
    {
        _provider.Models.Add(FakeChatProvider.Model("nomic-embed-text:latest") with { IsEmbedding = true });
        var chatOptions = Options.Create(new ChatOptions());
        var ragOptions = Options.Create(new RagOptions { MinScore = 0.3, TopK = 2 });
        var catalog = new ModelCatalog(_provider, chatOptions, TimeProvider.System);
        var chat = new ChatService(_provider, catalog, new InferenceMetrics(TimeProvider.System), NullLogger<ChatService>.Instance);
        var documentService = new DocumentService(_documents, [], catalog, new DocumentIngestionChannel(), ragOptions, NullLogger<DocumentService>.Instance);
        var retriever = new DocumentRetriever(_documents, _embeddings, documentService, ragOptions, NullLogger<DocumentRetriever>.Instance);
        _service = new ConversationService(_conversations, chat, new ConversationLocks(), chatOptions, NullLogger<ConversationService>.Instance, retriever);
    }

    private async Task AddReadyDocument(Guid owner, string title, params string[] chunks)
    {
        var doc = await _documents.CreateAsync(owner, title, title + ".txt", "text/plain", 1, [new TextSegment(1, string.Join(' ', chunks))], CancellationToken.None);
        await _documents.SaveChunksAsync(
            doc.Id,
            chunks.Select((c, i) => new EmbeddedChunk(new TextChunk(i, i + 1, c), FakeEmbeddingProvider.Embed(c))).ToArray(),
            CancellationToken.None);
    }

    private static ChatCompletionRequest Turn(string content) =>
        new("tiny:1b", [new ChatMessage(ChatRole.User, content)], new GenerationOptions(null, 64));

    [Fact]
    public async Task Relevant_passages_are_sent_as_a_system_message_and_saved_as_sources()
    {
        await AddReadyDocument(Owner, "Handbook", "Vacation policy: employees receive 25 vacation days per year.", "Parking is available in garage B.");
        var conversation = await _service.CreateAsync(Owner, null, CancellationToken.None);

        var turn = await _service.BeginTurnAsync(Owner, conversation.Id, Turn("How many vacation days do employees get?"), CancellationToken.None, useDocuments: true);
        await foreach (var _ in _service.StreamTurnAsync(turn, CancellationToken.None))
        {
        }

        var sent = _provider.LastRequest!.Messages;
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Contains("[1] Handbook, page 1", sent[0].Content, StringComparison.Ordinal);
        Assert.Contains("25 vacation days", sent[0].Content, StringComparison.Ordinal);
        Assert.Contains("not instructions", sent[0].Content, StringComparison.Ordinal);
        Assert.Equal((ChatRole.User, "How many vacation days do employees get?"), (sent[^1].Role, sent[^1].Content));

        var source = Assert.Single(turn.Sources!);
        Assert.Equal((1, "Handbook", 1), (source.Number, source.Title, source.Page));

        var stored = (await _service.GetAsync(Owner, conversation.Id, CancellationToken.None)).Messages;
        Assert.Equal(2, stored.Count); // the system context is never stored as a message
        Assert.Equal("Handbook", Assert.Single(stored[1].Sources!).Title);
        Assert.Contains("search_query: How many vacation days", _embeddings.Inputs[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_users_documents_are_never_retrieved()
    {
        await AddReadyDocument(Guid.NewGuid(), "Someone else's secrets", "The vacation policy grants 99 days.");
        var conversation = await _service.CreateAsync(Owner, null, CancellationToken.None);

        var turn = await _service.BeginTurnAsync(Owner, conversation.Id, Turn("vacation policy days"), CancellationToken.None, useDocuments: true);

        Assert.Empty(turn.Sources!);
        Assert.Contains("No passage in the user's documents matched", turn.Request.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_use_documents_nothing_is_retrieved()
    {
        await AddReadyDocument(Owner, "Handbook", "Vacation policy text.");
        var conversation = await _service.CreateAsync(Owner, null, CancellationToken.None);

        var turn = await _service.BeginTurnAsync(Owner, conversation.Id, Turn("vacation policy"), CancellationToken.None);

        Assert.Null(turn.Sources);
        Assert.All(turn.Request.Messages, m => Assert.NotEqual(ChatRole.System, m.Role));
        Assert.Equal(0, _embeddings.Calls);
    }

    [Fact]
    public void Rag_options_validator_rejects_overlap_not_smaller_than_chunk() =>
        Assert.True(new RagOptionsValidator().Validate(null, new RagOptions { ChunkSize = 300, ChunkOverlap = 300 }).Failed);
}
