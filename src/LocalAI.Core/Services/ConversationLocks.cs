using System.Collections.Concurrent;
using LocalAI.Core.Exceptions;

namespace LocalAI.Core.Services;

/// <summary>
/// Allows at most one in-flight generation per conversation, so two turns can never
/// interleave their messages. In-process only: valid for a single API instance.
/// </summary>
public sealed class ConversationLocks
{
    private readonly ConcurrentDictionary<Guid, byte> _held = new();

    /// <exception cref="ConversationException">A generation is already running for this conversation.</exception>
    public IDisposable Acquire(Guid conversationId) =>
        _held.TryAdd(conversationId, 0) ? new Lease(this, conversationId) : throw ConversationException.Busy();

    public bool IsHeld(Guid conversationId) => _held.ContainsKey(conversationId);

    private sealed class Lease(ConversationLocks owner, Guid id) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner._held.TryRemove(id, out _);
            }
        }
    }
}
