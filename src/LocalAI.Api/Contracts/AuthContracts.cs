using LocalAI.Core.Models;

namespace LocalAI.Api.Contracts;

public sealed record CredentialsRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CreateUserRequest(string? Username, string? Password, UserRole Role = UserRole.User);

/// <summary>Both fields optional; only supplied values change.</summary>
public sealed record UpdateUserRequest(UserRole? Role = null, string? Password = null);

public sealed record UserDto(Guid Id, string Username, UserRole Role, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt)
{
    public static UserDto From(UserAccount a) => new(a.Id, a.Username, a.Role, a.CreatedAt, a.LastLoginAt);
}

/// <summary>GET /api/auth/state. <see cref="User"/> is null when not signed in.</summary>
public sealed record AuthStateResponse(bool SetupRequired, UserDto? User);

public sealed record UserListResponse(IReadOnlyList<UserDto> Users);
