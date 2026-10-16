using LocalAI.Core.Models;
using LocalAI.Core.Services;

namespace LocalAI.Api.Contracts;

public sealed record ModelDto(
    string Name,
    string DisplayName,
    string? Tag,
    string? Family,
    string? Digest,
    long? SizeBytes,
    DateTimeOffset? ModifiedAt,
    string? ParameterSize,
    string? QuantizationLevel,
    bool IsRemote,
    bool IsAvailable,
    bool IsDefault,
    bool IsEmbedding);

public sealed record ModelListResponse(
    string Provider,
    IReadOnlyList<ModelDto> Models,
    string? DefaultModel,
    DateTimeOffset FetchedAt);

/// <summary>Provider connectivity + model availability. Inference capability is reported separately.</summary>
public sealed record ProviderStatusDto(
    string Provider,
    bool Reachable,
    string? Version,
    double? LatencyMs,
    string? Error,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastSuccessfulCheckAt,
    int? InstalledModelCount,
    int? UsableModelCount,
    string? DefaultModel,
    bool DefaultModelInstalled)
{
    public static ProviderStatusDto From(ProviderStatus status, string? configuredDefault) => new(
        status.Provider,
        status.IsReachable,
        status.Version,
        status.Latency is { } latency ? Math.Round(latency.TotalMilliseconds, 1) : null,
        status.Error,
        status.CheckedAt,
        status.LastSuccessfulCheckAt,
        status.Models?.InstalledCount,
        status.Models?.UsableCount,
        status.Models?.DefaultModel ?? configuredDefault,
        status.Models?.DefaultModelInstalled ?? false);
}

public sealed record ModelStatusResponse(
    ProviderStatusDto Provider,
    DateTimeOffset? LastSuccessfulInferenceAt);

public sealed record HealthResponse(
    string Status,
    string Service,
    string Version,
    string Environment,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    DateTimeOffset Timestamp);

/// <summary>Readiness: "ready" (200), "degraded" (503, provider up but no usable model), "unavailable" (503).</summary>
public sealed record ReadinessResponse(
    string Status,
    ReadinessChecks Checks,
    DateTimeOffset CheckedAt);

public sealed record ReadinessChecks(
    CheckResult Api,
    CheckResult Provider,
    CheckResult Models);

public sealed record CheckResult(string Status, string? Detail);

public sealed record SystemOverviewResponse(
    ApiInfoDto Api,
    ProviderStatusDto Provider,
    InferenceStatsDto Inference,
    LimitsDto Limits,
    DateTimeOffset GeneratedAt);

public sealed record ApiInfoDto(
    string Status,
    string Version,
    string Environment,
    DateTimeOffset StartedAt,
    double UptimeSeconds);

public sealed record InferenceStatsDto(
    DateTimeOffset Since,
    long TotalRequests,
    long Succeeded,
    long Failed,
    long Cancelled,
    int ActiveGenerations,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastCompletionModel,
    CompletionStatsDto? LastCompletion,
    IReadOnlyList<RecentFailure> RecentFailures)
{
    public static InferenceStatsDto From(InferenceMetricsSnapshot s) => new(
        s.Since,
        s.TotalRequests,
        s.Succeeded,
        s.Failed,
        s.Cancelled,
        s.ActiveGenerations,
        s.LastSuccessAt,
        s.LastFailureAt,
        s.LastCompletionModel,
        CompletionStatsDto.From(s.LastCompletionStats),
        s.RecentFailures);
}

public sealed record LimitsDto(
    int MaxMessageLength,
    int MaxConversationLength,
    int MaxMessages,
    int DefaultMaxTokens,
    int MaxTokensLimit,
    int MaxConcurrentGenerations,
    double InferenceTimeoutSeconds,
    bool AllowRemoteModels);
