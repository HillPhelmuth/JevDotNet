using Microsoft.Agents.AI;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevDotNet.Demo.Services;

public sealed class DemoDecisionModel(string id, string name)
{
    [JsonPropertyName("id")]
    public string? Id { get; set; } = id;

    [JsonPropertyName("canonical_slug")]
    public string? CanonicalSlug { get; set; }

    [JsonPropertyName("hugging_face_id")]
    public string? HuggingFaceId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; } = name;

    [JsonPropertyName("created")]
    public int Created { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("architecture")]
    public Architecture? Architecture { get; set; }

    public bool ModelSupportsImageInput() => Architecture?.InputModalities?.Contains("image") == true;
    public override string ToString()
    {
        return $"Name: {Name}, Id: {Id}";
    }
}
public class Architecture
{
    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("input_modalities")]
    public List<string>? InputModalities { get; set; }

    [JsonPropertyName("output_modalities")]
    public List<string>? OutputModalities { get; set; }

    [JsonPropertyName("tokenizer")]
    public string? Tokenizer { get; set; }

    [JsonPropertyName("instruct_type")]
    public string? InstructType { get; set; }
}


public static class DemoDecisionModels
{
    public const string DefaultModelId = "typesafe/jev-1.14";

    private static IReadOnlyList<DemoDecisionModel> models = Array.Empty<DemoDecisionModel>();

    public static IReadOnlyList<DemoDecisionModel> All => Volatile.Read(ref models);

    public static async Task RefreshAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        var response = await client.GetFromJsonAsync<ModelsResponse>(
            "https://openrouter.ai/api/v1/models?output_modalities=decisions", cancellationToken);
        var available = response?.Data;
        if (available is null || available.Length == 0 ||
            available.Any(model => model is null || string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Name)) ||
            available.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != available.Length)
            throw new JsonException("OpenRouter returned an empty or invalid Decisions model catalog.");

        // Publish only a complete, validated snapshot; a failed refresh leaves the previous catalog intact.
        Volatile.Write(ref models, Array.AsReadOnly(available));
    }

    private sealed record ModelsResponse(DemoDecisionModel[]? Data);

    public static bool IsSupported(string? modelId)
        => All.Any(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));

    public static string RequireSupported(string? modelId)
        => IsSupported(modelId)
            ? modelId!
            : throw new ArgumentException("Select one of the available Decisions models.", nameof(modelId));
}
