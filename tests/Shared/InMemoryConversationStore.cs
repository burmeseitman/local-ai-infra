using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;

namespace LocalAI.Tests.Shared;

/// <summary>Thread-safe in-memory <see cref="IConversationStore"/> for service-level tests.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Conversation> _items = new();
    private long _clock;

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<ConversationSummary> result = _items.Values
                .OrderByDescending(c => c.UpdatedAt)
                .Take(limit)
                .Select(c => new ConversationSummary(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages.Count,
                    c.Messages.LastOrDefault(m => m.Model is not null)?.Model))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.GetValueOrDefault(id));
        }
    }

    public Task<Conversation> CreateAsync(string title, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = Tick();
            var conversation = new Conversation(Guid.NewGuid(), title, now, now, []);
            _items[conversation.Id] = conversation;
            return Task.FromResult(conversation);
        }
    }

    public Task<bool> RenameAsync(Guid id, string title, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var c))
            {
                return Task.FromResult(false);
            }

            _items[id] = c with { Title = title, UpdatedAt = Tick() };
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Remove(id));
        }
    }

    public Task<StoredMessage> AppendMessageAsync(Guid conversationId, NewMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_items.TryGetValue(conversationId, out var c))
            {
                throw ConversationException.NotFound(conversationId);
            }

            var now = Tick();
            var stored = new StoredMessage(
                Guid.NewGuid(), (c.Messages.Count == 0 ? 0 : c.Messages[^1].Sequence) + 1, message.Role, message.Content, message.Status,
                message.Model, message.ErrorCode, message.ErrorMessage, message.Stats, now);
            _items[conversationId] = c with { Messages = [.. c.Messages, stored], UpdatedAt = now };
            return Task.FromResult(stored);
        }
    }

    public Task<bool> DeleteMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(conversationId, out var c) || c.Messages.All(m => m.Id != messageId))
            {
                return Task.FromResult(false);
            }

            _items[conversationId] = c with { Messages = c.Messages.Where(m => m.Id != messageId).ToArray(), UpdatedAt = Tick() };
            return Task.FromResult(true);
        }
    }

    private DateTimeOffset Tick() => DateTimeOffset.UnixEpoch.AddSeconds(++_clock);
}
