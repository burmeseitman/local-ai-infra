using System.Net;
using System.Text.Json;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;

namespace LocalAI.Providers.Tests;

public class OllamaProviderTests
{
    private static readonly ChatCompletionRequest Request = new(
        "tiny:1b",
        [new ChatMessage(ChatRole.System, "Be brief"), new ChatMessage(ChatRole.User, "Hi")],
        new GenerationOptions(0.2, 64));

    private const string SuccessStream =
        "{\"model\":\"tiny:1b\",\"message\":{\"role\":\"assistant\",\"content\":\"Hel\"},\"done\":false}\n" +
        "{\"model\":\"tiny:1b\",\"message\":{\"role\":\"assistant\",\"content\":\"lo\"},\"done\":false}\n" +
        "{\"model\":\"tiny:1b\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"," +
        "\"total_duration\":3000000000,\"load_duration\":1000000000,\"prompt_eval_count\":12,\"eval_count\":4,\"eval_duration\":2000000000}\n";

    private static async Task<List<ChatStreamChunk>> Collect(IAsyncEnumerable<ChatStreamChunk> stream)
    {
        var list = new List<ChatStreamChunk>();
        await foreach (var chunk in stream)
        {
            list.Add(chunk);
        }

        return list;
    }

    [Fact]
    public async Task Lists_models_and_maps_metadata()
    {
        const string tags = """
        {"models":[
          {"name":"qwen2.5:0.5b","model":"qwen2.5:0.5b","modified_at":"2026-01-02T03:04:05Z","size":397821319,
           "digest":"a8b0c51577010a279d933d14c2a8ab4b268079d44c5c8830c0a93900f1827c67",
           "details":{"format":"gguf","family":"qwen2","parameter_size":"494.03M","quantization_level":"Q4_K_M"}},
          {"name":"glm-5:cloud","model":"glm-5:cloud","size":384,"remote_model":"glm-5","remote_host":"https://ollama.com:443",
           "details":{"family":"","parameter_size":""}}
        ]}
        """;
        var (provider, handler) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Json(tags)));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal("/api/tags", handler.LastRequest!.RequestUri!.AbsolutePath);
        var local = models[0];
        Assert.Equal(("qwen2.5:0.5b", "qwen2.5", "0.5b"), (local.Name, local.DisplayName, local.Tag));
        Assert.Equal(397821319, local.SizeBytes);
        Assert.Equal("a8b0c5157701", local.Digest);
        Assert.Equal(("qwen2", "494.03M", "Q4_K_M"), (local.Family, local.ParameterSize, local.QuantizationLevel));
        Assert.False(local.IsRemote);

        var remote = models[1];
        Assert.True(remote.IsRemote);
        Assert.Null(remote.SizeBytes);
        Assert.Null(remote.Family);
    }

    [Fact]
    public async Task Empty_model_list_is_returned_as_empty()
    {
        var (provider, _) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Json("{\"models\":[]}")));

        Assert.Empty(await provider.ListModelsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"models\":")]
    [InlineData("{}")]
    [InlineData("<html>proxy error</html>")]
    public async Task Malformed_model_list_is_an_invalid_response(string body)
    {
        var (provider, _) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Json(body)));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => provider.ListModelsAsync(CancellationToken.None));

        Assert.Equal(InferenceErrorCode.ProviderInvalidResponse, ex.Code);
    }

    [Fact]
    public async Task Unreachable_provider_is_reported_as_unavailable()
    {
        var (provider, _) = TestHttp.CreateProvider((_, _) => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => provider.ListModelsAsync(CancellationToken.None));
        Assert.Equal(InferenceErrorCode.ProviderUnavailable, ex.Code);
        Assert.DoesNotContain("refused", ex.Message, StringComparison.Ordinal);

        var health = await provider.CheckHealthAsync(CancellationToken.None);
        Assert.False(health.IsReachable);
        Assert.NotNull(health.Error);
    }

    [Fact]
    public async Task Health_check_reports_version()
    {
        var (provider, handler) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Json("{\"version\":\"0.40.4\"}")));

        var health = await provider.CheckHealthAsync(CancellationToken.None);

        Assert.True(health.IsReachable);
        Assert.Equal("0.40.4", health.Version);
        Assert.Equal("/api/version", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(64)]
    public async Task Streams_deltas_and_final_stats_across_arbitrary_network_framing(int segmentSize)
    {
        var (provider, handler) = TestHttp.CreateProvider(
            (_, _) => Task.FromResult(TestHttp.Ndjson(new SegmentedStream(TestHttp.Split(SuccessStream, segmentSize)))));

        var chunks = await Collect(provider.StreamChatAsync(Request, CancellationToken.None));

        Assert.Equal(["Hel", "lo", ""], chunks.Select(c => c.Content));
        var final = chunks[^1];
        Assert.True(final.IsFinal);
        Assert.Equal("stop", final.FinishReason);
        Assert.Equal(12, final.Stats!.PromptTokens);
        Assert.Equal(4, final.Stats.CompletionTokens);
        Assert.Equal(TimeSpan.FromSeconds(1), final.Stats.LoadDuration);
        Assert.Equal(2.0, final.Stats.TokensPerSecond);

        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("tiny:1b", sent.RootElement.GetProperty("model").GetString());
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(64, sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal("system", sent.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Malformed_stream_line_is_an_invalid_response_after_valid_deltas()
    {
        const string body = "{\"message\":{\"content\":\"ok\"},\"done\":false}\n{not json}\n";
        var (provider, _) = TestHttp.CreateProvider(
            (_, _) => Task.FromResult(TestHttp.Ndjson(new SegmentedStream(TestHttp.Split(body, 8)))));

        var received = new List<string>();
        var ex = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (var chunk in provider.StreamChatAsync(Request, CancellationToken.None))
            {
                received.Add(chunk.Content);
            }
        });

        Assert.Equal(InferenceErrorCode.ProviderInvalidResponse, ex.Code);
        Assert.Equal(["ok"], received);
    }

    [Fact]
    public async Task Oversized_stream_line_is_an_invalid_response()
    {
        var body = "{\"message\":{\"content\":\"" + new string('x', 10_000) + "\"},\"done\":false}\n";
        var (provider, _) = TestHttp.CreateProvider(
            (_, _) => Task.FromResult(TestHttp.Ndjson(new SegmentedStream(TestHttp.Split(body, 512)))));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => Collect(provider.StreamChatAsync(Request, CancellationToken.None)));

        Assert.Equal(InferenceErrorCode.ProviderInvalidResponse, ex.Code);
    }

    [Fact]
    public async Task Stream_ending_without_done_is_an_invalid_response()
    {
        var (provider, _) = TestHttp.CreateProvider(
            (_, _) => Task.FromResult(TestHttp.Ndjson(new SegmentedStream(TestHttp.Split("{\"message\":{\"content\":\"a\"},\"done\":false}\n", 16)))));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => Collect(provider.StreamChatAsync(Request, CancellationToken.None)));

        Assert.Equal(InferenceErrorCode.ProviderInvalidResponse, ex.Code);
    }

    [Fact]
    public async Task Error_message_inside_stream_is_mapped()
    {
        const string body = "{\"error\":\"llama runner process has terminated: signal: killed\"}\n";
        var (provider, _) = TestHttp.CreateProvider(
            (_, _) => Task.FromResult(TestHttp.Ndjson(new SegmentedStream(TestHttp.Split(body, 100)))));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => Collect(provider.StreamChatAsync(Request, CancellationToken.None)));

        Assert.Equal(InferenceErrorCode.ModelLoadFailed, ex.Code);
        Assert.Contains("signal: killed", ex.ProviderDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("signal", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":\"model \\\"tiny:1b\\\" not found, try pulling it first\"}", InferenceErrorCode.ModelNotFound)]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":\"model requires more system memory (5.5 GiB) than is available (2.1 GiB)\"}", InferenceErrorCode.ModelLoadFailed)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid options\"}", InferenceErrorCode.ProviderError)]
    [InlineData(HttpStatusCode.BadGateway, "upstream down", InferenceErrorCode.ProviderError)]
    public async Task Http_errors_are_normalized(HttpStatusCode status, string body, InferenceErrorCode expected)
    {
        var (provider, _) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Json(body, status)));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => Collect(provider.StreamChatAsync(Request, CancellationToken.None)));

        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task Inference_timeout_is_reported_as_timeout()
    {
        var options = TestHttp.Options(inferenceTimeout: TimeSpan.FromMilliseconds(200));
        var (provider, _) = TestHttp.CreateProvider(
            (_, _) => Task.FromResult(TestHttp.Ndjson(new SegmentedStream(
                TestHttp.Split("{\"message\":{\"content\":\"a\"},\"done\":false}\n", 100),
                ct => Task.Delay(Timeout.Infinite, ct)))),
            options);

        var ex = await Assert.ThrowsAsync<InferenceException>(() => Collect(provider.StreamChatAsync(Request, CancellationToken.None)));

        Assert.Equal(InferenceErrorCode.ProviderTimeout, ex.Code);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Metadata_timeout_is_reported_as_timeout()
    {
        var options = TestHttp.Options(metadataTimeout: TimeSpan.FromMilliseconds(100));
        var (provider, _) = TestHttp.CreateProvider(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
            options);

        var ex = await Assert.ThrowsAsync<InferenceException>(() => provider.ListModelsAsync(CancellationToken.None));

        Assert.Equal(InferenceErrorCode.ProviderTimeout, ex.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_timeout_or_failure()
    {
        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (provider, _) = TestHttp.CreateProvider(
            (_, _) => Task.FromResult(TestHttp.Ndjson(new SegmentedStream(
                TestHttp.Split("{\"message\":{\"content\":\"a\"},\"done\":false}\n", 100),
                async ct =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    finally
                    {
                        upstreamCancelled.TrySetResult();
                    }
                }))));
        using var cts = new CancellationTokenSource();

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(Request, cts.Token))
            {
                await cts.CancelAsync();
            }
        });

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
