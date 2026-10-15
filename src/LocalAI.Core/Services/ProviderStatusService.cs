using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalAI.Core.Services;

/// <summary>
/// Combines a connectivity probe with model discovery. Never runs inference, so it is
/// cheap enough for health checks and dashboard polling.
/// </summary>
public sealed class ProviderStatusService(
    IChatProvider provider,
    ModelCatalog catalog,
    TimeProvider timeProvider,
    ILogger<ProviderStatusService> logger)
{
    private long _lastSuccessTicks;

    public DateTimeOffset? LastSuccessfulCheckAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSuccessTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public async Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var health = await provider.CheckHealthAsync(cancellationToken);
        var checkedAt = timeProvider.GetUtcNow();
        if (!health.IsReachable)
        {
            logger.LogWarning("Provider {Provider} is not reachable: {Error}", provider.Name, health.Error);
            return new ProviderStatus(provider.Name, false, null, null, health.Error, checkedAt, LastSuccessfulCheckAt, null);
        }

        ModelAvailability? models = null;
        string? error = null;
        try
        {
            models = catalog.Summarize(await catalog.GetModelsAsync(forceRefresh: true, cancellationToken));
        }
        catch (InferenceException ex)
        {
            logger.LogWarning("Provider {Provider} model listing failed: {Code} ({Detail})", provider.Name, ex.Code, ex.ProviderDetail);
            error = ex.Message;
        }

        if (error is null)
        {
            Interlocked.Exchange(ref _lastSuccessTicks, checkedAt.UtcTicks);
        }

        return new ProviderStatus(
            provider.Name, true, health.Version, health.Latency, error, checkedAt, LastSuccessfulCheckAt, models);
    }
}
