using System.ComponentModel.DataAnnotations;
using LocalAI.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalAI.Persistence;

/// <summary>Bound from the "Persistence" configuration section.</summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>SQLite connection string. Relative file paths resolve against the app's content root.</summary>
    [Required]
    public string ConnectionString { get; set; } = "Data Source=data/localai.db";
}

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the SQLite-backed <see cref="IConversationStore"/> and <see cref="IUserStore"/>.</summary>
    public static IServiceCollection AddSqlitePersistence(
        this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var options = configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>() ?? new PersistenceOptions();
        var connectionString = ResolveConnectionString(options.ConnectionString, contentRootPath);

        services.AddDbContext<LocalAiDbContext>(db => db.UseSqlite(connectionString));
        services.AddScoped<IConversationStore, SqliteConversationStore>();
        services.AddScoped<IUserStore, SqliteUserStore>();
        return services;
    }

    /// <summary>
    /// Creates the database directory, applies pending migrations and enables WAL mode.
    /// Run once at startup; safe for the single-instance SQLite deployment.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalAiDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LocalAI.Persistence");

        var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        var directory = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrEmpty(directory) && dataSource != ":memory:")
        {
            Directory.CreateDirectory(directory);
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        await db.Database.MigrateAsync(cancellationToken);
        // WAL lets readers proceed while a reply is being written; the setting persists in the file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        logger.LogInformation("Database ready at {DataSource} ({Applied} migration(s) applied)", dataSource, pending.Length);
    }

    private static string ResolveConnectionString(string connectionString, string contentRootPath)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException("Persistence:ConnectionString must specify a Data Source.");
        }

        if (builder.DataSource != ":memory:" && !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRootPath, builder.DataSource));
        }

        return builder.ToString();
    }
}
