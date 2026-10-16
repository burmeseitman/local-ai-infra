using LocalAI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LocalAI.Persistence;

internal sealed class LocalAiDbContext(DbContextOptions<LocalAiDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot order or compare DateTimeOffset stored as text; store UTC ticks instead.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Username).HasMaxLength(32).IsRequired();
            e.Property(u => u.NormalizedUsername).HasMaxLength(32).IsRequired();
            e.HasIndex(u => u.NormalizedUsername).IsUnique();
            e.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(u => u.Role).HasMaxLength(16).IsRequired();
            e.HasMany(u => u.Conversations)
                .WithOne(c => c.Owner)
                .HasForeignKey(c => c.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(u => u.Documents)
                .WithOne(d => d.Owner)
                .HasForeignKey(d => d.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentEntity>(e =>
        {
            e.ToTable("documents");
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.OwnerId, d.CreatedAt });
            e.HasIndex(d => d.Status);
            e.Property(d => d.Title).HasMaxLength(200).IsRequired();
            e.Property(d => d.FileName).HasMaxLength(260).IsRequired();
            e.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
            e.Property(d => d.Status).HasMaxLength(16).IsRequired();
            e.Property(d => d.Error).HasMaxLength(500);
            e.Property(d => d.ContentJson).IsRequired();
            e.HasMany(d => d.Chunks)
                .WithOne(c => c.Document)
                .HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChunkEntity>(e =>
        {
            e.ToTable("document_chunks");
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.DocumentId, c.Index }).IsUnique();
            e.Property(c => c.Text).IsRequired();
            e.Property(c => c.Embedding).IsRequired();
        });

        modelBuilder.Entity<ConversationEntity>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(c => c.Id);
            e.Property(c => c.Title).HasMaxLength(200).IsRequired();
            e.HasIndex(c => c.UpdatedAt);
            e.HasIndex(c => new { c.OwnerId, c.UpdatedAt });
            e.HasMany(c => c.Messages)
                .WithOne(m => m.Conversation)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageEntity>(e =>
        {
            e.ToTable("messages");
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
            e.Property(m => m.Role).HasMaxLength(16).IsRequired();
            e.Property(m => m.Status).HasMaxLength(16).IsRequired();
            e.Property(m => m.Content).IsRequired();
            e.Property(m => m.Model).HasMaxLength(200);
            e.Property(m => m.ErrorCode).HasMaxLength(64);
            e.Property(m => m.ErrorMessage).HasMaxLength(500);
        });
    }
}

/// <summary>Used only by `dotnet ef` to create migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LocalAiDbContext>
{
    public LocalAiDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LocalAiDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
