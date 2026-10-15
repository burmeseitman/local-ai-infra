using LocalAI.Core.Models;

namespace LocalAI.Core.Services;

/// <summary>
/// Chooses which stored messages are sent to the model. Everything is persisted, but only
/// the most recent turns that fit the configured limits form the context window.
/// </summary>
public static class HistoryWindow
{
    /// <summary>
    /// Returns, in chronological order, the newest eligible messages whose combined length
    /// fits <paramref name="maxCharacters"/> and whose count fits <paramref name="maxMessages"/>.
    /// Failed or empty assistant turns are never sent.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Select(IEnumerable<StoredMessage> messages, int maxMessages, int maxCharacters)
    {
        var eligible = messages
            .Where(m => m.Role == ChatRole.User
                || (m.Status is MessageStatus.Complete or MessageStatus.Stopped && !string.IsNullOrWhiteSpace(m.Content)))
            .OrderBy(m => m.Sequence)
            .ToArray();

        var selected = new List<ChatMessage>();
        var characters = 0;
        for (var i = eligible.Length - 1; i >= 0 && selected.Count < maxMessages; i--)
        {
            var message = eligible[i];
            if (characters + message.Content.Length > maxCharacters)
            {
                break;
            }

            characters += message.Content.Length;
            selected.Add(new ChatMessage(message.Role, message.Content));
        }

        selected.Reverse();
        return selected;
    }
}
