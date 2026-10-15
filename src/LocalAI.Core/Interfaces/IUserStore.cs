using LocalAI.Core.Models;

namespace LocalAI.Core.Interfaces;

/// <summary>Durable account storage. Usernames are unique case-insensitively.</summary>
public interface IUserStore
{
    Task<int> CountAsync(CancellationToken cancellationToken);

    Task<int> CountAdminsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken);

    Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<UserCredentials?> FindCredentialsByUsernameAsync(string username, CancellationToken cancellationToken);

    Task<UserCredentials?> FindCredentialsByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <exception cref="Exceptions.AuthException">The username is taken.</exception>
    Task<UserAccount> CreateAsync(string username, string passwordHash, UserRole role, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically creates the first account as an administrator, but only while no account
    /// exists. Conversations created before authentication existed are assigned to it.
    /// </summary>
    /// <exception cref="Exceptions.AuthException">Setup has already been completed.</exception>
    Task<UserAccount> CreateFirstAdminAsync(string username, string passwordHash, CancellationToken cancellationToken);

    /// <summary>Replaces the hash and increments the session version.</summary>
    Task<bool> SetPasswordAsync(Guid id, string passwordHash, CancellationToken cancellationToken);

    /// <summary>Changes the role and increments the session version.</summary>
    Task<bool> SetRoleAsync(Guid id, UserRole role, CancellationToken cancellationToken);

    /// <summary>Updates the hash without invalidating sessions (algorithm upgrade on login).</summary>
    Task RehashAsync(Guid id, string passwordHash, CancellationToken cancellationToken);

    Task RecordLoginAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Deletes the account and all of its conversations.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
