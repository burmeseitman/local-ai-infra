using LocalAI.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Persistence.Tests;

public sealed class SqliteDocumentStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly LocalAiDbContext _db;
    private readonly SqliteDocumentStore _store;
    private readonly Guid _owner;
    private readonly Guid _other;

    public SqliteDocumentStoreTests()
    {
        _connection.Open();
        _db = new LocalAiDbContext(new DbContextOptionsBuilder<LocalAiDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _store = new SqliteDocumentStore(_db, TimeProvider.System, NullLogger<SqliteDocumentStore>.Instance);
        var users = new SqliteUserStore(_db, TimeProvider.System);
        _owner = users.CreateAsync("owner", "h", UserRole.User, CancellationToken.None).GetAwaiter().GetResult().Id;
        _other = users.CreateAsync("other", "h", UserRole.User, CancellationToken.None).GetAwaiter().GetResult().Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static readonly TextSegment[] Content = [new(1, "page one text"), new(2, "page two text")];

    private Task<DocumentInfo> Create(Guid owner, string title = "Doc") =>
        _store.CreateAsync(owner, title, title + ".pdf", "application/pdf", 1234, Content, CancellationToken.None);

    private static EmbeddedChunk Chunk(int index, string text, params float[] vector) => new(new TextChunk(index, index + 1, text), vector);

    [Fact]
    public async Task Documents_are_owner_scoped_and_keep_their_text_for_processing()
    {
        var doc = await Create(_owner);

        Assert.Equal((DocumentStatus.Processing, 26, 1234L), (doc.Status, doc.CharacterCount, doc.SizeBytes));
        Assert.Single(await _store.ListAsync(_owner, CancellationToken.None));
        Assert.Empty(await _store.ListAsync(_other, CancellationToken.None));
        Assert.Null(await _store.GetAsync(_other, doc.Id, CancellationToken.None));
        Assert.False(await _store.DeleteAsync(_other, doc.Id, CancellationToken.None));
        Assert.Equal([doc.Id], await _store.ListProcessingIdsAsync(CancellationToken.None));
        Assert.Equal(Content, await _store.GetContentForProcessingAsync(doc.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Saving_chunks_marks_ready_and_search_ranks_by_cosine_similarity()
    {
        var doc = await Create(_owner, "Guide");
        await _store.SaveChunksAsync(doc.Id, [Chunk(0, "about cats", 1, 0, 0), Chunk(1, "about dogs", 0, 1, 0), Chunk(2, "cats and dogs", 1, 1, 0)], CancellationToken.None);

        var ready = (await _store.GetAsync(_owner, doc.Id, CancellationToken.None))!;
        Assert.Equal((DocumentStatus.Ready, 3), (ready.Status, ready.ChunkCount));
        Assert.True(await _store.HasReadyDocumentsAsync(_owner, CancellationToken.None));
        Assert.Empty(await _store.ListProcessingIdsAsync(CancellationToken.None));

        var hits = await _store.SearchAsync(_owner, [1, 0, 0], topK: 2, minScore: 0.5, CancellationToken.None);

        Assert.Equal(["about cats", "cats and dogs"], hits.Select(h => h.Text));
        Assert.Equal(1.0, hits[0].Score, 3);
        Assert.Equal(("Guide", 1), (hits[0].DocumentTitle, hits[0].Page!.Value));
        Assert.True(hits[1].Score is > 0.70 and < 0.71); // cos 45°
    }

    [Fact]
    public async Task Search_ignores_other_owners_unready_documents_and_mismatched_dimensions()
    {
        var mine = await Create(_owner);
        await _store.SaveChunksAsync(mine.Id, [Chunk(0, "mine", 1, 0, 0), Chunk(1, "old model", 1, 0)], CancellationToken.None);
        var theirs = await Create(_other);
        await _store.SaveChunksAsync(theirs.Id, [Chunk(0, "theirs", 1, 0, 0)], CancellationToken.None);
        await Create(_owner, "still processing");

        var hits = await _store.SearchAsync(_owner, [1, 0, 0], 10, 0.0, CancellationToken.None);

        Assert.Equal(["mine"], hits.Select(h => h.Text));
    }

    [Fact]
    public async Task Failure_is_recorded_and_deleting_removes_chunks()
    {
        var failed = await Create(_owner, "bad");
        await _store.MarkFailedAsync(failed.Id, new string('e', 900), CancellationToken.None);
        var stored = (await _store.GetAsync(_owner, failed.Id, CancellationToken.None))!;
        Assert.Equal(DocumentStatus.Failed, stored.Status);
        Assert.Equal(500, stored.Error!.Length);

        var ok = await Create(_owner, "ok");
        await _store.SaveChunksAsync(ok.Id, [Chunk(0, "x", 1)], CancellationToken.None);
        Assert.True(await _store.DeleteAsync(_owner, ok.Id, CancellationToken.None));
        Assert.Equal(0, await _db.Chunks.CountAsync());
    }

    [Fact]
    public async Task Reindexing_replaces_chunks_and_deleted_documents_are_skipped()
    {
        var doc = await Create(_owner);
        await _store.SaveChunksAsync(doc.Id, [Chunk(0, "a", 1), Chunk(1, "b", 1)], CancellationToken.None);
        await _store.SaveChunksAsync(doc.Id, [Chunk(0, "c", 1)], CancellationToken.None);
        Assert.Equal(1, await _db.Chunks.CountAsync());

        await _store.DeleteAsync(_owner, doc.Id, CancellationToken.None);
        await _store.SaveChunksAsync(doc.Id, [Chunk(0, "late", 1)], CancellationToken.None); // indexer finished after delete
        Assert.Equal(0, await _db.Chunks.CountAsync());
    }

    [Fact]
    public async Task Message_sources_round_trip()
    {
        var conversations = new SqliteConversationStore(_db, TimeProvider.System);
        var conversation = await conversations.CreateAsync(_owner, "c", CancellationToken.None);
        MessageSource[] sources = [new(1, Guid.NewGuid(), "Guide", 3, "snippet", 0.812)];

        await conversations.AppendMessageAsync(conversation.Id, new NewMessage(ChatRole.Assistant, "answer [1]", MessageStatus.Complete, Sources: sources), CancellationToken.None);
        await conversations.AppendMessageAsync(conversation.Id, new NewMessage(ChatRole.User, "q", MessageStatus.Complete), CancellationToken.None);

        var messages = (await conversations.GetAsync(_owner, conversation.Id, CancellationToken.None))!.Messages;
        Assert.Equal(sources, messages[0].Sources);
        Assert.Null(messages[1].Sources);
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_documents()
    {
        var doc = await Create(_owner);
        await _store.SaveChunksAsync(doc.Id, [Chunk(0, "x", 1)], CancellationToken.None);

        await new SqliteUserStore(_db, TimeProvider.System).DeleteAsync(_owner, CancellationToken.None);

        Assert.Equal(0, await _db.Documents.CountAsync());
        Assert.Equal(0, await _db.Chunks.CountAsync());
    }
}
