using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Services;

/// <summary>Document chat (retrieval-augmented generation). Bound from the "Rag" section.</summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Ollama embedding model. It is never downloaded automatically.</summary>
    [Required]
    [MaxLength(200)]
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Task prefixes some embedding models expect (nomic-embed-text does). Empty to disable.</summary>
    public string DocumentPrefix { get; set; } = "search_document: ";

    public string QueryPrefix { get; set; } = "search_query: ";

    [Range(200, 8_000)]
    public int ChunkSize { get; set; } = 1_000;

    [Range(0, 2_000)]
    public int ChunkOverlap { get; set; } = 150;

    [Range(1, 128)]
    public int EmbeddingBatchSize { get; set; } = 16;

    [Range(1_024, 100 * 1_024 * 1_024)]
    public long MaxFileBytes { get; set; } = 10 * 1_024 * 1_024;

    /// <summary>Upper bound on extracted text per document (bounds indexing time and storage).</summary>
    [Range(1_000, 20_000_000)]
    public int MaxDocumentCharacters { get; set; } = 2_000_000;

    [Range(1, 10_000)]
    public int MaxDocumentsPerUser { get; set; } = 200;

    /// <summary>Passages added to the prompt per question.</summary>
    [Range(1, 20)]
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Minimum cosine similarity for a passage to be used. Tuned for nomic-embed-text: relevant
    /// passages scored 0.55–0.80 and unrelated questions peaked below 0.49 in end-to-end tests.
    /// </summary>
    [Range(-1.0, 1.0)]
    public double MinScore { get; set; } = 0.5;
}

public sealed class RagOptionsValidator : IValidateOptions<RagOptions>
{
    public ValidateOptionsResult Validate(string? name, RagOptions options) =>
        options.ChunkOverlap >= options.ChunkSize
            ? ValidateOptionsResult.Fail($"Rag:ChunkOverlap ({options.ChunkOverlap}) must be smaller than Rag:ChunkSize ({options.ChunkSize}).")
            : !ChatRequestValidator.IsValidModelName(options.EmbeddingModel)
                ? ValidateOptionsResult.Fail($"Rag:EmbeddingModel '{options.EmbeddingModel}' is not a valid model identifier.")
                : ValidateOptionsResult.Success;
}
