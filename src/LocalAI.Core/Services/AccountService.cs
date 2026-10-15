using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Services;

/// <summary>Bound from the "Auth" configuration section.</summary>
public sealed class AccountOptions
{
    public const string SectionName = "Auth";

    [Range(8, 128)]
    public int MinPasswordLength { get; set; } = 10;

    [Range(1, 100)]
    public int MaxFailedLogins { get; set; } = 5;

    [Range(typeof(TimeSpan), "00:00:10", "1.00:00:00")]
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed record AuthState(bool SetupRequired, UserAccount? User);

/// <summary>
/// Local accounts: first-run administrator setup, sign-in, password changes and user
/// administration. There is no self-registration; administrators create accounts.
/// </summary>
public sealed partial class AccountService(
    IUserStore users,
    LoginThrottle throttle,
    IOptions<AccountOptions> options,
    ILogger<AccountService> logger)
{
    public const int MaxPasswordLength = 128;

    private static readonly PasswordHasher<UserAccount> Hasher = new();

    // Verified against when the username does not exist, so both paths cost the same.
    private static readonly string DummyHash = Hasher.HashPassword(null!, "not-a-real-password-0000");

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken) =>
        await users.CountAsync(cancellationToken) == 0;

    /// <summary>Validates new credentials. Keys: <c>username</c>, <c>password</c>.</summary>
    public Dictionary<string, string[]> ValidateNewCredentials(string? username, string? password)
    {
        var errors = new Dictionary<string, string[]>();
        if (username is null || !UsernamePattern().IsMatch(username.Trim()))
        {
            errors["username"] = ["Username must be 3–32 characters: letters, digits, '.', '_' or '-'."];
        }

        var passwordError = ValidatePassword(password);
        if (passwordError is not null)
        {
            errors["password"] = [passwordError];
        }

        return errors;
    }

    public string? ValidatePassword(string? password)
    {
        var min = options.Value.MinPasswordLength;
        return password is null || password.Length < min || password.Length > MaxPasswordLength
            ? $"Password must be {min}–{MaxPasswordLength} characters."
            : null;
    }

    /// <summary>Creates the first administrator. Callers must validate the credentials first.</summary>
    public async Task<UserAccount> SetupAsync(string username, string password, CancellationToken cancellationToken)
    {
        var account = await users.CreateFirstAdminAsync(username.Trim(), Hasher.HashPassword(null!, password), cancellationToken);
        logger.LogInformation("Initial administrator {Username} created", account.Username);
        return account;
    }

    /// <exception cref="AuthException">Invalid credentials or too many attempts.</exception>
    public async Task<UserAccount> SignInAsync(string? username, string? password, CancellationToken cancellationToken)
    {
        var normalized = Normalize(username ?? string.Empty);
        throttle.EnsureAllowed(normalized);

        var credentials = normalized.Length == 0 ? null : await users.FindCredentialsByUsernameAsync(normalized, cancellationToken);
        var result = Hasher.VerifyHashedPassword(null!, credentials?.PasswordHash ?? DummyHash, password ?? string.Empty);
        if (credentials is null || result == PasswordVerificationResult.Failed)
        {
            throttle.RecordFailure(normalized);
            logger.LogWarning("Failed sign-in for {Username}", normalized.Length > 64 ? normalized[..64] : normalized);
            throw AuthException.InvalidCredentials();
        }

        throttle.RecordSuccess(normalized);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await users.RehashAsync(credentials.Account.Id, Hasher.HashPassword(null!, password!), cancellationToken);
        }

        await users.RecordLoginAsync(credentials.Account.Id, cancellationToken);
        logger.LogInformation("User {Username} signed in", credentials.Account.Username);
        return credentials.Account;
    }

    /// <summary>Changes the caller's own password; all other sessions are invalidated.</summary>
    public async Task<UserAccount> ChangePasswordAsync(
        Guid userId, string? currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var credentials = await users.FindCredentialsByIdAsync(userId, cancellationToken)
            ?? throw new AuthException(AuthErrorCode.UserNotFound, "Account not found.");
        if (Hasher.VerifyHashedPassword(null!, credentials.PasswordHash, currentPassword ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            throw new AuthException(AuthErrorCode.InvalidCredentials, "The current password is incorrect.");
        }

        await users.SetPasswordAsync(userId, Hasher.HashPassword(null!, newPassword), cancellationToken);
        return await users.FindByIdAsync(userId, cancellationToken)
            ?? throw new AuthException(AuthErrorCode.UserNotFound, "Account not found.");
    }

    public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) => users.ListAsync(cancellationToken);

    public Task<UserAccount?> FindAsync(Guid id, CancellationToken cancellationToken) => users.FindByIdAsync(id, cancellationToken);

    public async Task<UserAccount> CreateUserAsync(string username, string password, UserRole role, CancellationToken cancellationToken)
    {
        var account = await users.CreateAsync(username.Trim(), Hasher.HashPassword(null!, password), role, cancellationToken);
        logger.LogInformation("User {Username} created with role {Role}", account.Username, role);
        return account;
    }

    /// <summary>Administrative change of another account's role and/or password.</summary>
    public async Task UpdateUserAsync(Guid actorId, Guid id, UserRole? role, string? newPassword, CancellationToken cancellationToken)
    {
        var target = await users.FindByIdAsync(id, cancellationToken)
            ?? throw new AuthException(AuthErrorCode.UserNotFound, "User not found.");

        if (role is { } newRole && newRole != target.Role)
        {
            if (id == actorId)
            {
                throw new AuthException(AuthErrorCode.CannotModifySelf, "You cannot change your own role.");
            }

            if (target.Role == UserRole.Admin && await users.CountAdminsAsync(cancellationToken) <= 1)
            {
                throw new AuthException(AuthErrorCode.LastAdmin, "At least one administrator must remain.");
            }

            await users.SetRoleAsync(id, newRole, cancellationToken);
        }

        if (newPassword is not null)
        {
            await users.SetPasswordAsync(id, Hasher.HashPassword(null!, newPassword), cancellationToken);
        }

        logger.LogInformation("User {Username} updated by administrator", target.Username);
    }

    public async Task DeleteUserAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    {
        if (id == actorId)
        {
            throw new AuthException(AuthErrorCode.CannotModifySelf, "You cannot delete your own account.");
        }

        var target = await users.FindByIdAsync(id, cancellationToken)
            ?? throw new AuthException(AuthErrorCode.UserNotFound, "User not found.");
        if (target.Role == UserRole.Admin && await users.CountAdminsAsync(cancellationToken) <= 1)
        {
            throw new AuthException(AuthErrorCode.LastAdmin, "At least one administrator must remain.");
        }

        await users.DeleteAsync(id, cancellationToken);
        logger.LogInformation("User {Username} deleted", target.Username);
    }
}
