using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LocalAI.Providers.Ollama;

/// <summary>Bound from the "Ollama" configuration section.</summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Base URL of the Ollama HTTP API, e.g. http://ollama:11434 inside Docker.</summary>
    [Required]
    public Uri? BaseUrl { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout for cheap metadata calls (version, model list).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan MetadataTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for a whole generation, including model load time.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan InferenceTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Optional Ollama keep_alive value (e.g. "5m", "0"). Null uses the server default.</summary>
    [MaxLength(16)]
    public string? KeepAlive { get; set; }

    /// <summary>Largest single NDJSON line accepted from Ollama.</summary>
    [Range(1_024, 16 * 1_024 * 1_024)]
    public int MaxResponseLineBytes { get; set; } = 1_024 * 1_024;
}

internal sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        if (options.BaseUrl is not { IsAbsoluteUri: true } url || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail(
                "Ollama:BaseUrl must be an absolute http(s) URL, e.g. http://localhost:11434 or http://ollama:11434.");
        }

        return ValidateOptionsResult.Success;
    }
}
