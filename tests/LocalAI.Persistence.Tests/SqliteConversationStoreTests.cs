using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LocalAI.Persistence.Tests;

/// <summary>Runs against a real SQLite database (in-memory) with the real migrations applied.</summary>
public sealed class SqliteConversationStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ManualClock _clock = new();
    private readonly LocalAiDbContext _db;
    private readonly SqliteConversationStore _store;
    private readonly Guid _owner;

    public SqliteConversationStoreTests()
    {
        _connection.Open(); // the in-memory database lives as long as this connection
        _db = new LocalAiDbContext(new DbContextOptionsBuilder<LocalAiDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _store = new SqliteConversationStore(_db, _clock);
        _owner = new SqliteUserStore(_db, _clock).CreateAsync("owner", "hash", UserRole.User, CancellationToken.None).GetAwaiter().GetResult().Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static readonly ChatCompletionStats Stats =
        new(12, 40, TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

    [Fact]
    public void Migrations_are_up_to_date_with_the_model()
    {
        Assert.Empty(_db.Database.GetPendingMigrations());
        Assert.False(_db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Creates_and_reads_back_a_conversation_with_ordered_messages()
    {
        var created = await _store.CreateAsync(_owner, "Test", CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new NewMessage(ChatRole.User, "hi", MessageStatus.Complete), CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new NewMessage(ChatRole.Assistant, "hello", MessageStatus.Complete, "tiny:1b", Stats: Stats), CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new NewMessage(ChatRole.User, "more", MessageStatus.Complete), CancellationToken.None);
        await _store.AppendMessageAsync(created.Id,
            new NewMessage(ChatRole.Assistant, "par", MessageStatus.Error, "tiny:1b", "provider_timeout", "Timed out."), CancellationToken.None);

        var loaded = await _store.GetAsync(_owner, created.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("Test", loaded.Title);
        Assert.Equal([1, 2, 3, 4], loaded.Messages.Select(m => m.Sequence));
        Assert.Equal(["hi", "hello", "more", "par"], loaded.Messages.Select(m => m.Content));
        var reply = loaded.Messages[1];
        Assert.Equal((ChatRole.Assistant, MessageStatus.Complete, "tiny:1b"), (reply.Role, reply.Status, reply.Model));
        Assert.Equal(40, reply.Stats!.CompletionTokens);
        Assert.Equal(TimeSpan.FromMilliseconds(500), reply.Stats.GenerationDuration);
        Assert.Null(loaded.Messages[0].Stats);
        Assert.Equal(("provider_timeout", "Timed out."), (loaded.Messages[3].ErrorCode, loaded.Messages[3].ErrorMessage));
    }

    [Fact]
    public async Task Appending_updates_the_conversation_timestamp_and_list_order()
    {
        var older = await _store.CreateAsync(_owner, "older", CancellationToken.None);
        var newer = await _store.CreateAsync(_owner, "newer", CancellationToken.None);

        await _store.AppendMessageAsync(older.Id, new NewMessage(ChatRole.User, "bump", MessageStatus.Complete), CancellationToken.None);
        await _store.AppendMessageAsync(older.Id, new NewMessage(ChatRole.Assistant, "ok", MessageStatus.Complete, "m:1b"), CancellationToken.None);

        var list = await _store.ListAsync(_owner, 10, CancellationToken.None);

        Assert.Equal([older.Id, newer.Id], list.Select(c => c.Id));
        Assert.Equal((2, "m:1b"), (list[0].MessageCount, list[0].LastModel));
        Assert.Equal((0, (string?)null), (list[1].MessageCount, list[1].LastModel));
        Assert.True(list[0].UpdatedAt > list[0].CreatedAt);
    }

    [Fact]
    public async Task List_respects_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.CreateAsync(_owner, $"c{i}", CancellationToken.None);
        }

        Assert.Equal(["c4", "c3"], (await _store.ListAsync(_owner, 2, CancellationToken.None)).Select(c => c.Title));
    }

    [Fact]
    public async Task Conversations_are_invisible_to_other_owners()
    {
        var created = await _store.CreateAsync(_owner, "private", CancellationToken.None);
        var other = (await new SqliteUserStore(_db, _clock).CreateAsync("other", "hash", UserRole.User, CancellationToken.None)).Id;

        Assert.Empty(await _store.ListAsync(other, 10, CancellationToken.None));
        Assert.Null(await _store.GetAsync(other, created.Id, CancellationToken.None));
        Assert.False(await _store.RenameAsync(other, created.Id, "stolen", CancellationToken.None));
        Assert.False(await _store.DeleteAsync(other, created.Id, CancellationToken.None));
        Assert.Equal("private", (await _store.GetAsync(_owner, created.Id, CancellationToken.None))!.Title);
    }

    [Fact]
    public async Task Rename_and_missing_ids()
    {
        var created = await _store.CreateAsync(_owner, "a", CancellationToken.None);

        Assert.True(await _store.RenameAsync(_owner, created.Id, "b", CancellationToken.None));
        Assert.False(await _store.RenameAsync(_owner, Guid.NewGuid(), "x", CancellationToken.None));
        Assert.Equal("b", (await _store.GetAsync(_owner, created.Id, CancellationToken.None))!.Title);
        Assert.Null(await _store.GetAsync(_owner, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_cascades_to_messages()
    {
        var created = await _store.CreateAsync(_owner, "doomed", CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new NewMessage(ChatRole.User, "x", MessageStatus.Complete), CancellationToken.None);

        Assert.True(await _store.DeleteAsync(_owner, created.Id, CancellationToken.None));
        Assert.False(await _store.DeleteAsync(_owner, created.Id, CancellationToken.None));
        Assert.Equal(0, await _db.Messages.CountAsync());
    }

    [Fact]
    public async Task Appending_to_a_missing_conversation_throws_not_found()
    {
        var ex = await Assert.ThrowsAsync<ConversationException>(() =>
            _store.AppendMessageAsync(Guid.NewGuid(), new NewMessage(ChatRole.User, "x", MessageStatus.Complete), CancellationToken.None));

        Assert.Equal(ConversationErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Delete_message_removes_only_that_message_and_sequence_continues()
    {
        var created = await _store.CreateAsync(_owner, "c", CancellationToken.None);
        await _store.AppendMessageAsync(created.Id, new NewMessage(ChatRole.User, "q", MessageStatus.Complete), CancellationToken.None);
        var reply = await _store.AppendMessageAsync(created.Id, new NewMessage(ChatRole.Assistant, "bad", MessageStatus.Error), CancellationToken.None);

        Assert.True(await _store.DeleteMessageAsync(created.Id, reply.Id, CancellationToken.None));
        Assert.False(await _store.DeleteMessageAsync(Guid.NewGuid(), reply.Id, CancellationToken.None));
        var again = await _store.AppendMessageAsync(created.Id, new NewMessage(ChatRole.Assistant, "good", MessageStatus.Complete), CancellationToken.None);

        var loaded = await _store.GetAsync(_owner, created.Id, CancellationToken.None);
        Assert.Equal(["q", "good"], loaded!.Messages.Select(m => m.Content));
        Assert.Equal(2, again.Sequence);
    }

    [Fact]
    public async Task Long_unicode_content_round_trips()
    {
        var created = await _store.CreateAsync(_owner, "unicode", CancellationToken.None);
        var content = string.Concat(Enumerable.Repeat("မင်္ဂလာပါ 🚀 ", 2_000));

        await _store.AppendMessageAsync(created.Id, new NewMessage(ChatRole.User, content, MessageStatus.Complete), CancellationToken.None);

        Assert.Equal(content, (await _store.GetAsync(_owner, created.Id, CancellationToken.None))!.Messages[0].Content);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }
}
