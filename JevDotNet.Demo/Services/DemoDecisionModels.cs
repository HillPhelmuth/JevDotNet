using DecisionsDotNet;
using System.Text.Json;

namespace JevDotNet.Demo.Services;

public static class DemoDecisionModels
{
    public const string DefaultModelId = "typesafe/jev-1.14";

    private static IReadOnlyList<OpenRouterDecisionsModel> models = Array.Empty<OpenRouterDecisionsModel>();

    public static IReadOnlyList<OpenRouterDecisionsModel> All => Volatile.Read(ref models);

    public static async Task RefreshAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        var available = await OpenRouterDecisionsClient.ListModelsAsync(client, cancellationToken: cancellationToken);
        if (available.Any(model => string.IsNullOrWhiteSpace(model.Name)) ||
            available.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != available.Count)
            throw new JsonException("OpenRouter returned an invalid Decisions model catalog.");

        // Publish only a complete, validated snapshot; a failed refresh leaves the previous catalog intact.
        Volatile.Write(ref models, available);
    }

    public static bool IsSupported(string? modelId)
        => All.Any(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));

    public static string RequireSupported(string? modelId)
        => IsSupported(modelId)
            ? modelId!
            : throw new ArgumentException("Select one of the available Decisions models.", nameof(modelId));
}
