using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Core.Services;
using LocalAI.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Tests;

public class ConversationServiceTests
{
    private readonly FakeChatProvider _provider = new();
    private readonly InMemoryConversationStore _store = new();
    private readonly InferenceMetrics _metrics = new(TimeProvider.System);
    private readonly ConversationService _service;

    public ConversationServiceTests()
    {
        var options = Options.Create(new ChatOptions { MaxMessages = 50, MaxConversationLength = 32_000 });
        var catalog = new ModelCatalog(_provider, options, TimeProvider.System);
        var chat = new ChatService(_provider, catalog, _metrics, NullLogger<ChatService>.Instance);
        _service = new ConversationService(_store, chat, new ConversationLocks(), options, NullLogger<ConversationService>.Instance);
    }

    private static ChatCompletionRequest Turn(string content, string model = "tiny:1b") =>
        new(model, [new ChatMessage(ChatRole.User, content)], new GenerationOptions(null, 64));

    private async Task<List<ChatStreamChunk>> Run(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in _service.StreamTurnAsync(turn, cancellationToken))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task Turn_persists_user_and_completed_reply_with_stats_and_auto_title()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);

        var turn = await _service.BeginTurnAsync(conversation.Id, Turn("  What is\nSSE?  "), CancellationToken.None);
        await Run(turn);

        var stored = await _service.GetAsync(conversation.Id, CancellationToken.None);
        Assert.Equal("What is SSE?", stored.Title);
        Assert.Collection(
            stored.Messages,
            m => Assert.Equal((ChatRole.User, "  What is\nSSE?  ", MessageStatus.Complete), (m.Role, m.Content, m.Status)),
            m =>
            {
                Assert.Equal((ChatRole.Assistant, "Hello, world", MessageStatus.Complete, "tiny:1b"), (m.Role, m.Content, m.Status, m.Model));
                Assert.Equal(2, m.Stats!.CompletionTokens);
            });
    }

    [Fact]
    public async Task Explicit_title_is_not_replaced()
    {
        var conversation = await _service.CreateAsync("Planning", CancellationToken.None);

        await Run(await _service.BeginTurnAsync(conversation.Id, Turn("hello"), CancellationToken.None));

        Assert.Equal("Planning", (await _service.GetAsync(conversation.Id, CancellationToken.None)).Title);
    }

    [Fact]
    public async Task Provider_receives_stored_history_ending_with_new_message_and_skips_failed_turns()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);
        await Run(await _service.BeginTurnAsync(conversation.Id, Turn("first"), CancellationToken.None));

        _provider.StreamHandler = (_, ct) => FakeChatProvider.Throwing(InferenceException.Unavailable(), ct);
        await Assert.ThrowsAsync<InferenceException>(async () =>
            await Run(await _service.BeginTurnAsync(conversation.Id, Turn("second"), CancellationToken.None)));

        _provider.StreamHandler = (_, ct) => FakeChatProvider.Yield(ct, new ChatStreamChunk("ok"), FakeChatProvider.Final());
        await Run(await _service.BeginTurnAsync(conversation.Id, Turn("third"), CancellationToken.None));

        Assert.Equal(
            [(ChatRole.User, "first"), (ChatRole.Assistant, "Hello, world"), (ChatRole.User, "second"), (ChatRole.User, "third")],
            _provider.LastRequest!.Messages.Select(m => (m.Role, m.Content)));
    }

    [Fact]
    public async Task Failed_reply_is_persisted_with_error_code_and_partial_text()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);
        _provider.StreamHandler = (_, ct) => FakeChatProvider.Throwing(
            InferenceException.Timeout(TimeSpan.FromSeconds(30)), ct, new ChatStreamChunk("partial"));

        await Assert.ThrowsAsync<InferenceException>(async () =>
            await Run(await _service.BeginTurnAsync(conversation.Id, Turn("go"), CancellationToken.None)));

        var reply = (await _service.GetAsync(conversation.Id, CancellationToken.None)).Messages[^1];
        Assert.Equal((MessageStatus.Error, "partial", "provider_timeout"), (reply.Status, reply.Content, reply.ErrorCode));
        Assert.NotNull(reply.ErrorMessage);
    }

    [Fact]
    public async Task Cancelled_reply_is_persisted_as_stopped_and_lock_is_released()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);
        _provider.StreamHandler = (_, ct) => _provider.HangAfter(new ChatStreamChunk("half"), ct);
        using var cts = new CancellationTokenSource();

        var turn = await _service.BeginTurnAsync(conversation.Id, Turn("long task"), CancellationToken.None);
        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in _service.StreamTurnAsync(turn, cts.Token))
            {
                await cts.CancelAsync();
            }
        });

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        var reply = (await _service.GetAsync(conversation.Id, CancellationToken.None)).Messages[^1];
        Assert.Equal((ChatRole.Assistant, MessageStatus.Stopped, "half"), (reply.Role, reply.Status, reply.Content));

        // Lock released: a new turn can start.
        _provider.StreamHandler = (_, ct) => FakeChatProvider.Yield(ct, FakeChatProvider.Final());
        await Run(await _service.BeginTurnAsync(conversation.Id, Turn("again"), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_model_stores_nothing()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InferenceException>(() =>
            _service.BeginTurnAsync(conversation.Id, Turn("hi", model: "ghost:1b"), CancellationToken.None));

        Assert.Equal(InferenceErrorCode.ModelNotFound, ex.Code);
        Assert.Empty((await _service.GetAsync(conversation.Id, CancellationToken.None)).Messages);
    }

    [Fact]
    public async Task Missing_conversation_is_reported()
    {
        var ex = await Assert.ThrowsAsync<ConversationException>(() =>
            _service.BeginTurnAsync(Guid.NewGuid(), Turn("hi"), CancellationToken.None));

        Assert.Equal(ConversationErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Concurrent_turns_in_one_conversation_are_rejected()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);
        using var first = await _service.BeginTurnAsync(conversation.Id, Turn("one"), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ConversationException>(() =>
            _service.BeginTurnAsync(conversation.Id, Turn("two"), CancellationToken.None));
        Assert.Equal(ConversationErrorCode.Busy, ex.Code);

        var delete = await Assert.ThrowsAsync<ConversationException>(() => _service.DeleteAsync(conversation.Id, CancellationToken.None));
        Assert.Equal(ConversationErrorCode.Busy, delete.Code);
    }

    [Fact]
    public async Task Regenerate_replaces_last_reply()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);
        _provider.StreamHandler = (_, ct) => FakeChatProvider.Throwing(InferenceException.Unavailable(), ct, new ChatStreamChunk("bro"));
        await Assert.ThrowsAsync<InferenceException>(async () =>
            await Run(await _service.BeginTurnAsync(conversation.Id, Turn("question"), CancellationToken.None)));

        _provider.StreamHandler = (_, ct) => FakeChatProvider.Yield(ct, new ChatStreamChunk("fixed"), FakeChatProvider.Final());
        var turn = await _service.BeginRegenerateAsync(conversation.Id, "tiny:1b", new GenerationOptions(null, 64), CancellationToken.None);
        Assert.Null(turn.UserMessageId);
        await Run(turn);

        var messages = (await _service.GetAsync(conversation.Id, CancellationToken.None)).Messages;
        Assert.Equal([(ChatRole.User, "question"), (ChatRole.Assistant, "fixed")], messages.Select(m => (m.Role, m.Content)));
        Assert.Equal([(ChatRole.User, "question")], _provider.LastRequest!.Messages.Select(m => (m.Role, m.Content)));
    }

    [Fact]
    public async Task Regenerate_without_a_user_message_is_rejected()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ConversationException>(() =>
            _service.BeginRegenerateAsync(conversation.Id, "tiny:1b", new GenerationOptions(null, 64), CancellationToken.None));

        Assert.Equal(ConversationErrorCode.NothingToRegenerate, ex.Code);
    }

    [Fact]
    public async Task Delete_removes_conversation()
    {
        var conversation = await _service.CreateAsync(null, CancellationToken.None);

        await _service.DeleteAsync(conversation.Id, CancellationToken.None);

        await Assert.ThrowsAsync<ConversationException>(() => _service.GetAsync(conversation.Id, CancellationToken.None));
        await Assert.ThrowsAsync<ConversationException>(() => _service.DeleteAsync(conversation.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("Short", "Short")]
    [InlineData("  multi\n\tline   text ", "multi line text")]
    [InlineData("This is a very long first message that certainly exceeds the sixty character limit", "This is a very long first message that certainly exceeds th…")]
    public void Title_is_derived_from_the_first_message(string content, string expected) =>
        Assert.Equal(expected, ConversationService.TitleFrom(content));
}

public class HistoryWindowTests
{
    private static StoredMessage Msg(int seq, ChatRole role, string content, MessageStatus status = MessageStatus.Complete) =>
        new(Guid.NewGuid(), seq, role, content, status, null, null, null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Keeps_newest_messages_within_character_budget()
    {
        var messages = new[]
        {
            Msg(1, ChatRole.User, new string('a', 10)),
            Msg(2, ChatRole.Assistant, new string('b', 10)),
            Msg(3, ChatRole.User, new string('c', 10)),
        };

        var window = HistoryWindow.Select(messages, maxMessages: 10, maxCharacters: 25);

        Assert.Equal(["bbbbbbbbbb", "cccccccccc"], window.Select(m => m.Content));
    }

    [Fact]
    public void Keeps_newest_messages_within_count_budget()
    {
        var messages = Enumerable.Range(1, 6).Select(i => Msg(i, i % 2 == 1 ? ChatRole.User : ChatRole.Assistant, $"m{i}"));

        Assert.Equal(["m5", "m6"], HistoryWindow.Select(messages, maxMessages: 2, maxCharacters: 1_000).Select(m => m.Content));
    }

    [Fact]
    public void Excludes_failed_and_empty_replies_but_keeps_stopped_ones()
    {
        var messages = new[]
        {
            Msg(1, ChatRole.User, "q1"),
            Msg(2, ChatRole.Assistant, "partial", MessageStatus.Error),
            Msg(3, ChatRole.User, "q2"),
            Msg(4, ChatRole.Assistant, "", MessageStatus.Stopped),
            Msg(5, ChatRole.User, "q3"),
            Msg(6, ChatRole.Assistant, "half", MessageStatus.Stopped),
            Msg(7, ChatRole.User, "q4"),
        };

        Assert.Equal(["q1", "q2", "q3", "half", "q4"], HistoryWindow.Select(messages, 50, 1_000).Select(m => m.Content));
    }
}
