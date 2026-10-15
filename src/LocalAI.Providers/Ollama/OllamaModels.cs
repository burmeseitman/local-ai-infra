using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAI.Providers.Ollama;

// Wire-format DTOs for the Ollama HTTP API (https://github.com/ollama/ollama/blob/main/docs/api.md).
// They never leave this assembly; OllamaProvider maps them to LocalAI.Core models.

internal static class OllamaJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed class OllamaVersionResponse
{
    public string? Version { get; set; }
}

internal sealed class OllamaTagsResponse
{
    public List<OllamaModelEntry>? Models { get; set; }
}

internal sealed class OllamaModelEntry
{
    public string? Name { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public long? Size { get; set; }
    public string? Digest { get; set; }
    public OllamaModelDetails? Details { get; set; }

    /// <summary>Present for models proxied to a remote host (Ollama "cloud" models).</summary>
    public string? RemoteHost { get; set; }
    public string? RemoteModel { get; set; }
}

internal sealed class OllamaModelDetails
{
    public string? Format { get; set; }
    public string? Family { get; set; }
    public string? ParameterSize { get; set; }
    public string? QuantizationLevel { get; set; }
}

internal sealed class OllamaChatRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<OllamaMessage> Messages { get; init; }
    public bool Stream { get; init; } = true;
    public OllamaGenerationOptions? Options { get; init; }
    public string? KeepAlive { get; init; }
}

internal sealed class OllamaMessage
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}

internal sealed class OllamaGenerationOptions
{
    public double? Temperature { get; init; }
    public int? NumPredict { get; init; }
}

internal sealed class OllamaChatChunk
{
    public string? Model { get; set; }
    public OllamaMessage? Message { get; set; }
    public bool Done { get; set; }
    public string? DoneReason { get; set; }
    public long? TotalDuration { get; set; }
    public long? LoadDuration { get; set; }
    public int? PromptEvalCount { get; set; }
    public int? EvalCount { get; set; }
    public long? EvalDuration { get; set; }
    public string? Error { get; set; }
}

internal sealed class OllamaErrorResponse
{
    public string? Error { get; set; }
}
