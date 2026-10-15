namespace LocalAI.Core.Models;

/// <summary>
/// Metadata for a model reported by the provider. Unknown values stay null.
/// <see cref="Name"/> is the full identifier used for inference (e.g. "qwen2.5:0.5b").
/// </summary>
public sealed record ModelInfo(
    string Name,
    string DisplayName,
    string? Tag,
    string? Family,
    string? Digest,
    long? SizeBytes,
    DateTimeOffset? ModifiedAt,
    string? ParameterSize,
    string? QuantizationLevel,
    bool IsRemote);

/// <summary>Result of a cheap connectivity probe (no inference).</summary>
public sealed record ProviderHealth(
    bool IsReachable,
    string? Version,
    TimeSpan Latency,
    string? Error);

/// <summary>Aggregated provider + model availability, as exposed by status endpoints.</summary>
public sealed record ProviderStatus(
    string Provider,
    bool IsReachable,
    string? Version,
    TimeSpan? Latency,
    string? Error,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastSuccessfulCheckAt,
    ModelAvailability? Models);

public sealed record ModelAvailability(
    int InstalledCount,
    int UsableCount,
    string? DefaultModel,
    bool DefaultModelInstalled);
