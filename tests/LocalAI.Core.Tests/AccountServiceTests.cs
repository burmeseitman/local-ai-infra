using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Core.Services;
using LocalAI.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Tests;

public class AccountServiceTests
{
    private const string Password = "correct horse battery";

    private readonly InMemoryUserStore _store = new();
    private readonly ManualTimeProvider _time = new();
    private readonly AccountService _accounts;

    public AccountServiceTests()
    {
        var options = Options.Create(new AccountOptions { MinPasswordLength = 10, MaxFailedLogins = 3, LockoutDuration = TimeSpan.FromMinutes(5) });
        _accounts = new AccountService(_store, new LoginThrottle(options, _time), options, NullLogger<AccountService>.Instance);
    }

    [Fact]
    public async Task Setup_creates_the_first_administrator_only_once()
    {
        Assert.True(await _accounts.IsSetupRequiredAsync(CancellationToken.None));

        var admin = await _accounts.SetupAsync("  Alice ", Password, CancellationToken.None);

        Assert.Equal(("Alice", UserRole.Admin), (admin.Username, admin.Role));
        Assert.False(await _accounts.IsSetupRequiredAsync(CancellationToken.None));
        var ex = await Assert.ThrowsAsync<AuthException>(() => _accounts.SetupAsync("bob", Password, CancellationToken.None));
        Assert.Equal(AuthErrorCode.SetupCompleted, ex.Code);
    }

    [Fact]
    public async Task Passwords_are_stored_hashed_and_sign_in_is_case_insensitive_on_username()
    {
        await _accounts.SetupAsync("Alice", Password, CancellationToken.None);

        var stored = await _store.FindCredentialsByUsernameAsync("alice", CancellationToken.None);
        Assert.DoesNotContain(Password, stored!.PasswordHash, StringComparison.Ordinal);
        Assert.StartsWith("AQAAAA", stored.PasswordHash, StringComparison.Ordinal); // Identity v3 PBKDF2 format

        var user = await _accounts.SignInAsync("ALICE", Password, CancellationToken.None);
        Assert.Equal("Alice", user.Username);
        Assert.NotNull((await _store.FindByIdAsync(user.Id, CancellationToken.None))!.LastLoginAt);
    }

