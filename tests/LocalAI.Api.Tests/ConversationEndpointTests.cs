using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LocalAI.Api.Tests;

public class ConversationEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public ConversationEndpointTests()
    {
        _client = _factory.CreateAuthenticatedClientAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> CreateConversation(string? title = null)
    {
        var response = await _client.PostAsJsonAsync("/api/conversations", new { title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"/api/conversations/{body.GetProperty("id").GetGuid()}", response.Headers.Location!.OriginalString);
        return body.GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage Send(Guid id, string content, string model = "tiny:1b") =>
        new(HttpMethod.Post, $"/api/conversations/{id}/messages/stream") { Content = JsonContent.Create(new { model, content }) };

    private Task<JsonElement> Get(Guid id) => _client.GetFromJsonAsync<JsonElement>($"/api/conversations/{id}");

    [Fact]
    public async Task Send_streams_reply_and_persists_both_messages()
    {
        var id = await CreateConversation();

        using var response = await _client.SendAsync(Send(id, "What is SSE?"), HttpCompletionOption.ResponseHeadersRead);
        var events = await Sse.ReadAll(response);

        Assert.Equal(["start", "delta", "delta", "done"], events.Select(e => e.Event));
        Assert.Equal(id, events[0].Data.GetProperty("conversationId").GetGuid());

        var conversation = await Get(id);
        Assert.Equal("What is SSE?", conversation.GetProperty("title").GetString());
        var messages = conversation.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(("user", "What is SSE?", "complete"), (messages[0].GetProperty("role").GetString(), messages[0].GetProperty("content").GetString(), messages[0].GetProperty("status").GetString()));
        Assert.Equal(("assistant", "Hello, world", "complete"), (messages[1].GetProperty("role").GetString(), messages[1].GetProperty("content").GetString(), messages[1].GetProperty("status").GetString()));
        Assert.Equal(2, messages[1].GetProperty("stats").GetProperty("completionTokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Second_turn_sends_stored_history_to_the_provider()
    {
        var id = await CreateConversation();
        using (var first = await _client.SendAsync(Send(id, "first"), HttpCompletionOption.ResponseHeadersRead))
        {
            await Sse.ReadAll(first);
        }

        using var second = await _client.SendAsync(Send(id, "second"), HttpCompletionOption.ResponseHeadersRead);
        await Sse.ReadAll(second);

        Assert.Equal(
            [(ChatRole.User, "first"), (ChatRole.Assistant, "Hello, world"), (ChatRole.User, "second")],
            _factory.Provider.LastRequest!.Messages.Select(m => (m.Role, m.Content)));
    }

    [Fact]
    public async Task List_returns_conversations_newest_first_and_rename_and_delete_work()
    {
        var a = await CreateConversation("Alpha");
        var b = await CreateConversation("Beta");

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/conversations");
        Assert.Equal([b, a], list.GetProperty("conversations").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));

        var rename = await _client.PatchAsJsonAsync($"/api/conversations/{a}", new { title = "  Renamed  " });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        Assert.Equal("Renamed", (await Get(a)).GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/conversations/{a}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/conversations/{a}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/conversations/{a}")).StatusCode);
    }

    [Fact]
    public async Task Unknown_conversation_returns_404_problem()
    {
        using var response = await _client.SendAsync(Send(Guid.NewGuid(), "hello"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conversation_not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_message_and_title_return_validation_problems()
    {
        var id = await CreateConversation();

        using var empty = await _client.SendAsync(Send(id, "   "));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var problem = await empty.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("content", out _));

        var badTitle = await _client.PatchAsJsonAsync($"/api/conversations/{id}", new { title = new string('x', 121) });
        Assert.Equal(HttpStatusCode.BadRequest, badTitle.StatusCode);

        Assert.Empty((await Get(id)).GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Unknown_model_returns_404_and_stores_nothing()
    {
        var id = await CreateConversation();

        using var response = await _client.SendAsync(Send(id, "hi", model: "ghost:1b"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await Get(id)).GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Provider_failure_is_streamed_and_persisted_with_partial_text()
    {
        _factory.Provider.StreamHandler = (_, ct) => FakeChatProvider.Throwing(
            InferenceException.Timeout(TimeSpan.FromSeconds(30)), ct, new ChatStreamChunk("partial"));
        var id = await CreateConversation();

        using var response = await _client.SendAsync(Send(id, "go"), HttpCompletionOption.ResponseHeadersRead);
        var events = await Sse.ReadAll(response);

        Assert.Equal("error", events[^1].Event);
        var reply = (await Get(id)).GetProperty("messages")[1];
        Assert.Equal(("error", "partial"), (reply.GetProperty("status").GetString(), reply.GetProperty("content").GetString()));
        Assert.Equal("provider_timeout", reply.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Client_disconnect_persists_reply_as_stopped()
    {
        _factory.Provider.StreamHandler = (_, ct) => _factory.Provider.HangAfter(new ChatStreamChunk("half"), ct);
        var id = await CreateConversation();

        using (var cts = new CancellationTokenSource())
        {
            using var response = await _client.SendAsync(Send(id, "long"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await foreach (var e in Sse.ReadEvents(response))
            {
                if (e.Event == "delta")
                {
                    break;
                }
            }

            await cts.CancelAsync();
        }

        await _factory.Provider.StreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        JsonElement reply = default;
        for (var i = 0; i < 50; i++)
        {
            var messages = (await Get(id)).GetProperty("messages");
            if (messages.GetArrayLength() == 2)
            {
                reply = messages[1];
                break;
            }

            await Task.Delay(100);
        }

        Assert.Equal(JsonValueKind.Object, reply.ValueKind);
        Assert.Equal(("stopped", "half"), (reply.GetProperty("status").GetString(), reply.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task Concurrent_send_to_the_same_conversation_returns_409()
    {
        _factory.Provider.StreamHandler = (_, ct) => _factory.Provider.HangAfter(new ChatStreamChunk("busy"), ct);
        var id = await CreateConversation();

        using var cts = new CancellationTokenSource();
        using var first = await _client.SendAsync(Send(id, "one"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var second = await _client.SendAsync(Send(id, "two"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conversation_busy", problem.GetProperty("code").GetString());
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Regenerate_replaces_the_last_reply()
    {
        var id = await CreateConversation();
        using (var first = await _client.SendAsync(Send(id, "question"), HttpCompletionOption.ResponseHeadersRead))
        {
            await Sse.ReadAll(first);
        }

        _factory.Provider.StreamHandler = (_, ct) => FakeChatProvider.Yield(ct, new ChatStreamChunk("second answer"), FakeChatProvider.Final());
        using var regen = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{id}/regenerate/stream") { Content = JsonContent.Create(new { model = "tiny:1b" }) },
            HttpCompletionOption.ResponseHeadersRead);
        var events = await Sse.ReadAll(regen);

        Assert.Equal("done", events[^1].Event);
        var messages = (await Get(id)).GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString());
        Assert.Equal(["question", "second answer"], messages);
    }

    [Fact]
    public async Task Regenerate_on_empty_conversation_returns_409()
    {
        var id = await CreateConversation();

        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{id}/regenerate/stream") { Content = JsonContent.Create(new { model = "tiny:1b" }) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("nothing_to_regenerate", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Conversations_survive_an_api_restart()
    {
        var id = await CreateConversation("Persistent");

        using var restarted = new ApiFactoryOnSameDatabase(_factory.DatabasePath, _factory.KeysDirectory);
        using var client = restarted.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { username = ApiFactory.AdminUsername, password = ApiFactory.AdminPassword })).EnsureSuccessStatusCode();

        var conversation = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{id}");
        Assert.Equal("Persistent", conversation.GetProperty("title").GetString());
    }

    private sealed class ApiFactoryOnSameDatabase(string path, string keys) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Ollama:BaseUrl", "http://ollama.invalid:11434");
            builder.UseSetting("Persistence:ConnectionString", $"Data Source={path}");
            builder.UseSetting("Session:KeysDirectory", keys);
        }
    }
}
