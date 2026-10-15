using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Tests.Shared;

namespace LocalAI.Api.Tests;

public class ChatEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Stream_emits_start_deltas_and_done_as_server_sent_events()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.SendAsync(Sse.StreamRequest(Sse.ChatBody()), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        var events = await Sse.ReadAll(response);

        Assert.Equal(["start", "delta", "delta", "done"], events.Select(e => e.Event));
        Assert.Equal("tiny:1b", events[0].Data.GetProperty("model").GetString());
        Assert.Equal("Hello, world", string.Concat(events.Where(e => e.Event == "delta").Select(e => e.Data.GetProperty("content").GetString())));
        var done = events[^1].Data;
        Assert.Equal("stop", done.GetProperty("finishReason").GetString());
        Assert.Equal(2, done.GetProperty("stats").GetProperty("completionTokens").GetInt32());
        Assert.Equal(2.0, done.GetProperty("stats").GetProperty("tokensPerSecond").GetDouble());
    }

    [Fact]
    public async Task Stream_delivers_deltas_before_generation_finishes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Provider.StreamHandler = (_, ct) => _factory.Provider.HangAfter(
            new ChatStreamChunk("first"), ct, release.Task, new ChatStreamChunk(" second"), FakeChatProvider.Final());
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.SendAsync(Sse.StreamRequest(Sse.ChatBody()), HttpCompletionOption.ResponseHeadersRead);

        var received = new List<SseEvent>();
        await foreach (var e in Sse.ReadEvents(response).WithCancellation(CancellationToken.None))
        {
            received.Add(e);
            if (e.Event == "delta" && received.Count(x => x.Event == "delta") == 1)
            {
                // The provider is still blocked here: proof that the API is not buffering.
                Assert.False(release.Task.IsCompleted);
                Assert.Equal("first", e.Data.GetProperty("content").GetString());
                release.SetResult();
            }
        }

        Assert.Equal(["start", "delta", "delta", "done"], received.Select(e => e.Event));
    }

    [Fact]
    public async Task Provider_failure_mid_stream_becomes_structured_error_event_after_partial_text()
    {
        _factory.Provider.StreamHandler = (_, ct) => FakeChatProvider.Throwing(
            InferenceException.Timeout(TimeSpan.FromSeconds(30)), ct, new ChatStreamChunk("partial"));
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.SendAsync(Sse.StreamRequest(Sse.ChatBody()), HttpCompletionOption.ResponseHeadersRead);

        var events = await Sse.ReadAll(response);

        Assert.Equal(["start", "delta", "error"], events.Select(e => e.Event));
        var error = events[^1].Data;
        Assert.Equal("provider_timeout", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Unexpected_exception_mid_stream_does_not_leak_internal_details()
    {
        _factory.Provider.StreamHandler = (_, ct) => FakeChatProvider.Throwing(
            new InvalidOperationException("secret connection string xyz"), ct);
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.SendAsync(Sse.StreamRequest(Sse.ChatBody()), HttpCompletionOption.ResponseHeadersRead);

        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: error", raw, StringComparison.Ordinal);
        Assert.Contains("internal_error", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_disconnect_cancels_the_provider_stream_and_is_counted_as_cancelled()
    {
        _factory.Provider.StreamHandler = (_, ct) => _factory.Provider.HangAfter(new ChatStreamChunk("tick"), ct);
        using var client = await _factory.CreateAuthenticatedClientAsync();

        using (var cts = new CancellationTokenSource())
        {
            using var response = await client.SendAsync(
                Sse.StreamRequest(Sse.ChatBody()), HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await foreach (var e in Sse.ReadEvents(response))
            {
                if (e.Event == "delta")
                {
                    break; // the user pressed "Stop"
                }
            }

            await cts.CancelAsync();
        }

        await _factory.Provider.StreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var overview = await WaitForAsync(
            client,
            o => o.GetProperty("inference").GetProperty("cancelled").GetInt64() == 1);
        var inference = overview.GetProperty("inference");
        Assert.Equal(0, inference.GetProperty("failed").GetInt64());
        Assert.Equal(0, inference.GetProperty("activeGenerations").GetInt32());
    }

    [Fact]
    public async Task Validation_errors_return_problem_details_before_streaming()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.SendAsync(Sse.StreamRequest(new { model = "tiny:1b", messages = new[] { new { role = "user", content = "   " } } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("messages[0].content", out _));
        Assert.Null(_factory.Provider.LastRequest);
    }

    [Fact]
    public async Task Overlong_prompt_is_rejected()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.PostAsJsonAsync("/api/chat", Sse.ChatBody(content: new string('x', 8_001)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_model_returns_404_problem_and_never_reaches_provider()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.SendAsync(Sse.StreamRequest(Sse.ChatBody(model: "ghost:70b")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("model_not_found", problem.GetProperty("code").GetString());
        Assert.False(problem.GetProperty("retryable").GetBoolean());
        Assert.Null(_factory.Provider.LastRequest);
    }

    [Fact]
    public async Task Provider_down_returns_503_problem()
    {
        _factory.Provider.ListModelsException = InferenceException.Unavailable(new HttpRequestException("refused"));
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.SendAsync(Sse.StreamRequest(Sse.ChatBody()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("provider_unavailable", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain("refused", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_returns_problem_details()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.PostAsync("/api/chat", new StringContent("{oops", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Oversized_body_returns_413()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.PostAsync(
            "/api/chat",
            new StringContent($"{{\"model\":\"tiny:1b\",\"messages\":[{{\"role\":\"user\",\"content\":\"{new string('x', 70_000)}\"}}]}}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Non_streaming_endpoint_returns_full_reply()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.PostAsJsonAsync("/api/chat", Sse.ChatBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Hello, world", body.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("assistant", body.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal(1_024, _factory.Provider.LastRequest!.Options.MaxTokens);
    }

    [Fact]
    public async Task Non_streaming_endpoint_maps_provider_errors_to_status_codes()
    {
        _factory.Provider.StreamHandler = (_, ct) => FakeChatProvider.Throwing(
            new InferenceException(InferenceErrorCode.ModelLoadFailed, "The model could not be loaded."), ct);
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using var response = await client.PostAsJsonAsync("/api/chat", Sse.ChatBody());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("model_load_failed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Excess_concurrent_generations_are_rejected_with_429()
    {
        using var factory = new ApiFactory { MaxConcurrentGenerations = 1 };
        factory.Provider.StreamHandler = (_, ct) => factory.Provider.HangAfter(new ChatStreamChunk("busy"), ct);
        using var client = await factory.CreateAuthenticatedClientAsync();

        using var cts = new CancellationTokenSource();
        using var first = await client.SendAsync(Sse.StreamRequest(Sse.ChatBody()), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var second = await client.SendAsync(Sse.StreamRequest(Sse.ChatBody()));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("too_many_generations", problem.GetProperty("code").GetString());
        await cts.CancelAsync();
    }

    private static async Task<JsonElement> WaitForAsync(HttpClient client, Func<JsonElement, bool> condition)
    {
        JsonElement overview = default;
        for (var i = 0; i < 50; i++)
        {
            overview = await client.GetFromJsonAsync<JsonElement>("/api/system/overview");
            if (condition(overview))
            {
                return overview;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Condition not met. Last overview: {overview}");
        return overview;
    }
}