    [Theory]
    [InlineData("alice", "wrong password!")]
    [InlineData("nobody", Password)]
    [InlineData(null, null)]
    public async Task Wrong_credentials_get_one_generic_error(string? username, string? password)
    {
        await _accounts.SetupAsync("alice", Password, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuthException>(() => _accounts.SignInAsync(username, password, CancellationToken.None));

        Assert.Equal(AuthErrorCode.InvalidCredentials, ex.Code);
        Assert.Equal("The username or password is incorrect.", ex.Message);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_username_until_the_window_passes()
    {
        await _accounts.SetupAsync("alice", Password, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<AuthException>(() => _accounts.SignInAsync("alice", "wrong-password", CancellationToken.None));
        }

        // Even the right password is refused while locked.
        var locked = await Assert.ThrowsAsync<AuthException>(() => _accounts.SignInAsync("alice", Password, CancellationToken.None));
        Assert.Equal(AuthErrorCode.TooManyAttempts, locked.Code);
        Assert.NotNull(locked.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("alice", (await _accounts.SignInAsync("alice", Password, CancellationToken.None)).Username);
    }

    [Fact]
    public async Task Successful_sign_in_resets_the_failure_count()
    {
        await _accounts.SetupAsync("alice", Password, CancellationToken.None);
        await Assert.ThrowsAsync<AuthException>(() => _accounts.SignInAsync("alice", "wrong-password", CancellationToken.None));
        await Assert.ThrowsAsync<AuthException>(() => _accounts.SignInAsync("alice", "wrong-password", CancellationToken.None));
        await _accounts.SignInAsync("alice", Password, CancellationToken.None);

        await Assert.ThrowsAsync<AuthException>(() => _accounts.SignInAsync("alice", "wrong-password", CancellationToken.None));
        await _accounts.SignInAsync("alice", Password, CancellationToken.None);
    }

    [Theory]
    [InlineData("ab", Password, "username")]
    [InlineData("has space", Password, "username")]
    [InlineData("ok_name", "short", "password")]
    [InlineData(null, null, "username")]
    public void New_credentials_are_validated(string? username, string? password, string expectedKey) =>
        Assert.Contains(expectedKey, _accounts.ValidateNewCredentials(username, password).Keys);

    [Fact]
    public void Valid_credentials_produce_no_errors() =>
        Assert.Empty(_accounts.ValidateNewCredentials("minh.tet-01", Password));

    [Fact]
    public async Task Change_password_requires_the_current_password_and_bumps_session_version()
    {
        var admin = await _accounts.SetupAsync("alice", Password, CancellationToken.None);

        var wrong = await Assert.ThrowsAsync<AuthException>(() =>
            _accounts.ChangePasswordAsync(admin.Id, "not it", "a brand new password", CancellationToken.None));
        Assert.Equal(AuthErrorCode.InvalidCredentials, wrong.Code);

        var updated = await _accounts.ChangePasswordAsync(admin.Id, Password, "a brand new password", CancellationToken.None);

        Assert.Equal(admin.SessionVersion + 1, updated.SessionVersion);
        await Assert.ThrowsAsync<AuthException>(() => _accounts.SignInAsync("alice", Password, CancellationToken.None));
        await _accounts.SignInAsync("alice", "a brand new password", CancellationToken.None);
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_demoted_or_deleted_and_nobody_can_modify_themselves()
    {
        var admin = await _accounts.SetupAsync("alice", Password, CancellationToken.None);
        var other = await _accounts.CreateUserAsync("bob", Password, UserRole.Admin, CancellationToken.None);

        var self = await Assert.ThrowsAsync<AuthException>(() => _accounts.DeleteUserAsync(admin.Id, admin.Id, CancellationToken.None));
        Assert.Equal(AuthErrorCode.CannotModifySelf, self.Code);
        var selfRole = await Assert.ThrowsAsync<AuthException>(() =>
            _accounts.UpdateUserAsync(admin.Id, admin.Id, UserRole.User, null, CancellationToken.None));
        Assert.Equal(AuthErrorCode.CannotModifySelf, selfRole.Code);

        await _accounts.UpdateUserAsync(admin.Id, other.Id, UserRole.User, null, CancellationToken.None);
        Assert.Equal(UserRole.User, (await _accounts.FindAsync(other.Id, CancellationToken.None))!.Role);

        // bob (now a user) is fine to delete; alice remains the only admin.
        await _accounts.DeleteUserAsync(admin.Id, other.Id, CancellationToken.None);
        var promoted = await _accounts.CreateUserAsync("carol", Password, UserRole.Admin, CancellationToken.None);
        await _accounts.DeleteUserAsync(promoted.Id, admin.Id, CancellationToken.None);

        var last = await Assert.ThrowsAsync<AuthException>(() =>
            _accounts.UpdateUserAsync(Guid.NewGuid(), promoted.Id, UserRole.User, null, CancellationToken.None));
        Assert.Equal(AuthErrorCode.LastAdmin, last.Code);
    }

    [Fact]
    public async Task Admin_password_reset_invalidates_the_users_sessions()
    {
        var admin = await _accounts.SetupAsync("alice", Password, CancellationToken.None);
        var user = await _accounts.CreateUserAsync("bob", Password, UserRole.User, CancellationToken.None);

        await _accounts.UpdateUserAsync(admin.Id, user.Id, null, "reset password 123", CancellationToken.None);

        Assert.Equal(user.SessionVersion + 1, (await _accounts.FindAsync(user.Id, CancellationToken.None))!.SessionVersion);
        await _accounts.SignInAsync("bob", "reset password 123", CancellationToken.None);
    }

    [Fact]
    public async Task Duplicate_usernames_are_rejected_case_insensitively()
    {
        await _accounts.SetupAsync("alice", Password, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuthException>(() => _accounts.CreateUserAsync("ALICE", Password, UserRole.User, CancellationToken.None));

        Assert.Equal(AuthErrorCode.UsernameTaken, ex.Code);
    }
}
