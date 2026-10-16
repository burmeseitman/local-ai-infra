using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Services;

/// <summary>
/// Model discovery with a short-lived cache, plus the policy that decides whether a
/// requested model may be used for inference.
/// </summary>
public sealed class ModelCatalog(
    IChatProvider provider,
    IOptions<ChatOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<ModelInfo>? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var ttl = options.Value.ModelListCacheDuration;
        if (!forceRefresh && TryGetFresh(ttl) is { } fresh)
        {
            return fresh;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && TryGetFresh(ttl) is { } refreshed)
            {
                return refreshed;
            }

            var models = await provider.ListModelsAsync(cancellationToken);
            var ordered = models.OrderBy(m => m.IsRemote).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            _cached = ordered;
            _cachedAt = timeProvider.GetUtcNow();
            return ordered;
        }
        catch
        {
            // Never serve a stale list after the provider failed: callers must see the outage.
            _cached = null;
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Whether the model may be used for chat (embedding-only and disallowed remote models may not).</summary>
    public bool IsUsable(ModelInfo model) => !model.IsEmbedding && (!model.IsRemote || options.Value.AllowRemoteModels);

    /// <summary>Returns the installed, policy-permitted model matching <paramref name="requested"/>.</summary>
    /// <exception cref="InferenceException">The model is missing, not allowed, or the provider is down.</exception>
    public async Task<ModelInfo> ResolveAsync(string requested, CancellationToken cancellationToken, bool allowEmbedding = false)
    {
        // Always re-check against the provider for inference so a just-deleted model is caught.
        var models = await GetModelsAsync(forceRefresh: false, cancellationToken);
        var match = Find(models, requested)
            ?? Find(await GetModelsAsync(forceRefresh: true, cancellationToken), requested)
            ?? throw InferenceException.ModelNotFound(requested);

        if (allowEmbedding && match.IsEmbedding && !match.IsRemote)
        {
            return match;
        }

        if (match.IsEmbedding)
        {
            throw new InferenceException(
                InferenceErrorCode.ModelNotAllowed,
                $"Model '{match.Name}' is an embedding model and cannot be used for chat.");
        }

        if (!IsUsable(match))
        {
            throw new InferenceException(
                InferenceErrorCode.ModelNotAllowed,
                $"Model '{match.Name}' runs on a remote host and remote models are disabled by configuration.");
        }

        return match;
    }

    public ModelAvailability Summarize(IReadOnlyList<ModelInfo> models)
    {
        var defaultModel = string.IsNullOrWhiteSpace(options.Value.DefaultModel) ? null : options.Value.DefaultModel;
        var defaultInstalled = defaultModel is not null && Find(models, defaultModel) is { } m && IsUsable(m);
        return new ModelAvailability(models.Count, models.Count(IsUsable), defaultModel, defaultInstalled);
    }

    public void Dispose() => _refreshLock.Dispose();

    private IReadOnlyList<ModelInfo>? TryGetFresh(TimeSpan ttl) =>
        _cached is not null && timeProvider.GetUtcNow() - _cachedAt < ttl ? _cached : null;

    /// <summary>Exact match, or "name" matching "name:latest" (provider convention).</summary>
    private static ModelInfo? Find(IReadOnlyList<ModelInfo> models, string requested) =>
        models.FirstOrDefault(m => string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase))
        ?? (requested.Contains(':', StringComparison.Ordinal)
            ? null
            : models.FirstOrDefault(m => string.Equals(m.Name, requested + ":latest", StringComparison.OrdinalIgnoreCase)));
}
