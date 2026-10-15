using LocalAI.Api.Configuration;
using LocalAI.Api.Contracts;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Services;
using LocalAI.Providers.Ollama;
using Microsoft.Extensions.Options;

namespace LocalAI.Api.Endpoints;

/// <summary>Health, readiness, model discovery and system overview. None of these run inference.</summary>
internal static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var health = app.MapGroup("/api/health").WithTags("Health");
        health.MapGet("/", GetHealth)
            .WithName("GetHealth")
            .WithSummary("Liveness: the API process is up. Does not check dependencies.");
        health.MapGet("/ready", GetReadinessAsync)
            .WithName("GetReadiness")
            .WithSummary("Readiness: provider reachable and at least one usable model installed.")
            .Produces<ReadinessResponse>()
            .Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable);

        var models = app.MapGroup("/api/models").WithTags("Models");
        models.MapGet("/", GetModelsAsync)
            .WithName("ListModels")
            .WithSummary("Models installed in the local provider. Pass refresh=true to bypass the short cache.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        models.MapGet("/status", GetModelStatusAsync)
            .WithName("GetModelStatus")
            .WithSummary("Provider connectivity and model availability. Always 200; inspect the payload.");

        app.MapGet("/api/system/overview", GetOverviewAsync)
            .WithTags("System")
            .WithName("GetSystemOverview")
            .WithSummary("Snapshot of API, provider, model and in-memory inference statistics.");

        return app;
    }

    private static HealthResponse GetHealth(ApiRuntimeInfo runtime, TimeProvider time) => new(
        "healthy",
        "LocalAI.Api",
        runtime.Version,
        runtime.Environment,
        runtime.StartedAt,
        Math.Round(runtime.Uptime.TotalSeconds),
        time.GetUtcNow());

    private static async Task<IResult> GetReadinessAsync(ProviderStatusService statusService, CancellationToken cancellationToken)
    {
        var status = await statusService.GetStatusAsync(cancellationToken);

        CheckResult provider = status.IsReachable
            ? new("pass", $"{status.Provider} {status.Version} reachable")
            : new("fail", status.Error);
        CheckResult models = status switch
        {
            { IsReachable: false } => new("unknown", "Provider unreachable."),
            { Models: null } => new("fail", status.Error ?? "Model list unavailable."),
            { Models.UsableCount: 0 } => new("fail", "No usable model is installed. Pull one with 'ollama pull <model>'."),
            _ => new("pass", $"{status.Models!.UsableCount} usable model(s)"),
        };

        var overall = !status.IsReachable ? "unavailable" : models.Status == "pass" ? "ready" : "degraded";
        var body = new ReadinessResponse(overall, new ReadinessChecks(new("pass", null), provider, models), status.CheckedAt);
        return TypedResults.Json(
            body,
            statusCode: overall == "ready" ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<ModelListResponse> GetModelsAsync(
        ModelCatalog catalog,
        IChatProvider provider,
        IOptions<ChatOptions> chatOptions,
        TimeProvider time,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        var defaultModel = string.IsNullOrWhiteSpace(chatOptions.Value.DefaultModel) ? null : chatOptions.Value.DefaultModel;
        var models = await catalog.GetModelsAsync(refresh, cancellationToken);
        var dtos = models.Select(m => new ModelDto(
                m.Name,
                m.DisplayName,
                m.Tag,
                m.Family,
                m.Digest,
                m.SizeBytes,
                m.ModifiedAt,
                m.ParameterSize,
                m.QuantizationLevel,
                m.IsRemote,
                catalog.IsUsable(m),
                IsDefault(m.Name, defaultModel)))
            .ToArray();
        return new ModelListResponse(provider.Name, dtos, defaultModel, time.GetUtcNow());
    }

    private static async Task<ModelStatusResponse> GetModelStatusAsync(
        ProviderStatusService statusService,
        InferenceMetrics metrics,
        IOptions<ChatOptions> chatOptions,
        CancellationToken cancellationToken)
    {
        var status = await statusService.GetStatusAsync(cancellationToken);
        return new ModelStatusResponse(
            ProviderStatusDto.From(status, chatOptions.Value.DefaultModel),
            metrics.Snapshot().LastSuccessAt);
    }

    private static async Task<SystemOverviewResponse> GetOverviewAsync(
        ProviderStatusService statusService,
        InferenceMetrics metrics,
        ApiRuntimeInfo runtime,
        IOptions<ChatOptions> chatOptions,
        IOptions<ApiOptions> apiOptions,
        IOptions<OllamaOptions> ollamaOptions,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var status = await statusService.GetStatusAsync(cancellationToken);
        var chat = chatOptions.Value;
        return new SystemOverviewResponse(
            new ApiInfoDto("healthy", runtime.Version, runtime.Environment, runtime.StartedAt, Math.Round(runtime.Uptime.TotalSeconds)),
            ProviderStatusDto.From(status, chat.DefaultModel),
            InferenceStatsDto.From(metrics.Snapshot()),
            new LimitsDto(
                chat.MaxMessageLength,
                chat.MaxConversationLength,
                chat.MaxMessages,
                chat.DefaultMaxTokens,
                chat.MaxTokensLimit,
                apiOptions.Value.MaxConcurrentGenerations,
                ollamaOptions.Value.InferenceTimeout.TotalSeconds,
                chat.AllowRemoteModels),
            time.GetUtcNow());
    }

    private static bool IsDefault(string name, string? defaultModel) =>
        defaultModel is not null
        && (string.Equals(name, defaultModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, defaultModel + ":latest", StringComparison.OrdinalIgnoreCase));
}
