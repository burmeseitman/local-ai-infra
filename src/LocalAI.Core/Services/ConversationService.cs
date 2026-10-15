using System.Runtime.CompilerServices;
using System.Text;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Services;

/// <summary>A prepared generation for a stored conversation. Holds the conversation lock until disposed.</summary>
public sealed class ConversationTurn(
    Guid conversationId,
    Guid? userMessageId,
    ChatCompletionRequest request,
    IDisposable lease) : IDisposable
{
    public Guid ConversationId { get; } = conversationId;

    /// <summary>The user message stored for this turn; null when regenerating.</summary>
    public Guid? UserMessageId { get; } = userMessageId;

    /// <summary>Request sent to the provider: the context window ending with the user's message.</summary>
    public ChatCompletionRequest Request { get; } = request;

    public void Dispose() => lease.Dispose();
}

/// <summary>
/// Server-side conversation history. The server is the source of truth: clients send only
/// the new message, and the assistant reply is persisted whatever the outcome (complete,
/// stopped by the user, or failed) so a reload or dropped connection loses nothing.
/// </summary>
public sealed class ConversationService(
    IConversationStore store,
    ChatService chat,
    ConversationLocks locks,
    IOptions<ChatOptions> options,
    ILogger<ConversationService> logger)
{
    public const string DefaultTitle = "New conversation";
    public const int MaxTitleLength = 120;
    public const int MaxListLimit = 200;
    private const int GeneratedTitleLength = 60;

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(int limit, CancellationToken cancellationToken) =>
        store.ListAsync(Math.Clamp(limit, 1, MaxListLimit), cancellationToken);

    public async Task<Conversation> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetAsync(id, cancellationToken) ?? throw ConversationException.NotFound(id);

    public Task<Conversation> CreateAsync(string? title, CancellationToken cancellationToken) =>
        store.CreateAsync(NormalizeTitle(title) ?? DefaultTitle, cancellationToken);

    public async Task RenameAsync(Guid id, string title, CancellationToken cancellationToken)
    {
        if (!await store.RenameAsync(id, NormalizeTitle(title) ?? DefaultTitle, cancellationToken))
        {
            throw ConversationException.NotFound(id);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (locks.IsHeld(id))
        {
            throw ConversationException.Busy();
        }

        if (!await store.DeleteAsync(id, cancellationToken))
        {
            throw ConversationException.NotFound(id);
        }
    }

    /// <summary>
    /// Validates the model and stores the user's message. <paramref name="userTurn"/> must be a
    /// validated request containing exactly the new user message.
    /// </summary>
    public async Task<ConversationTurn> BeginTurnAsync(
        Guid conversationId, ChatCompletionRequest userTurn, CancellationToken cancellationToken)
    {
        var userMessage = userTurn.Messages[^1];
        var lease = locks.Acquire(conversationId);
        try
        {
            var conversation = await GetAsync(conversationId, cancellationToken);

            // Resolve the model before storing anything, so a bad model leaves no orphan message.
            var prepared = await chat.PrepareAsync(userTurn, cancellationToken);
            var stored = await store.AppendMessageAsync(
                conversationId, new NewMessage(ChatRole.User, userMessage.Content, MessageStatus.Complete), cancellationToken);

            if (conversation.Messages.Count == 0 && conversation.Title == DefaultTitle)
            {
                await store.RenameAsync(conversationId, TitleFrom(userMessage.Content), cancellationToken);
            }

            var history = Window([.. conversation.Messages, stored]);
            return new ConversationTurn(conversationId, stored.Id, prepared with { Messages = history }, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Replaces the last assistant reply (if any) with a new generation.</summary>
    public async Task<ConversationTurn> BeginRegenerateAsync(
        Guid conversationId, string model, GenerationOptions generation, CancellationToken cancellationToken)
    {
        var lease = locks.Acquire(conversationId);
        try
        {
            var conversation = await GetAsync(conversationId, cancellationToken);
            var replaced = conversation.Messages.Count > 0 && conversation.Messages[^1].Role == ChatRole.Assistant
                ? conversation.Messages[^1]
                : null;
            var remaining = replaced is null ? conversation.Messages : conversation.Messages.SkipLast(1).ToArray();
            if (remaining.Count == 0 || remaining[^1].Role != ChatRole.User)
            {
                throw ConversationException.NothingToRegenerate();
            }

            var prepared = await chat.PrepareAsync(new ChatCompletionRequest(model, [], generation), cancellationToken);
            if (replaced is not null)
            {
                await store.DeleteMessageAsync(conversationId, replaced.Id, cancellationToken);
            }

            return new ConversationTurn(conversationId, null, prepared with { Messages = Window(remaining) }, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Streams the reply and persists it when the stream ends for any reason. Persistence
    /// ignores the request's cancellation token so a stopped reply is still saved.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamChunk> StreamTurnAsync(
        ConversationTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var _ = turn;
        var content = new StringBuilder();
        await using var stream = chat.StreamAsync(turn.Request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatStreamChunk chunk;
            try
            {
                if (!await stream.MoveNextAsync())
                {
                    yield break; // ChatService guarantees a final chunk or an exception.
                }

                chunk = stream.Current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await SaveReplyAsync(turn, new NewMessage(
                    ChatRole.Assistant, content.ToString(), MessageStatus.Stopped, turn.Request.Model));
                throw;
            }
            catch (InferenceException ex)
            {
                await SaveReplyAsync(turn, new NewMessage(
                    ChatRole.Assistant, content.ToString(), MessageStatus.Error, turn.Request.Model,
                    InferenceMetrics.ToCode(ex.Code), ex.Message));
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await SaveReplyAsync(turn, new NewMessage(
                    ChatRole.Assistant, content.ToString(), MessageStatus.Error, turn.Request.Model,
                    "internal_error", "An unexpected error occurred."));
                throw;
            }

            content.Append(chunk.Content);
            if (chunk.IsFinal)
            {
                await SaveReplyAsync(turn, new NewMessage(
                    ChatRole.Assistant, content.ToString(), MessageStatus.Complete, turn.Request.Model, Stats: chunk.Stats));
            }

            yield return chunk;
        }
    }

    public static string TitleFrom(string content)
    {
        var singleLine = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= GeneratedTitleLength ? singleLine : singleLine[..(GeneratedTitleLength - 1)].TrimEnd() + "…";
    }

    private static string? NormalizeTitle(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxTitleLength ? trimmed : trimmed[..MaxTitleLength];
    }

    private IReadOnlyList<ChatMessage> Window(IEnumerable<StoredMessage> messages) =>
        HistoryWindow.Select(messages, options.Value.MaxMessages, options.Value.MaxConversationLength);

    private async Task SaveReplyAsync(ConversationTurn turn, NewMessage reply)
    {
        try
        {
            await store.AppendMessageAsync(turn.ConversationId, reply, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Never mask the generation outcome with a storage failure; log and continue.
            logger.LogError(ex, "Failed to persist {Status} reply for conversation {ConversationId}", reply.Status, turn.ConversationId);
        }
    }
}
