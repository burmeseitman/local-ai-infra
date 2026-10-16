namespace LocalAI.Core.Exceptions;

public enum DocumentErrorCode
{
    NotFound,
    UnsupportedType,
    TooLarge,
    NoText,
    Unreadable,
    EmbeddingModelMissing,
    LimitReached,
}

/// <summary>Document upload and retrieval failures. <see cref="Exception.Message"/> is safe to show to clients.</summary>
public sealed class DocumentException(DocumentErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public DocumentErrorCode Code { get; } = code;

    public static DocumentException NotFound(Guid id) =>
        new(DocumentErrorCode.NotFound, $"Document '{id}' does not exist.");

    public static DocumentException EmbeddingModelMissing(string model) =>
        new(DocumentErrorCode.EmbeddingModelMissing,
            $"The embedding model '{model}' is not installed. Install it with: ollama pull {model}");
}
