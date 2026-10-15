using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Core.Services;
using LocalAI.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Tests;

public class ChatServiceTests
{
    private readonly FakeChatProvider _provider = new();
    private readonly InferenceMetrics _metrics = new(TimeProvider.System);
    private readonly ChatService _service;

    public ChatServiceTests()
    {
        var catalog = new ModelCatalog(_provider, Options.Create(new ChatOptions()), TimeProvider.System);
        _service = new ChatService(_provider, catalog, _metrics, NullLogger<ChatService>.Instance);
    }

    private static ChatCompletionRequest Request(string model = "tiny:1b") =>
        new(model, [new ChatMessage(ChatRole.User, "Hi")], new GenerationOptions(null, 16));

    [Fact]
    public async Task Streams_provider_chunks_in_order_and_records_success()
    {
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in _service.StreamAsync(Request(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(["Hello", ", world", ""], chunks.Select(c => c.Content));
        Assert.True(chunks[^1].IsFinal);

        var snapshot = _metrics.Snapshot();
        Assert.Equal(1, snapshot.Succeeded);
        Assert.Equal(0, snapshot.ActiveGenerations);
        Assert.NotNull(snapshot.LastSuccessAt);
        Assert.Equal(2, snapshot.LastCompletionStats!.CompletionTokens);
        Assert.Equal(2.0, snapshot.LastCompletionStats.TokensPerSecond);
    }

    [Fact]
    public async Task Complete_aggregates_the_stream()
    {
        var result = await _service.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal("Hello, world", result.Content);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public async Task Prepare_resolves_canonical_model_name()
    {
        _provider.Models.Add(FakeChatProvider.Model("llama:latest"));

        var prepared = await _service.PrepareAsync(Request("llama"), CancellationToken.None);

        Assert.Equal("llama:latest", prepared.Model);
        Assert.Equal(1, _metrics.Snapshot().TotalRequests);
    }

    [Fact]
    public async Task Prepare_records_failure_for_unknown_model()
    {
        await Assert.ThrowsAsync<InferenceException>(() => _service.PrepareAsync(Request("ghost:1b"), CancellationToken.None));

        var snapshot = _metrics.Snapshot();
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal("model_not_found", snapshot.RecentFailures[0].Code);
    }

    [Fact]
    public async Task Provider_failure_mid_stream_is_propagated_and_recorded()
    {
        _provider.StreamHandler = (_, ct) => FakeChatProvider.Throwing(
            InferenceException.InvalidResponse("garbage"), ct, new ChatStreamChunk("partial"));

        var received = new List<string>();
        var ex = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (var chunk in _service.StreamAsync(Request(), CancellationToken.None))
            {
                received.Add(chunk.Content);
            }
        });

        Assert.Equal(InferenceErrorCode.ProviderInvalidResponse, ex.Code);
        Assert.Equal(["partial"], received);
        Assert.Equal("provider_invalid_response", _metrics.Snapshot().RecentFailures.Single().Code);
        Assert.Equal(0, _metrics.Snapshot().ActiveGenerations);
    }

    [Fact]
    public async Task Stream_that_ends_without_final_chunk_is_a_protocol_error()
    {
        _provider.StreamHandler = (_, ct) => FakeChatProvider.Yield(ct, new ChatStreamChunk("only"));

        var ex = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (var _ in _service.StreamAsync(Request(), CancellationToken.None))
            {
            }
        });

        Assert.Equal(InferenceErrorCode.ProviderInvalidResponse, ex.Code);
        Assert.Equal(1, _metrics.Snapshot().Failed);
    }

    [Fact]
    public async Task Cancellation_is_propagated_and_counted_as_cancelled_not_failed()
    {
        _provider.StreamHandler = (_, ct) => _provider.HangAfter(new ChatStreamChunk("first"), ct);
        using var cts = new CancellationTokenSource();

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var chunk in _service.StreamAsync(Request(), cts.Token))
            {
                Assert.Equal(1, _metrics.Snapshot().ActiveGenerations);
                await cts.CancelAsync();
            }
        });

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        await _provider.StreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = _metrics.Snapshot();
        Assert.Equal(1, snapshot.Cancelled);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.ActiveGenerations);
    }

    [Fact]
    public async Task Consumer_stopping_early_counts_as_cancelled()
    {
        await foreach (var _ in _service.StreamAsync(Request(), CancellationToken.None))
        {
            break;
        }

        Assert.Equal(1, _metrics.Snapshot().Cancelled);
        Assert.Equal(0, _metrics.Snapshot().ActiveGenerations);
    }
}
