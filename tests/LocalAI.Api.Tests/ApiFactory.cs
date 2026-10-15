using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalAI.Core.Interfaces;
using LocalAI.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LocalAI.Api.Tests;

/// <summary>Hosts the real API pipeline with the Ollama provider replaced by <see cref="FakeChatProvider"/>.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeChatProvider Provider { get; } = new();

    public int MaxConcurrentGenerations { get; init; } = 2;

    /// <summary>Each factory gets its own SQLite file so tests never share conversations.</summary>
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"localai-test-{Guid.NewGuid():N}.db");

    public string KeysDirectory { get; } = Path.Combine(Path.GetTempPath(), $"localai-test-keys-{Guid.NewGuid():N}");

    public const string AdminUsername = "admin";
    public const string AdminPassword = "admin-password-123";

    private readonly SemaphoreSlim _setupGate = new(1, 1);
    private bool _setupDone;

    /// <summary>A client signed in as the administrator (created on first use via /api/auth/setup).</summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(string username = AdminUsername, string password = AdminPassword)
    {
        var client = CreateClient(); // handles cookies
        await _setupGate.WaitAsync();
        try
        {
            if (!_setupDone)
            {
                (await client.PostAsJsonAsync("/api/auth/setup", new { username = AdminUsername, password = AdminPassword })).EnsureSuccessStatusCode();
                _setupDone = true;
                if (username == AdminUsername)
                {
                    return client;
                }
            }
        }
        finally
        {
            _setupGate.Release();
        }

        (await client.PostAsJsonAsync("/api/auth/login", new { username, password })).EnsureSuccessStatusCode();
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Ollama:BaseUrl", "http://ollama.invalid:11434");
        builder.UseSetting("Chat:DefaultModel", "tiny:1b");
        builder.UseSetting("Chat:ModelListCacheDuration", "00:00:00");
        builder.UseSetting("Api:MaxConcurrentGenerations", MaxConcurrentGenerations.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Api:MaxRequestBodyBytes", "65536");
        builder.UseSetting("Persistence:ConnectionString", $"Data Source={DatabasePath}");
        builder.UseSetting("Session:KeysDirectory", KeysDirectory);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatProvider>();
            services.AddSingleton<IChatProvider>(Provider);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(DatabasePath + suffix);
        }

        if (Directory.Exists(KeysDirectory))
        {
            Directory.Delete(KeysDirectory, recursive: true);
        }

        _setupGate.Dispose();
    }
}

public sealed record SseEvent(string Event, JsonElement Data);

internal static class Sse
{
    public static HttpRequestMessage StreamRequest(object body) =>
        new(HttpMethod.Post, "/api/chat/stream") { Content = JsonContent.Create(body) };

    public static object ChatBody(string model = "tiny:1b", string content = "Hello") =>
        new { model, messages = new[] { new { role = "user", content } } };

    /// <summary>Reads SSE frames incrementally from the response body.</summary>
    public static async IAsyncEnumerable<SseEvent> ReadEvents(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                if (eventName is not null)
                {
                    yield return new SseEvent(eventName, JsonDocument.Parse(data.ToString()).RootElement.Clone());
                }

                eventName = null;
                data.Clear();
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data.Append(line["data: ".Length..]);
            }
        }
    }

    public static async Task<List<SseEvent>> ReadAll(HttpResponseMessage response)
    {
        var events = new List<SseEvent>();
        await foreach (var e in ReadEvents(response))
        {
            events.Add(e);
        }

        return events;
    }
}
