using System.Runtime.CompilerServices;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;

namespace LocalAI.Tests.Shared;

/// <summary>Scriptable in-memory provider. No network, GPU or model download required.</summary>
public sealed class FakeChatProvider : IChatProvider
{
    public string Name => "fake";

    public List<ModelInfo> Models { get; } = [Model("tiny:1b")];

    public Exception? ListModelsException { get; set; }

    public ProviderHealth Health { get; set; } = new(true, "fake-1.0", TimeSpan.FromMilliseconds(2), null);

    public Func<ChatCompletionRequest, CancellationToken, IAsyncEnumerable<ChatStreamChunk>> StreamHandler { get; set; } =
        (_, ct) => Yield(ct, new ChatStreamChunk("Hello"), new ChatStreamChunk(", world"), Final());

    public int ListModelsCalls { get; private set; }

    public ChatCompletionRequest? LastRequest { get; private set; }

    /// <summary>Completes when a stream created by <see cref="HangAfter"/> observes cancellation.</summary>
    public TaskCompletionSource StreamCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(Health);

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        ListModelsCalls++;
        return ListModelsException is not null
            ? Task.FromException<IReadOnlyList<ModelInfo>>(ListModelsException)
            : Task.FromResult<IReadOnlyList<ModelInfo>>(Models.ToArray());
    }

    public IAsyncEnumerable<ChatStreamChunk> StreamChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return StreamHandler(request, cancellationToken);
    }

    public static ModelInfo Model(string name, bool remote = false) =>
        new(name, name.Split(':')[0], name.Contains(':') ? name.Split(':')[1] : null, "fam", "abc123", remote ? null : 1_000,
            DateTimeOffset.UnixEpoch, "1B", "Q4_K_M", remote);

    public static ChatStreamChunk Final(int completionTokens = 2) => new(
        string.Empty,
        IsFinal: true,
        FinishReason: "stop",
        Stats: new ChatCompletionStats(5, completionTokens, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

    public static async IAsyncEnumerable<ChatStreamChunk> Yield(
        [EnumeratorCancellation] CancellationToken cancellationToken, params ChatStreamChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }

    public static async IAsyncEnumerable<ChatStreamChunk> Throwing(
        Exception exception, [EnumeratorCancellation] CancellationToken cancellationToken, params ChatStreamChunk[] before)
    {
        foreach (var chunk in before)
        {
            await Task.Yield();
            yield return chunk;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw exception;
    }

    /// <summary>Yields <paramref name="first"/>, optionally waits for <paramref name="release"/>, then hangs until cancelled.</summary>
    public async IAsyncEnumerable<ChatStreamChunk> HangAfter(
        ChatStreamChunk first,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Task? release = null,
        params ChatStreamChunk[] afterRelease)
    {
        yield return first;
        if (release is not null)
        {
            await release.WaitAsync(cancellationToken);
            foreach (var chunk in afterRelease)
            {
                yield return chunk;
            }

            yield break;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            StreamCancelled.TrySetResult();
            throw;
        }
    }
}
