using System.Text;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LocalAI.Providers.Documents;

/// <summary>Extracts text per page from PDFs (text layer only; scanned images are not OCR'd).</summary>
internal sealed class PdfTextExtractor : ITextExtractor
{
    public bool Supports(string extension) => extension == ".pdf";

    public Task<IReadOnlyList<TextSegment>> ExtractAsync(Stream content, CancellationToken cancellationToken)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            var pages = new List<TextSegment>(document.NumberOfPages);
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // ContentOrderTextExtractor keeps reading order and line breaks better than page.Text.
                pages.Add(new TextSegment(page.Number, ContentOrderTextExtractor.GetText(page)));
            }

            return Task.FromResult<IReadOnlyList<TextSegment>>(pages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentException(
                DocumentErrorCode.Unreadable,
                "The PDF could not be read. It may be damaged or password-protected.",
                ex);
        }
    }
}

/// <summary>Plain text and Markdown, decoded as UTF-8 (with BOM detection).</summary>
internal sealed class PlainTextExtractor : ITextExtractor
{
    public bool Supports(string extension) => extension is ".txt" or ".md" or ".markdown";

    public async Task<IReadOnlyList<TextSegment>> ExtractAsync(Stream content, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false), detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new DocumentException(DocumentErrorCode.Unreadable, "The file does not look like text.");
        }

        return [new TextSegment(null, text)];
    }
}
