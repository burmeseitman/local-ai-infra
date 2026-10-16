using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalAI.Core.Models;
using LocalAI.Tests.Shared;

namespace LocalAI.Api.Tests;

public class DocumentEndpointTests : IDisposable
{
    private const string Handbook =
        "Vacation policy. Employees receive 25 vacation days per year, plus public holidays.\n\n" +
        "Parking. Staff parking is available in garage B on weekdays.";

    private readonly ApiFactory _factory = new();

    public DocumentEndpointTests()
    {
        _factory.Provider.Models.Add(FakeChatProvider.Model("nomic-embed-text:latest") with { IsEmbedding = true, Family = "nomic-bert" });
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static MultipartFormDataContent File(string name, string text, string contentType = "text/plain")
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", name } };
    }

    private static async Task<JsonElement> WaitUntilIndexed(HttpClient client, Guid id)
    {
        for (var i = 0; i < 50; i++)
        {
            var doc = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{id}");
            if (doc.GetProperty("status").GetString() != "processing")
            {
                return doc;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Document was not indexed in time.");
    }

    private async Task<(HttpClient Client, Guid Id)> UploadHandbook()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/api/documents", File("Handbook.txt", Handbook));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("processing", created.GetProperty("status").GetString());
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/documents/{id}", response.Headers.Location!.OriginalString);
        return (client, id);
    }

    [Fact]
    public async Task Uploaded_text_is_indexed_in_the_background()
    {
        var (client, id) = await UploadHandbook();

        var indexed = await WaitUntilIndexed(client, id);

        Assert.Equal("ready", indexed.GetProperty("status").GetString());
        Assert.Equal(("Handbook", "Handbook.txt"), (indexed.GetProperty("title").GetString(), indexed.GetProperty("fileName").GetString()));
        Assert.True(indexed.GetProperty("chunkCount").GetInt32() >= 1);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/documents");
        Assert.True(list.GetProperty("embeddingModelInstalled").GetBoolean());
        Assert.Equal("nomic-embed-text", list.GetProperty("embeddingModel").GetString());
        Assert.Single(list.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task Chat_with_documents_streams_sources_and_grounds_the_prompt()
    {
        var (client, id) = await UploadHandbook();
        await WaitUntilIndexed(client, id);
        var conversation = (await (await client.PostAsJsonAsync("/api/conversations", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversation}/messages/stream")
            {
                Content = JsonContent.Create(new { model = "tiny:1b", content = "How many vacation days do employees get?", useDocuments = true }),
            },
            HttpCompletionOption.ResponseHeadersRead);
        var events = await Sse.ReadAll(response);

        Assert.Equal(["start", "sources", "delta", "delta", "done"], events.Select(e => e.Event));
        var source = events[1].Data.GetProperty("sources")[0];
        Assert.Equal((1, "Handbook"), (source.GetProperty("number").GetInt32(), source.GetProperty("title").GetString()));
        Assert.Contains("vacation", source.GetProperty("snippet").GetString(), StringComparison.OrdinalIgnoreCase);

        var system = _factory.Provider.LastRequest!.Messages[0];
        Assert.Equal(ChatRole.System, system.Role);
        Assert.Contains("25 vacation days", system.Content, StringComparison.Ordinal);

        var stored = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation}");
        Assert.Equal("Handbook", stored.GetProperty("messages")[1].GetProperty("sources")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Documents_are_private_to_their_owner()
    {
        var (admin, id) = await UploadHandbook();
        (await admin.PostAsJsonAsync("/api/users", new { username = "bob", password = "user-password-123" })).EnsureSuccessStatusCode();
        using var bob = await _factory.CreateAuthenticatedClientAsync("bob", "user-password-123");

        Assert.Empty((await bob.GetFromJsonAsync<JsonElement>("/api/documents")).GetProperty("documents").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}")).StatusCode);
    }

    [Fact]
    public async Task Upload_errors_are_mapped_to_problem_details()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var unsupported = await client.PostAsync("/api/documents", File("slides.pptx", "x"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal("unsupported_document_type", (await unsupported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var empty = await client.PostAsync("/api/documents", File("empty.txt", "   \n  "));
        Assert.Equal("document_has_no_text", (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var noFile = await client.PostAsync("/api/documents", new MultipartFormDataContent { { new StringContent("x"), "other" } });
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);

        // Above the regular 64 KB API body limit, below the 10 MB upload limit: allowed.
        var large = await client.PostAsync("/api/documents", File("big.txt", string.Join(' ', Enumerable.Repeat("lorem ipsum", 20_000))));
        Assert.Equal(HttpStatusCode.Accepted, large.StatusCode);

        // The raised limit applies only to the upload endpoint.
        var bigChat = await client.PostAsJsonAsync("/api/chat", Sse.ChatBody(content: new string('x', 70_000)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bigChat.StatusCode);
    }

    [Fact]
    public async Task Upload_reports_a_missing_embedding_model()
    {
        _factory.Provider.Models.RemoveAll(m => m.IsEmbedding);
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/documents", File("a.txt", "text"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("embedding_model_missing", problem.GetProperty("code").GetString());
        Assert.Contains("ollama pull nomic-embed-text", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/documents")).GetProperty("embeddingModelInstalled").GetBoolean());
    }

    [Fact]
    public async Task State_changing_requests_without_the_csrf_header_are_refused()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        client.DefaultRequestHeaders.Remove("X-LocalAI-Request");

        var upload = await client.PostAsync("/api/documents", File("a.txt", "text"));
        var create = await client.PostAsJsonAsync("/api/conversations", new { });

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal("missing_request_header", (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/documents")).StatusCode); // reads are unaffected
    }

    [Fact]
    public async Task Embedding_models_are_listed_but_cannot_be_used_for_chat()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var models = await client.GetFromJsonAsync<JsonElement>("/api/models");
        var embed = models.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "nomic-embed-text:latest");
        Assert.True(embed.GetProperty("isEmbedding").GetBoolean());
        Assert.False(embed.GetProperty("isAvailable").GetBoolean());

        var chat = await client.PostAsJsonAsync("/api/chat", Sse.ChatBody(model: "nomic-embed-text"));
        Assert.Equal("model_not_allowed", (await chat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
