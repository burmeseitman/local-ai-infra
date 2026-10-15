using LocalAI.Core.Models;

namespace LocalAI.Core.Interfaces;

/// <summary>
/// Durable conversation storage. Implementations assign ids, sequence numbers and
/// timestamps, and keep <see cref="Conversation.UpdatedAt"/> current on every change.
/// </summary>
public interface IConversationStore
{
    /// <summary>Most recently updated first.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Returns the conversation with messages ordered by sequence, or null.</summary>
    Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Conversation> CreateAsync(string title, CancellationToken cancellationToken);

    Task<bool> RenameAsync(Guid id, string title, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <exception cref="Exceptions.ConversationException">The conversation does not exist.</exception>
    Task<StoredMessage> AppendMessageAsync(Guid conversationId, NewMessage message, CancellationToken cancellationToken);

    Task<bool> DeleteMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken);
}
