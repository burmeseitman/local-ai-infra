using System.Buffers;

namespace LocalAI.Providers.Ollama;

/// <summary>
/// Reads newline-delimited records from a stream without assuming that a network read
/// aligns with record boundaries. Lines longer than the configured limit are rejected
/// instead of growing memory without bound.
/// </summary>
internal sealed class NdjsonLineReader(Stream stream, int maxLineBytes)
{
    private readonly byte[] _buffer = new byte[8 * 1024];
    private readonly ArrayBufferWriter<byte> _pending = new();
    private int _start;
    private int _end;
    private bool _endOfStream;

    /// <summary>Returns the next line without its terminator, or null at end of stream.</summary>
    /// <exception cref="InvalidDataException">A line exceeded the size limit.</exception>
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                Append(_start, newline - _start);
                _start = newline + 1;
                return TakePending();
            }

            Append(_start, _end - _start);
            _start = _end = 0;

            if (_endOfStream)
            {
                return _pending.WrittenCount > 0 ? TakePending() : null;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                _endOfStream = true;
            }
            else
            {
                _end = read;
            }
        }
    }

    private void Append(int offset, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (_pending.WrittenCount + count > maxLineBytes)
        {
            throw new InvalidDataException($"Response line exceeded {maxLineBytes} bytes.");
        }

        _pending.Write(_buffer.AsSpan(offset, count));
    }

    private byte[] TakePending()
    {
        var line = _pending.WrittenSpan.ToArray();
        _pending.ResetWrittenCount();
        return line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;
    }
}
