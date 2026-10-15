using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;

namespace LocalAI.Tests.Shared;

/// <summary>In-memory <see cref="IUserStore"/> for service-level tests.</summary>
public sealed class InMemoryUserStore : IUserStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, UserCredentials> _users = new();

    public Task<int> CountAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.Count);
        }
    }

    public Task<int> CountAdminsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.Values.Count(u => u.Account.Role == UserRole.Admin));
        }
    }

    public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<UserAccount>>(_users.Values.Select(u => u.Account).ToArray());
        }
    }

    public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.GetValueOrDefault(id)?.Account);
        }
    }

    public Task<UserCredentials?> FindCredentialsByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.Values.FirstOrDefault(u => string.Equals(u.Account.Username, username.Trim(), StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<UserCredentials?> FindCredentialsByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.GetValueOrDefault(id));
        }
    }

    public Task<UserAccount> CreateAsync(string username, string passwordHash, UserRole role, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_users.Values.Any(u => string.Equals(u.Account.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AuthException(AuthErrorCode.UsernameTaken, "taken");
            }

            var account = new UserAccount(Guid.NewGuid(), username, role, DateTimeOffset.UnixEpoch, null, 1);
            _users[account.Id] = new UserCredentials(account, passwordHash);
            return Task.FromResult(account);
        }
    }

    public Task<UserAccount> CreateFirstAdminAsync(string username, string passwordHash, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_users.Count > 0)
            {
                throw new AuthException(AuthErrorCode.SetupCompleted, "done");
            }

            return CreateAsync(username, passwordHash, UserRole.Admin, cancellationToken);
        }
    }

    public Task<bool> SetPasswordAsync(Guid id, string passwordHash, CancellationToken cancellationToken) =>
        Update(id, u => new UserCredentials(u.Account with { SessionVersion = u.Account.SessionVersion + 1 }, passwordHash));

    public Task<bool> SetRoleAsync(Guid id, UserRole role, CancellationToken cancellationToken) =>
        Update(id, u => u with { Account = u.Account with { Role = role, SessionVersion = u.Account.SessionVersion + 1 } });

    public Task RehashAsync(Guid id, string passwordHash, CancellationToken cancellationToken) =>
        Update(id, u => u with { PasswordHash = passwordHash });

    public Task RecordLoginAsync(Guid id, CancellationToken cancellationToken) =>
        Update(id, u => u with { Account = u.Account with { LastLoginAt = DateTimeOffset.UnixEpoch.AddDays(1) } });

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.Remove(id));
        }
    }

    private Task<bool> Update(Guid id, Func<UserCredentials, UserCredentials> change)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(id, out var u))
            {
                return Task.FromResult(false);
            }

            _users[id] = change(u);
            return Task.FromResult(true);
        }
    }
}
