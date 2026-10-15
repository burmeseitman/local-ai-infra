namespace LocalAI.Persistence.Entities;

// Storage shapes, mapped to LocalAI.Core records by SqliteConversationStore.
// Enums are stored as strings so the database stays readable with any SQLite tool.

internal sealed class ConversationEntity
{
    public Guid Id { get; set; }
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
    public ConversationEntity? Conversation { get; set; }
}
