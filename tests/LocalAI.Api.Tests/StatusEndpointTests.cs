using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Tests.Shared;

namespace LocalAI.Api.Tests;

public class StatusEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Health_reports_liveness_without_touching_the_provider()
    {
        _factory.Provider.Health = new ProviderHealth(false, null, TimeSpan.Zero, "down");
        using var client = _factory.CreateClient(); // anonymous: probes must not need a session

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Readiness_is_ready_when_provider_and_model_are_available()
    {
        using var client = _factory.CreateClient(); // anonymous: probes must not need a session

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Equal("pass", body.GetProperty("checks").GetProperty("models").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_is_degraded_when_no_model_is_installed()
    {
        _factory.Provider.Models.Clear();
        using var client = _factory.CreateClient(); // anonymous: probes must not need a session

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("pass", body.GetProperty("checks").GetProperty("provider").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_is_unavailable_when_provider_is_unreachable()
    {
        _factory.Provider.Health = new ProviderHealth(false, null, TimeSpan.Zero, "The local inference service is not reachable.");
        using var client = _factory.CreateClient(); // anonymous: probes must not need a session

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unavailable", body.GetProperty("status").GetString());
        Assert.Equal("fail", body.GetProperty("checks").GetProperty("provider").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Models_endpoint_returns_installed_models_with_availability_flags()
    {
        _factory.Provider.Models.Add(FakeChatProvider.Model("big:cloud", remote: true));
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/models?refresh=true");

        Assert.Equal("fake", body.GetProperty("provider").GetString());
        Assert.Equal("tiny:1b", body.GetProperty("defaultModel").GetString());
        var models = body.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(2, models.Length);
        Assert.True(models[0].GetProperty("isAvailable").GetBoolean());
        Assert.True(models[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal(1_000, models[0].GetProperty("sizeBytes").GetInt64());
        Assert.True(models[1].GetProperty("isRemote").GetBoolean());
        Assert.False(models[1].GetProperty("isAvailable").GetBoolean());
    }

    [Fact]
    public async Task Models_endpoint_returns_empty_list_when_nothing_is_installed()
    {
        _factory.Provider.Models.Clear();
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/models");

        Assert.Empty(body.GetProperty("models").EnumerateArray());
    }

    [Fact]
    public async Task Models_endpoint_returns_503_problem_when_provider_is_down()
    {
        _factory.Provider.ListModelsException = InferenceException.Unavailable();
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal("provider_unavailable", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Unexpected_errors_return_generic_500_problem_without_internals()
    {
        _factory.Provider.ListModelsException = new InvalidOperationException("stack secret");
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("internal_error", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("stack secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_status_distinguishes_connectivity_from_inference()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/models/status");
        Assert.True(before.GetProperty("provider").GetProperty("reachable").GetBoolean());
        Assert.True(before.GetProperty("provider").GetProperty("defaultModelInstalled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lastSuccessfulInferenceAt").ValueKind);

        (await client.PostAsJsonAsync("/api/chat", Sse.ChatBody())).EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<JsonElement>("/api/models/status");
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("lastSuccessfulInferenceAt").ValueKind);
    }

    [Fact]
    public async Task System_overview_reports_real_counters_and_limits()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        (await client.PostAsJsonAsync("/api/chat", Sse.ChatBody())).EnsureSuccessStatusCode();
        await client.PostAsJsonAsync("/api/chat", Sse.ChatBody(model: "ghost:1b"));

        var overview = await client.GetFromJsonAsync<JsonElement>("/api/system/overview");

        var inference = overview.GetProperty("inference");
        Assert.Equal(2, inference.GetProperty("totalRequests").GetInt64());
        Assert.Equal(1, inference.GetProperty("succeeded").GetInt64());
        Assert.Equal(1, inference.GetProperty("failed").GetInt64());
        Assert.Equal("model_not_found", inference.GetProperty("recentFailures")[0].GetProperty("code").GetString());
        Assert.Equal("tiny:1b", inference.GetProperty("lastCompletionModel").GetString());
        Assert.Equal(8_000, overview.GetProperty("limits").GetProperty("maxMessageLength").GetInt32());
        Assert.Equal("Testing", overview.GetProperty("api").GetProperty("environment").GetString());
        Assert.NotEqual(JsonValueKind.Null, overview.GetProperty("provider").GetProperty("lastSuccessfulCheckAt").ValueKind);
    }

    [Fact]
    public async Task Swagger_is_disabled_unless_configured()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
