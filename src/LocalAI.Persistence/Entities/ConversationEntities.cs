namespace LocalAI.Persistence.Entities;

// Storage shapes, mapped to LocalAI.Core records by SqliteConversationStore.
// Enums are stored as strings so the database stays readable with any SQLite tool.

internal sealed class UserEntity
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int SessionVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<ConversationEntity> Conversations { get; set; } = [];
    public List<DocumentEntity> Documents { get; set; } = [];
}

internal sealed class ConversationEntity
{
    public Guid Id { get; set; }

    /// <summary>Null only for conversations created before accounts existed (claimed at setup).</summary>
    public Guid? OwnerId { get; set; }
    public UserEntity? Owner { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<MessageEntity> Messages { get; set; } = [];
}

internal sealed class MessageEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public int Sequence { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public double? TotalDurationMs { get; set; }
    public double? LoadDurationMs { get; set; }
    public double? GenerationDurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>JSON array of document passages the reply was grounded on (null when none).</summary>
    public string? SourcesJson { get; set; }
    public ConversationEntity? Conversation { get; set; }
}

internal sealed class DocumentEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public UserEntity? Owner { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>Extracted text as a JSON array of { page, text } segments (kept for re-indexing).</summary>
    public string ContentJson { get; set; } = "[]";
    public int CharacterCount { get; set; }
    public int ChunkCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public List<ChunkEntity> Chunks { get; set; } = [];
}

internal sealed class ChunkEntity
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public DocumentEntity? Document { get; set; }
    public int Index { get; set; }
    public int? Page { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>float32 vector, little-endian.</summary>
    public byte[] Embedding { get; set; } = [];
}
