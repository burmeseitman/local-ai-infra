namespace LocalAI.Core.Exceptions;

public enum ConversationErrorCode
{
    NotFound,
    Busy,
    NothingToRegenerate,
}

/// <summary>Conversation-level failures. <see cref="Exception.Message"/> is safe to show to clients.</summary>
public sealed class ConversationException(ConversationErrorCode code, string message) : Exception(message)
{
    public ConversationErrorCode Code { get; } = code;

    public static ConversationException NotFound(Guid id) =>
        new(ConversationErrorCode.NotFound, $"Conversation '{id}' does not exist.");

    public static ConversationException Busy() =>
        new(ConversationErrorCode.Busy, "A reply is already being generated in this conversation. Wait for it or stop it first.");

    public static ConversationException NothingToRegenerate() =>
        new(ConversationErrorCode.NothingToRegenerate, "There is no user message to generate a reply for.");
}
