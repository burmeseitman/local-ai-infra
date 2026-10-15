using System.Net;
using System.Text;
using LocalAI.Providers.Ollama;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalAI.Providers.Tests;

/// <summary>HTTP handler whose behaviour is a delegate; records the last request body.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }

    public HttpRequestMessage? LastRequest { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return await respond(request, cancellationToken);
    }
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("http://ollama.test/"), Timeout = Timeout.InfiniteTimeSpan };
}

/// <summary>Stream that returns pre-defined segments, one per read, to simulate arbitrary network framing.</summary>
internal sealed class SegmentedStream(IEnumerable<byte[]> segments, Func<CancellationToken, Task>? afterSegments = null) : Stream
{
    private readonly Queue<byte[]> _segments = new(segments);
    private byte[]? _current;
    private int _offset;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (_current is null || _offset == _current.Length)
        {
            if (!_segments.TryDequeue(out _current))
            {
                if (afterSegments is not null)
                {
                    await afterSegments(cancellationToken);
                }

                return 0;
            }

            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class TestHttp
{
    public static OllamaOptions Options(TimeSpan? inferenceTimeout = null, TimeSpan? metadataTimeout = null) => new()
    {
        BaseUrl = new Uri("http://ollama.test"),
        InferenceTimeout = inferenceTimeout ?? TimeSpan.FromSeconds(10),
        MetadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(10),
        MaxResponseLineBytes = 4_096,
    };

    public static (OllamaProvider Provider, StubHandler Handler) CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, OllamaOptions? options = null)
    {
        var handler = new StubHandler(respond);
        var wrapped = Microsoft.Extensions.Options.Options.Create(options ?? Options());
        var client = new OllamaClient(new StubHttpClientFactory(handler), wrapped, NullLogger<OllamaClient>.Instance);
        return (new OllamaProvider(client, wrapped), handler);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Ndjson(Stream body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { { "Content-Type", "application/x-ndjson" } } } };

    /// <summary>Splits text into segments of <paramref name="size"/> bytes, deliberately cutting JSON objects apart.</summary>
    public static IEnumerable<byte[]> Split(string text, int size) => Encoding.UTF8.GetBytes(text).Chunk(size);
}
