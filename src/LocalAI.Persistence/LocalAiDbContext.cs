using LocalAI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LocalAI.Persistence;

internal sealed class LocalAiDbContext(DbContextOptions<LocalAiDbContext> options) : DbContext(options)
{
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot order or compare DateTimeOffset stored as text; store UTC ticks instead.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConversationEntity>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(c => c.Id);
            e.Property(c => c.Title).HasMaxLength(200).IsRequired();
            e.HasIndex(c => c.UpdatedAt);
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
