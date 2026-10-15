namespace LocalAI.Core.Models;

public enum UserRole
{
    User,
    Admin,
}

/// <summary>
/// An account. <see cref="SessionVersion"/> increases whenever credentials or role change,
/// which invalidates every existing session for that user.
/// </summary>
public sealed record UserAccount(
    Guid Id,
    string Username,
    UserRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    int SessionVersion);

/// <summary>An account together with its password hash. Never leaves the server.</summary>
public sealed record UserCredentials(UserAccount Account, string PasswordHash);
