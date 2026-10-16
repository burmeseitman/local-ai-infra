using System.Text;
using System.Text.RegularExpressions;
using LocalAI.Core.Models;

namespace LocalAI.Core.Services;

/// <summary>
/// Splits extracted text into overlapping chunks of roughly <c>size</c> characters, preferring
/// paragraph, then sentence, then word boundaries. Each chunk records the page where it starts.
/// </summary>
public static partial class TextChunker
{
    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    /// <summary>Collapses runs of spaces and blank lines; keeps paragraph breaks.</summary>
    public static string Normalize(string text)
    {
        var unified = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\0', ' ');
        var lines = unified.Split('\n').Select(l => HorizontalWhitespace().Replace(l, " ").Trim());
        return ExtraBlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    public static IReadOnlyList<TextChunk> Chunk(IReadOnlyList<TextSegment> segments, int size, int overlap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(overlap);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlap, size);

        // Flatten while remembering which page each character offset came from.
        var text = new StringBuilder();
        var pageStarts = new List<(int Offset, int? Page)>();
        foreach (var segment in segments)
        {
            var normalized = Normalize(segment.Text);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            pageStarts.Add((text.Length, segment.Page));
            text.Append(normalized);
        }

        var all = text.ToString();
        var chunks = new List<TextChunk>();
        var start = 0;
        while (start < all.Length)
        {
            var end = Math.Min(start + size, all.Length);
            if (end < all.Length)
            {
                end = FindBreak(all, start, end);
            }

            var piece = all[start..end].Trim();
            if (piece.Length > 0)
            {
                chunks.Add(new TextChunk(chunks.Count, PageAt(pageStarts, start), piece));
            }

            if (end >= all.Length)
            {
                break;
            }

            // Step back by the overlap, but always make progress, and start on a word boundary.
            var next = Math.Max(end - overlap, start + 1);
            while (next < end && !char.IsWhiteSpace(all[next - 1]))
            {
                next++;
            }

            start = next;
        }

        return chunks;
    }

    /// <summary>Best break at or before <paramref name="end"/>, but not in the first half of the window.</summary>
    private static int FindBreak(string text, int start, int end)
    {
        var minimum = start + ((end - start) / 2);
        foreach (var separator in new[] { "\n\n", "\n", ". ", "? ", "! ", "; ", ", ", " " })
        {
            var index = text.LastIndexOf(separator, end - 1, end - minimum, StringComparison.Ordinal);
            if (index >= minimum)
            {
                return index + separator.Length;
            }
        }

        return end;
    }

    private static int? PageAt(List<(int Offset, int? Page)> pageStarts, int offset)
    {
        int? page = null;
        foreach (var (start, p) in pageStarts)
        {
            if (start > offset)
            {
                break;
            }

            page = p;
        }

        return page;
    }
}
