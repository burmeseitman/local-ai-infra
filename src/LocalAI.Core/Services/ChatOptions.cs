using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Services;

/// <summary>Provider-independent chat limits and model policy. Bound from the "Chat" section.</summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>Preferred model. Only used if it is actually installed.</summary>
    [MaxLength(200)]
    public string? DefaultModel { get; set; }

    /// <summary>Maximum characters in a single message (the "prompt length" limit).</summary>
    [Range(1, 200_000)]
    public int MaxMessageLength { get; set; } = 8_000;

    /// <summary>Maximum characters across the whole conversation sent per request.</summary>
    [Range(1, 1_000_000)]
    public int MaxConversationLength { get; set; } = 32_000;

    [Range(1, 500)]
    public int MaxMessages { get; set; } = 50;

    /// <summary>Generation length used when the client does not specify one.</summary>
    [Range(1, 32_768)]
    public int DefaultMaxTokens { get; set; } = 1_024;

    /// <summary>Upper bound a client may request for generation length.</summary>
    [Range(1, 32_768)]
    public int MaxTokensLimit { get; set; } = 4_096;

    /// <summary>
    /// Some providers can proxy to remotely hosted models (e.g. Ollama "cloud" models).
    /// These are refused by default so inference stays on this machine.
    /// </summary>
    public bool AllowRemoteModels { get; set; }

    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan ModelListCacheDuration { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class ChatOptionsValidator : IValidateOptions<ChatOptions>
{
    public ValidateOptionsResult Validate(string? name, ChatOptions options)
    {
        var failures = new List<string>();
        if (options.DefaultMaxTokens > options.MaxTokensLimit)
        {
            failures.Add(
                $"Chat:DefaultMaxTokens ({options.DefaultMaxTokens}) must not exceed " +
                $"Chat:MaxTokensLimit ({options.MaxTokensLimit}).");
        }

        if (options.MaxMessageLength > options.MaxConversationLength)
        {
            failures.Add(
                $"Chat:MaxMessageLength ({options.MaxMessageLength}) must not exceed " +
                $"Chat:MaxConversationLength ({options.MaxConversationLength}).");
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultModel)
            && !ChatRequestValidator.IsValidModelName(options.DefaultModel))
        {
            failures.Add($"Chat:DefaultModel '{options.DefaultModel}' is not a valid model identifier.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
