using System.Net;
using System.Text;
using System.Text.Json;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Providers.Documents;
using LocalAI.Providers.Ollama;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LocalAI.Providers.Tests;

public class OllamaEmbeddingTests
{
    [Fact]
    public async Task Embeds_a_batch_via_api_embed()
    {
        var (provider, handler) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Json("""{"model":"nomic-embed-text","embeddings":[[0.1,0.2],[0.3,0.4]]}""")));

        var vectors = await provider.EmbedAsync("nomic-embed-text", ["a", "b"], CancellationToken.None);

        Assert.Equal([0.3f, 0.4f], vectors[1]);
        Assert.Equal("/api/embed", handler.LastRequest!.RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(["a", "b"], sent.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.True(sent.RootElement.GetProperty("truncate").GetBoolean());
    }

    [Theory]
    [InlineData("""{"embeddings":[[0.1]]}""")]
    [InlineData("""{"embeddings":[[0.1],[]]}""")]
    [InlineData("""{}""")]
    public async Task Wrong_embedding_count_or_shape_is_an_invalid_response(string body)
    {
        var (provider, _) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Json(body)));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => provider.EmbedAsync("m", ["a", "b"], CancellationToken.None));

        Assert.Equal(InferenceErrorCode.ProviderInvalidResponse, ex.Code);
    }

    [Fact]
    public async Task Missing_embedding_model_maps_to_model_not_found()
    {
        var (provider, _) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Json("""{"error":"model \"nomic-embed-text\" not found"}""", HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => provider.EmbedAsync("nomic-embed-text", ["a"], CancellationToken.None));

        Assert.Equal(InferenceErrorCode.ModelNotFound, ex.Code);
    }

    [Theory]
    [InlineData("nomic-embed-text:latest", "nomic-bert", true)]
    [InlineData("mxbai-embed-large:latest", "bert", true)]
    [InlineData("all-minilm:latest", "bert", true)]
    [InlineData("snowflake-arctic-embed:s", null, true)]
    [InlineData("qwen2.5:7b", "qwen2", false)]
    public void Recognises_embedding_models(string name, string? family, bool expected) =>
        Assert.Equal(expected, OllamaProvider.IsEmbeddingModel(name, family));

    [Fact]
    public async Task Chat_requests_carry_the_configured_context_length()
    {
        const string body = "{\"message\":{\"content\":\"\"},\"done\":true}\n";
        var (provider, handler) = TestHttp.CreateProvider((_, _) => Task.FromResult(TestHttp.Ndjson(new SegmentedStream(TestHttp.Split(body, 100)))));

        await foreach (var _ in provider.StreamChatAsync(new ChatCompletionRequest("m", [new ChatMessage(ChatRole.User, "hi")], new GenerationOptions(null, 8)), CancellationToken.None))
        {
        }

        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(8_192, sent.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }
}

public class TextExtractorTests
{
    private static byte[] BuildPdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(50, 700), font);
        }

        return builder.Build();
    }

    [Fact]
    public async Task Pdf_text_is_extracted_per_page()
    {
        var segments = await new PdfTextExtractor().ExtractAsync(new MemoryStream(BuildPdf("Vacation policy is 25 days.", "Parking is in garage B.")), CancellationToken.None);

        Assert.Equal([1, 2], segments.Select(s => s.Page!.Value));
        Assert.Contains("25 days", segments[0].Text, StringComparison.Ordinal);
        Assert.Contains("garage B", segments[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_pdf_is_reported_as_unreadable()
    {
        var ex = await Assert.ThrowsAsync<DocumentException>(() =>
            new PdfTextExtractor().ExtractAsync(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 this is not really a pdf")), CancellationToken.None));

        Assert.Equal(DocumentErrorCode.Unreadable, ex.Code);
    }

    [Fact]
    public async Task Plain_text_is_decoded_as_utf8_with_bom_and_leaves_the_stream_open()
    {
        var stream = new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("# Notes\nမင်္ဂလာပါ")]);

        var segments = await new PlainTextExtractor().ExtractAsync(stream, CancellationToken.None);

        Assert.Equal("# Notes\nမင်္ဂလာပါ", Assert.Single(segments).Text);
        Assert.Null(segments[0].Page);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task Binary_data_is_not_accepted_as_text()
    {
        var ex = await Assert.ThrowsAsync<DocumentException>(() =>
            new PlainTextExtractor().ExtractAsync(new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]), CancellationToken.None));

        Assert.Equal(DocumentErrorCode.Unreadable, ex.Code);
    }

    [Theory]
    [InlineData(".pdf", true, false)]
    [InlineData(".txt", false, true)]
    [InlineData(".md", false, true)]
    [InlineData(".docx", false, false)]
    public void Extensions_are_routed_to_the_right_extractor(string extension, bool pdf, bool text)
    {
        Assert.Equal(pdf, new PdfTextExtractor().Supports(extension));
        Assert.Equal(text, new PlainTextExtractor().Supports(extension));
    }
}
