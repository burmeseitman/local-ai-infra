using LocalAI.Core.Exceptions;
using LocalAI.Core.Services;
using LocalAI.Tests.Shared;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Tests;

public class ModelCatalogTests
{
    private readonly FakeChatProvider _provider = new();
    private readonly ManualTimeProvider _time = new();

    private ModelCatalog CreateCatalog(bool allowRemote = false, string? defaultModel = null) => new(
        _provider,
        Options.Create(new ChatOptions
        {
            AllowRemoteModels = allowRemote,
            DefaultModel = defaultModel,
            ModelListCacheDuration = TimeSpan.FromSeconds(5),
        }),
        _time);

    [Fact]
    public async Task Lists_local_models_before_remote_ones()
    {
        _provider.Models.Clear();
        _provider.Models.AddRange([FakeChatProvider.Model("zeta:cloud", remote: true), FakeChatProvider.Model("beta:1b"), FakeChatProvider.Model("alpha:1b")]);

        var models = await CreateCatalog().GetModelsAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal(["alpha:1b", "beta:1b", "zeta:cloud"], models.Select(m => m.Name));
    }

    [Fact]
    public async Task Caches_model_list_until_ttl_expires_or_refresh_is_forced()
    {
        using var catalog = CreateCatalog();

        await catalog.GetModelsAsync(false, CancellationToken.None);
        await catalog.GetModelsAsync(false, CancellationToken.None);
        Assert.Equal(1, _provider.ListModelsCalls);

        _time.Advance(TimeSpan.FromSeconds(6));
        await catalog.GetModelsAsync(false, CancellationToken.None);
        Assert.Equal(2, _provider.ListModelsCalls);

        await catalog.GetModelsAsync(true, CancellationToken.None);
        Assert.Equal(3, _provider.ListModelsCalls);
    }

    [Fact]
    public async Task Provider_unavailable_propagates_and_is_not_masked_by_cache()
    {
        using var catalog = CreateCatalog();
        await catalog.GetModelsAsync(false, CancellationToken.None);

        _provider.ListModelsException = InferenceException.Unavailable();
        var ex = await Assert.ThrowsAsync<InferenceException>(() => catalog.GetModelsAsync(true, CancellationToken.None));

        Assert.Equal(InferenceErrorCode.ProviderUnavailable, ex.Code);
        await Assert.ThrowsAsync<InferenceException>(() => catalog.GetModelsAsync(false, CancellationToken.None));
    }

    [Theory]
    [InlineData("tiny:1b", "tiny:1b")]
    [InlineData("TINY:1B", "tiny:1b")]
    [InlineData("llama", "llama:latest")]
    public async Task Resolves_requested_model_to_installed_name(string requested, string expected)
    {
        _provider.Models.Add(FakeChatProvider.Model("llama:latest"));

        var model = await CreateCatalog().ResolveAsync(requested, CancellationToken.None);

        Assert.Equal(expected, model.Name);
    }

    [Fact]
    public async Task Missing_model_is_reported_as_model_not_found()
    {
        var ex = await Assert.ThrowsAsync<InferenceException>(
            () => CreateCatalog().ResolveAsync("ghost:7b", CancellationToken.None));

        Assert.Equal(InferenceErrorCode.ModelNotFound, ex.Code);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Resolve_refreshes_a_stale_cache_before_reporting_not_found()
    {
        using var catalog = CreateCatalog();
        await catalog.GetModelsAsync(false, CancellationToken.None);
        _provider.Models.Add(FakeChatProvider.Model("fresh:1b"));

        var model = await catalog.ResolveAsync("fresh:1b", CancellationToken.None);

        Assert.Equal("fresh:1b", model.Name);
    }

    [Fact]
    public async Task Remote_models_are_refused_unless_allowed()
    {
        _provider.Models.Add(FakeChatProvider.Model("big:cloud", remote: true));

        var ex = await Assert.ThrowsAsync<InferenceException>(
            () => CreateCatalog().ResolveAsync("big:cloud", CancellationToken.None));
        Assert.Equal(InferenceErrorCode.ModelNotAllowed, ex.Code);

        var allowed = await CreateCatalog(allowRemote: true).ResolveAsync("big:cloud", CancellationToken.None);
        Assert.True(allowed.IsRemote);
    }

    [Fact]
    public void Summary_reports_default_model_installation_without_inventing_models()
    {
        var catalog = CreateCatalog(defaultModel: "tiny:1b");
        var summary = catalog.Summarize([FakeChatProvider.Model("tiny:1b"), FakeChatProvider.Model("x:cloud", remote: true)]);

        Assert.Equal(2, summary.InstalledCount);
        Assert.Equal(1, summary.UsableCount);
        Assert.True(summary.DefaultModelInstalled);

        var empty = catalog.Summarize([]);
        Assert.Equal(0, empty.InstalledCount);
        Assert.False(empty.DefaultModelInstalled);
        Assert.Equal("tiny:1b", empty.DefaultModel);
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
