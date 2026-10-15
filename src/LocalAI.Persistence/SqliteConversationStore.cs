using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using LocalAI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalAI.Persistence;

internal sealed class SqliteConversationStore(LocalAiDbContext db, TimeProvider timeProvider) : IConversationStore
{
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await db.Conversations
            .AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .Take(limit)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.CreatedAt,
                c.UpdatedAt,
                MessageCount = c.Messages.Count,
                LastModel = c.Messages
                    .Where(m => m.Model != null)
                    .OrderByDescending(m => m.Sequence)
                    .Select(m => m.Model)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new ConversationSummary(r.Id, r.Title, r.CreatedAt, r.UpdatedAt, r.MessageCount, r.LastModel)).ToArray();
    }

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.Conversations
            .AsNoTracking()
            .Include(c => c.Messages.OrderBy(m => m.Sequence))
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return entity is null ? null : ToConversation(entity);
    }

    public async Task<Conversation> CreateAsync(string title, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var entity = new ConversationEntity { Id = Guid.NewGuid(), Title = title, CreatedAt = now, UpdatedAt = now };
        db.Conversations.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToConversation(entity);
    }

    public async Task<bool> RenameAsync(Guid id, string title, CancellationToken cancellationToken)
    {
        var updated = await db.Conversations
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Title, title).SetProperty(c => c.UpdatedAt, timeProvider.GetUtcNow()),
                cancellationToken);
        return updated > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // Messages are removed by the ON DELETE CASCADE foreign key.
        return await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<StoredMessage> AppendMessageAsync(Guid conversationId, NewMessage message, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw ConversationException.NotFound(conversationId);

        var lastSequence = await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .MaxAsync(m => (int?)m.Sequence, cancellationToken) ?? 0;

        var now = timeProvider.GetUtcNow();
        var entity = new MessageEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Sequence = lastSequence + 1,
            Role = message.Role.ToString(),
            Content = message.Content,
            Status = message.Status.ToString(),
            Model = message.Model,
            ErrorCode = message.ErrorCode,
            ErrorMessage = message.ErrorMessage,
            PromptTokens = message.Stats?.PromptTokens,
            CompletionTokens = message.Stats?.CompletionTokens,
            TotalDurationMs = message.Stats?.TotalDuration?.TotalMilliseconds,
            LoadDurationMs = message.Stats?.LoadDuration?.TotalMilliseconds,
            GenerationDurationMs = message.Stats?.GenerationDuration?.TotalMilliseconds,
            CreatedAt = now,
        };
        db.Messages.Add(entity);
        conversation.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToMessage(entity);
    }

    public async Task<bool> DeleteMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var deleted = await db.Messages
            .Where(m => m.ConversationId == conversationId && m.Id == messageId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            await db.Conversations
                .Where(c => c.Id == conversationId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken);
        }

        return deleted > 0;
    }

    private static Conversation ToConversation(ConversationEntity e) =>
        new(e.Id, e.Title, e.CreatedAt, e.UpdatedAt, e.Messages.OrderBy(m => m.Sequence).Select(ToMessage).ToArray());

    private static StoredMessage ToMessage(MessageEntity m) => new(
        m.Id,
        m.Sequence,
        Enum.Parse<ChatRole>(m.Role),
        m.Content,
        Enum.Parse<MessageStatus>(m.Status),
        m.Model,
        m.ErrorCode,
        m.ErrorMessage,
        m.PromptTokens is null && m.CompletionTokens is null && m.TotalDurationMs is null
            ? null
            : new ChatCompletionStats(
                m.PromptTokens,
                m.CompletionTokens,
                FromMs(m.TotalDurationMs),
                FromMs(m.LoadDurationMs),
                FromMs(m.GenerationDurationMs)),
        m.CreatedAt);

    private static TimeSpan? FromMs(double? value) => value is null ? null : TimeSpan.FromMilliseconds(value.Value);
}
