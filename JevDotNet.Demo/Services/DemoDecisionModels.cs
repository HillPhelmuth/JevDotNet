using System.Net.Http.Json;
using System.Text.Json;

namespace JevDotNet.Demo.Services;

public sealed record DemoDecisionModel(string Id, string Name);

public static class DemoDecisionModels
{
    public const string DefaultModelId = "typesafe/jev-1.13";

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
