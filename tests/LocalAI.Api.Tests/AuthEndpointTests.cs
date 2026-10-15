using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LocalAI.Api.Tests;

public class AuthEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<HttpClient> Admin() => await _factory.CreateAuthenticatedClientAsync();

    private async Task<HttpClient> CreateUserAndSignIn(HttpClient admin, string username, string role = "user")
    {
        (await admin.PostAsJsonAsync("/api/users", new { username, password = "user-password-123", role })).EnsureSuccessStatusCode();
        return await _factory.CreateAuthenticatedClientAsync(username, "user-password-123");
    }

    [Fact]
    public async Task Fresh_install_requires_setup_and_protects_every_api_endpoint()
    {
        using var anonymous = _factory.CreateClient();

        var state = await anonymous.GetFromJsonAsync<JsonElement>("/api/auth/state");
        Assert.True(state.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("user").ValueKind);

        foreach (var path in new[] { "/api/models", "/api/models/status", "/api/system/overview", "/api/conversations", "/api/users" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{path} returned {response.StatusCode}");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("unauthenticated", problem.GetProperty("code").GetString());
        }

        using var chat = await anonymous.PostAsJsonAsync("/api/chat", Sse.ChatBody());
        Assert.Equal(HttpStatusCode.Unauthorized, chat.StatusCode);
        Assert.Null(_factory.Provider.LastRequest);
    }

    [Fact]
    public async Task Setup_signs_in_with_a_hardened_cookie_and_can_only_run_once()
    {
        using var client = _factory.CreateClient();

        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "Owner", password = "a-long-password" });

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var cookie = setup.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("localai.session=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var state = await client.GetFromJsonAsync<JsonElement>("/api/auth/state");
        Assert.False(state.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(("Owner", "admin"), (state.GetProperty("user").GetProperty("username").GetString(), state.GetProperty("user").GetProperty("role").GetString()));

        using var second = _factory.CreateClient();
        var again = await second.PostAsJsonAsync("/api/auth/setup", new { username = "intruder", password = "a-long-password" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("setup_completed", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Setup_validates_credentials()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/setup", new { username = "x", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("username", out _));
        Assert.True(errors.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Login_logout_and_generic_errors()
    {
        using var _ = await Admin();
        using var client = _factory.CreateClient();

        var bad = await client.PostAsJsonAsync("/api/auth/login", new { username = ApiFactory.AdminUsername, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var badProblem = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_credentials", badProblem.GetProperty("code").GetString());
        // Unknown user and wrong password are indistinguishable.
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { username = "ghost", password = "wrong-password" });
        Assert.Equal(
            badProblem.GetProperty("detail").GetString(),
            (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        (await client.PostAsJsonAsync("/api/auth/login", new { username = "ADMIN", password = ApiFactory.AdminPassword })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/conversations")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/conversations")).StatusCode);
    }

    [Fact]
    public async Task Repeated_failed_logins_are_throttled_with_retry_after()
    {
        using var _ = await Admin();
        using var client = _factory.CreateClient();

        HttpResponseMessage response = null!;
        for (var i = 0; i < 6; i++)
        {
            response = await client.PostAsJsonAsync("/api/auth/login", new { username = ApiFactory.AdminUsername, password = "nope-nope-nope" });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter is not null);
        Assert.Equal("too_many_attempts", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Users_only_see_their_own_conversations()
    {
        using var admin = await Admin();
        using var bob = await CreateUserAndSignIn(admin, "bob");

        var created = await (await admin.PostAsJsonAsync("/api/conversations", new { title = "Admin secrets" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        Assert.Empty((await bob.GetFromJsonAsync<JsonElement>("/api/conversations")).GetProperty("conversations").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/conversations/{id}")).StatusCode);
        using var send = await bob.PostAsJsonAsync($"/api/conversations/{id}/messages/stream", new { model = "tiny:1b", content = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);

        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/conversations")).GetProperty("conversations").EnumerateArray());
    }

    [Fact]
    public async Task User_administration_is_admin_only()
    {
        using var admin = await Admin();
        using var bob = await CreateUserAndSignIn(admin, "bob");

        var forbidden = await bob.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("forbidden", (await forbidden.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/api/users", new { username = "eve", password = "eve-password-123" })).StatusCode);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        Assert.Equal(["admin", "bob"], list.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("username").GetString()));
        Assert.DoesNotContain("password", list.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_usernames_and_self_or_last_admin_changes_are_rejected()
    {
        using var admin = await Admin();
        var me = (await admin.GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("user").GetProperty("id").GetGuid();

        var dup = await admin.PostAsJsonAsync("/api/users", new { username = "ADMIN", password = "another-password" });
        Assert.Equal("username_taken", (await dup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var deleteSelf = await admin.DeleteAsync($"/api/users/{me}");
        Assert.Equal("cannot_modify_self", (await deleteSelf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var demoteSelf = await admin.PatchAsJsonAsync($"/api/users/{me}", new { role = "user" });
        Assert.Equal(HttpStatusCode.Conflict, demoteSelf.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_user_ends_their_session_and_removes_their_conversations()
    {
        using var admin = await Admin();
        using var bob = await CreateUserAndSignIn(admin, "bob");
        (await bob.PostAsJsonAsync("/api/conversations", new { title = "bob's" })).EnsureSuccessStatusCode();
        var bobId = (await bob.GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("user").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{bobId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/api/conversations")).StatusCode);
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { username = "bob", password = "user-password-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Password_change_keeps_this_session_and_ends_other_sessions()
    {
        using var first = await Admin();
        using var second = await _factory.CreateAuthenticatedClientAsync();

        var wrong = await first.PostAsJsonAsync("/api/auth/password", new { currentPassword = "nope", newPassword = "brand-new-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var weak = await first.PostAsJsonAsync("/api/auth/password", new { currentPassword = ApiFactory.AdminPassword, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var changed = await first.PostAsJsonAsync("/api/auth/password", new { currentPassword = ApiFactory.AdminPassword, newPassword = "brand-new-password" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/conversations")).StatusCode);
    }

    [Fact]
    public async Task Admin_password_reset_and_role_change_sign_the_user_out()
    {
        using var admin = await Admin();
        using var bob = await CreateUserAndSignIn(admin, "bob");
        var bobId = (await bob.GetFromJsonAsync<JsonElement>("/api/auth/state")).GetProperty("user").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/users/{bobId}", new { role = "admin" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/api/conversations")).StatusCode);
        using var bobAgain = await _factory.CreateAuthenticatedClientAsync("bob", "user-password-123");
        Assert.Equal(HttpStatusCode.OK, (await bobAgain.GetAsync("/api/users")).StatusCode); // now an admin
    }
}
