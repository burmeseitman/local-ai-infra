using LocalAI.Core.Exceptions;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using LocalAI.Core.Services;
using LocalAI.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LocalAI.Persistence;

internal sealed class SqliteUserStore(LocalAiDbContext db, TimeProvider timeProvider) : IUserStore
{
    private const int SqliteConstraintViolation = 19;

    // SQLite serializes writers, but two setup requests could both observe "no users" before
    // either writes; serialize the check-then-insert within this (single) process as well.
    private static readonly SemaphoreSlim SetupGate = new(1, 1);

    public Task<int> CountAsync(CancellationToken cancellationToken) => db.Users.CountAsync(cancellationToken);

    public Task<int> CountAdminsAsync(CancellationToken cancellationToken) =>
        db.Users.CountAsync(u => u.Role == nameof(UserRole.Admin), cancellationToken);

    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Users.AsNoTracking().OrderBy(u => u.NormalizedUsername).ToListAsync(cancellationToken))
        .Select(ToAccount)
        .ToArray();

    public async Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken) is { } u ? ToAccount(u) : null;

    public async Task<UserCredentials?> FindCredentialsByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        var normalized = AccountService.Normalize(username);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancellationToken);
        return user is null ? null : new UserCredentials(ToAccount(user), user.PasswordHash);
    }

    public async Task<UserCredentials?> FindCredentialsByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? null : new UserCredentials(ToAccount(user), user.PasswordHash);
    }

    public async Task<UserAccount> CreateAsync(string username, string passwordHash, UserRole role, CancellationToken cancellationToken)
    {
        var entity = NewUser(username, passwordHash, role);
        db.Users.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintViolation })
        {
            db.Entry(entity).State = EntityState.Detached;
            throw new AuthException(AuthErrorCode.UsernameTaken, $"The username '{username}' is already taken.");
        }

        return ToAccount(entity);
    }

    public async Task<UserAccount> CreateFirstAdminAsync(string username, string passwordHash, CancellationToken cancellationToken)
    {
        await SetupGate.WaitAsync(cancellationToken);
        try
        {
            // Microsoft.Data.Sqlite starts non-deferred (BEGIN IMMEDIATE) transactions: the write lock is taken up front.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (await db.Users.AnyAsync(cancellationToken))
            {
                throw new AuthException(AuthErrorCode.SetupCompleted, "Setup has already been completed. Sign in instead.");
            }

            var entity = NewUser(username, passwordHash, UserRole.Admin);
            db.Users.Add(entity);
            await db.SaveChangesAsync(cancellationToken);

            // Conversations from before accounts existed belong to the first administrator.
            await db.Conversations
                .Where(c => c.OwnerId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerId, entity.Id), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return ToAccount(entity);
        }
        finally
        {
            SetupGate.Release();
        }
    }

    public async Task<bool> SetPasswordAsync(Guid id, string passwordHash, CancellationToken cancellationToken) =>
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.PasswordHash, passwordHash).SetProperty(u => u.SessionVersion, u => u.SessionVersion + 1),
            cancellationToken) > 0;

    public async Task<bool> SetRoleAsync(Guid id, UserRole role, CancellationToken cancellationToken) =>
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.Role, role.ToString()).SetProperty(u => u.SessionVersion, u => u.SessionVersion + 1),
            cancellationToken) > 0;

    public Task RehashAsync(Guid id, string passwordHash, CancellationToken cancellationToken) =>
        db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, passwordHash), cancellationToken);

    public Task RecordLoginAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.LastLoginAt, timeProvider.GetUtcNow()), cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        // Conversations (and their messages) are removed by ON DELETE CASCADE.
        await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private UserEntity NewUser(string username, string passwordHash, UserRole role) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        NormalizedUsername = AccountService.Normalize(username),
        PasswordHash = passwordHash,
        Role = role.ToString(),
        SessionVersion = 1,
        CreatedAt = timeProvider.GetUtcNow(),
    };

    private static UserAccount ToAccount(UserEntity u) =>
        new(u.Id, u.Username, Enum.Parse<UserRole>(u.Role), u.CreatedAt, u.LastLoginAt, u.SessionVersion);
}
