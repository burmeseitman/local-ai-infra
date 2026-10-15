using System.Text;
using LocalAI.Providers.Ollama;

namespace LocalAI.Providers.Tests;

public class NdjsonLineReaderTests
{
    private static async Task<List<string>> ReadAll(IEnumerable<byte[]> segments, int maxLineBytes = 1_024)
    {
        var reader = new NdjsonLineReader(new SegmentedStream(segments), maxLineBytes);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            lines.Add(Encoding.UTF8.GetString(line));
        }

        return lines;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4_096)]
    public async Task Reassembles_lines_regardless_of_read_boundaries(int segmentSize)
    {
        var lines = await ReadAll(TestHttp.Split("{\"a\":1}\n{\"b\":\"é\"}\n{\"c\":3}\n", segmentSize));

        Assert.Equal(["{\"a\":1}", "{\"b\":\"é\"}", "{\"c\":3}"], lines);
    }

    [Fact]
    public async Task Handles_crlf_blank_lines_and_missing_trailing_newline()
    {
        var lines = await ReadAll(TestHttp.Split("one\r\n\ntwo", 2));

        Assert.Equal(["one", "", "two"], lines);
    }

    [Fact]
    public async Task Rejects_lines_longer_than_the_limit()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(TestHttp.Split(new string('x', 100) + "\n", 10), maxLineBytes: 50));
    }
}
