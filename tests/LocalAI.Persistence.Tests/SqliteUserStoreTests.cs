using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;
using LocalAI.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LocalAI.Persistence.Tests;

public sealed class SqliteUserStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly LocalAiDbContext _db;
    private readonly SqliteUserStore _users;

    public SqliteUserStoreTests()
    {
        _connection.Open();
        _db = new LocalAiDbContext(new DbContextOptionsBuilder<LocalAiDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        _users = new SqliteUserStore(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Creates_and_finds_users_case_insensitively()
    {
        var created = await _users.CreateAsync("Alice", "hash-1", UserRole.User, CancellationToken.None);

        var found = await _users.FindCredentialsByUsernameAsync("  ALICE ", CancellationToken.None);
        Assert.Equal((created.Id, "Alice", "hash-1", 1), (found!.Account.Id, found.Account.Username, found.PasswordHash, found.Account.SessionVersion));
        Assert.Null(await _users.FindCredentialsByUsernameAsync("bob", CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_username_is_rejected_case_insensitively()
    {
        await _users.CreateAsync("alice", "h", UserRole.User, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuthException>(() => _users.CreateAsync("ALICE", "h", UserRole.User, CancellationToken.None));

        Assert.Equal(AuthErrorCode.UsernameTaken, ex.Code);
        Assert.Equal(1, await _users.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task First_admin_claims_conversations_created_before_accounts_existed()
    {
        // A Phase 2 database upgraded by migration: conversations without an owner.
        _db.Conversations.Add(new ConversationEntity { Id = Guid.NewGuid(), Title = "legacy", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        var admin = await _users.CreateFirstAdminAsync("root", "h", CancellationToken.None);

        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.Equal(admin.Id, (await _db.Conversations.AsNoTracking().SingleAsync()).OwnerId);
        var again = await Assert.ThrowsAsync<AuthException>(() => _users.CreateFirstAdminAsync("second", "h", CancellationToken.None));
        Assert.Equal(AuthErrorCode.SetupCompleted, again.Code);
    }

    [Fact]
    public async Task Password_and_role_changes_bump_the_session_version_but_rehash_does_not()
    {
        var user = await _users.CreateAsync("bob", "h1", UserRole.User, CancellationToken.None);

        await _users.RehashAsync(user.Id, "h1-upgraded", CancellationToken.None);
        Assert.Equal(1, (await _users.FindByIdAsync(user.Id, CancellationToken.None))!.SessionVersion);

        await _users.SetPasswordAsync(user.Id, "h2", CancellationToken.None);
        await _users.SetRoleAsync(user.Id, UserRole.Admin, CancellationToken.None);

        var credentials = await _users.FindCredentialsByIdAsync(user.Id, CancellationToken.None);
        Assert.Equal((3, UserRole.Admin, "h2"), (credentials!.Account.SessionVersion, credentials.Account.Role, credentials.PasswordHash));
        Assert.Equal(1, await _users.CountAdminsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_conversations_and_messages()
    {
        var user = await _users.CreateAsync("bob", "h", UserRole.User, CancellationToken.None);
        var conversations = new SqliteConversationStore(_db, TimeProvider.System);
        var c = await conversations.CreateAsync(user.Id, "x", CancellationToken.None);
        await conversations.AppendMessageAsync(c.Id, new NewMessage(ChatRole.User, "hi", MessageStatus.Complete), CancellationToken.None);

        Assert.True(await _users.DeleteAsync(user.Id, CancellationToken.None));

        Assert.Equal(0, await _db.Conversations.CountAsync());
        Assert.Equal(0, await _db.Messages.CountAsync());
    }
}
