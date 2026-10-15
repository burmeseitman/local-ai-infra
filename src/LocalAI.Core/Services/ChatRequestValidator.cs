using System.Text.RegularExpressions;
using LocalAI.Core.Models;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Services;

public sealed record ChatValidationResult(
    ChatCompletionRequest? Request,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Request is not null && Errors.Count == 0;
}

/// <summary>Turns untrusted client input into a bounded <see cref="ChatCompletionRequest"/>.</summary>
public sealed partial class ChatRequestValidator(IOptions<ChatOptions> options)
{
    public const double MinTemperature = 0;
    public const double MaxTemperature = 2;
    private const int MaxModelNameLength = 200;

    // namespace/name:tag style identifiers, e.g. "qwen2.5:0.5b", "library/llama3.2:1b".
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]*(:[A-Za-z0-9][A-Za-z0-9._\-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelNamePattern();

    public static bool IsValidModelName(string value) =>
        value.Length <= MaxModelNameLength && ModelNamePattern().IsMatch(value);

    /// <summary>
    /// Validates one new user message for a stored conversation. Error keys are
    /// <c>model</c>, <c>content</c>, <c>temperature</c> and <c>maxTokens</c>.
    /// </summary>
    public ChatValidationResult ValidateTurn(string? model, string? content, double? temperature, int? maxTokens)
    {
        var result = Validate(new UnvalidatedChatRequest(model, [new UnvalidatedChatMessage("user", content)], temperature, maxTokens));
        var errors = result.Errors.ToDictionary(
            e => e.Key.StartsWith("messages", StringComparison.Ordinal) ? "content" : e.Key,
            e => e.Value);
        return result with { Errors = errors };
    }

    /// <summary>
    /// Validates model and sampling options only (used when regenerating from stored history).
    /// The returned request's messages are a placeholder and must not be sent.
    /// </summary>
    public ChatValidationResult ValidateGeneration(string? model, double? temperature, int? maxTokens) =>
        Validate(new UnvalidatedChatRequest(model, [new UnvalidatedChatMessage("user", "-")], temperature, maxTokens));

    public ChatValidationResult Validate(UnvalidatedChatRequest input)
    {
        var limits = options.Value;
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }

            list.Add(message);
        }

        var model = input.Model?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            Add("model", "A model is required.");
        }
        else if (!IsValidModelName(model))
        {
            Add("model", "The model identifier is not valid.");
        }

        var messages = new List<ChatMessage>();
        if (input.Messages is null || input.Messages.Count == 0)
        {
            Add("messages", "At least one message is required.");
        }
        else if (input.Messages.Count > limits.MaxMessages)
        {
            Add("messages", $"A conversation may contain at most {limits.MaxMessages} messages.");
        }
        else
        {
            var totalLength = 0;
            for (var i = 0; i < input.Messages.Count; i++)
            {
                var message = input.Messages[i];
                var key = $"messages[{i}]";
                if (message is null)
                {
                    Add(key, "Message must not be null.");
                    continue;
                }

                if (!TryParseRole(message.Role, out var role))
                {
                    Add($"{key}.role", "Role must be one of: system, user, assistant.");
                }

                if (string.IsNullOrWhiteSpace(message.Content))
                {
                    Add($"{key}.content", "Message content must not be empty.");
                    continue;
                }

                if (message.Content.Length > limits.MaxMessageLength)
                {
                    Add($"{key}.content", $"Message content must be at most {limits.MaxMessageLength} characters.");
                    continue;
                }

                totalLength += message.Content.Length;
                messages.Add(new ChatMessage(role, message.Content));
            }

            if (totalLength > limits.MaxConversationLength)
            {
                Add("messages", $"The conversation must be at most {limits.MaxConversationLength} characters in total.");
            }

            if (input.Messages[^1] is { } last && TryParseRole(last.Role, out var lastRole) && lastRole != ChatRole.User)
            {
                Add("messages", "The last message must have the 'user' role.");
            }
        }

        if (input.Temperature is { } temperature
            && (double.IsNaN(temperature) || temperature < MinTemperature || temperature > MaxTemperature))
        {
            Add("temperature", $"Temperature must be between {MinTemperature} and {MaxTemperature}.");
        }

        if (input.MaxTokens is { } maxTokens && (maxTokens < 1 || maxTokens > limits.MaxTokensLimit))
        {
            Add("maxTokens", $"maxTokens must be between 1 and {limits.MaxTokensLimit}.");
        }

        if (errors.Count > 0)
        {
            return new ChatValidationResult(null, errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }

        var request = new ChatCompletionRequest(
            model!,
            messages,
            new GenerationOptions(input.Temperature, input.MaxTokens ?? limits.DefaultMaxTokens));
        return new ChatValidationResult(request, new Dictionary<string, string[]>());
    }

    private static bool TryParseRole(string? value, out ChatRole role)
    {
        // Enum.TryParse would also accept numeric strings such as "1"; be explicit.
        switch (value?.Trim().ToLowerInvariant())
        {
            case "system": role = ChatRole.System; return true;
            case "user": role = ChatRole.User; return true;
            case "assistant": role = ChatRole.Assistant; return true;
            default: role = default; return false;
        }
    }
}
