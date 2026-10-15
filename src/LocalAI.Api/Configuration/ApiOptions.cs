using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LocalAI.Api.Configuration;

/// <summary>HTTP-surface settings. Bound from the "Api" configuration section.</summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// Browser origins allowed to call the API cross-origin. Empty (the default) disables
    /// CORS entirely, which is correct when the web app proxies /api on the same origin.
    /// </summary>
    public string[] CorsAllowedOrigins { get; set; } = [];

    [Range(1_024, 10 * 1_024 * 1_024)]
    public long MaxRequestBodyBytes { get; set; } = 256 * 1_024;

    /// <summary>Concurrent generations allowed; additional requests receive HTTP 429.</summary>
    [Range(1, 64)]
    public int MaxConcurrentGenerations { get; set; } = 2;

    public bool EnableSwagger { get; set; }
}

internal sealed class ApiOptionsValidator : IValidateOptions<ApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiOptions options)
    {
        var failures = new List<string>();
        foreach (var origin in options.CorsAllowedOrigins)
        {
            if (origin.Contains('*', StringComparison.Ordinal))
            {
                failures.Add("Api:CorsAllowedOrigins must list explicit origins; wildcards are not allowed.");
            }
            else if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                     || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                     || uri.AbsolutePath != "/")
            {
                failures.Add($"Api:CorsAllowedOrigins entry '{origin}' must be an origin such as http://localhost:5173.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
