using LocalAI.Core.Models;

namespace LocalAI.Core.Interfaces;

/// <summary>
/// Durable conversation storage. Implementations assign ids, sequence numbers and
/// timestamps, and keep <see cref="Conversation.UpdatedAt"/> current on every change.
/// Every conversation belongs to one user; owner-scoped methods treat another user's
/// conversation exactly like a missing one.
/// </summary>
public interface IConversationStore
{
    /// <summary>Most recently updated first.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(Guid ownerId, int limit, CancellationToken cancellationToken);

    /// <summary>Returns the conversation with messages ordered by sequence, or null.</summary>
    Task<Conversation?> GetAsync(Guid ownerId, Guid id, CancellationToken cancellationToken);

    Task<Conversation> CreateAsync(Guid ownerId, string title, CancellationToken cancellationToken);

    Task<bool> RenameAsync(Guid ownerId, Guid id, string title, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid ownerId, Guid id, CancellationToken cancellationToken);

    /// <summary>Not owner-scoped: callers must have verified ownership first.</summary>
    /// <exception cref="Exceptions.ConversationException">The conversation does not exist.</exception>
    Task<StoredMessage> AppendMessageAsync(Guid conversationId, NewMessage message, CancellationToken cancellationToken);

    Task<bool> DeleteMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken);
}
