namespace LocalAI.Core.Exceptions;

public enum InferenceErrorCode
{
    ProviderUnavailable,
    ProviderTimeout,
    ProviderInvalidResponse,
    ProviderError,
    ModelNotFound,
    ModelNotAllowed,
    ModelLoadFailed,
}

/// <summary>
/// A normalized, provider-independent failure. <see cref="Exception.Message"/> is
/// safe to show to clients; provider-specific detail lives in <see cref="Exception.InnerException"/>
/// or <see cref="ProviderDetail"/> and is only logged.
/// </summary>
public sealed class InferenceException : Exception
{
    public InferenceException(
        InferenceErrorCode code,
        string message,
        string? providerDetail = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        ProviderDetail = providerDetail;
    }

    public InferenceErrorCode Code { get; }

    /// <summary>Raw upstream error text, for server logs only.</summary>
    public string? ProviderDetail { get; }

    /// <summary>True when retrying the same request later may succeed.</summary>
    public bool IsTransient => Code is InferenceErrorCode.ProviderUnavailable
        or InferenceErrorCode.ProviderTimeout
        or InferenceErrorCode.ProviderError
        or InferenceErrorCode.ModelLoadFailed;

    public static InferenceException Unavailable(Exception? inner = null) =>
        new(InferenceErrorCode.ProviderUnavailable,
            "The local inference service is not reachable. It may be stopped or still starting.",
            inner?.Message, inner);

    public static InferenceException Timeout(TimeSpan limit) =>
        new(InferenceErrorCode.ProviderTimeout,
            $"The inference service did not respond within {limit.TotalSeconds:0} seconds.");

    public static InferenceException InvalidResponse(string detail, Exception? inner = null) =>
        new(InferenceErrorCode.ProviderInvalidResponse,
            "The inference service returned an unexpected response.", detail, inner);

    public static InferenceException ModelNotFound(string model) =>
        new(InferenceErrorCode.ModelNotFound,
            $"Model '{model}' is not installed in the local inference service.");
}
