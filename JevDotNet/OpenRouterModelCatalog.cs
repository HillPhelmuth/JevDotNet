using System.Collections.Concurrent;
using System.Text.Json;

namespace DecisionsDotNet;

/// <summary>Shares public catalog snapshots, never API keys or evaluation data.</summary>
internal sealed class OpenRouterModelCatalog(TimeProvider clock)
{
    internal static OpenRouterModelCatalog Shared { get; } = new(TimeProvider.System);
    internal const string Path = "api/v1/models?output_modalities=decisions";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<Uri, Entry> _entries = new();

    internal async Task<IReadOnlyList<string>?> ResolveAsync(
        HttpClient client, Uri baseUri, string model, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(baseUri, _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (clock.GetUtcNow() >= entry.NextRefresh)
            {
                try
                {
                    using var response = await client.GetAsync(new Uri(baseUri, Path), cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    entry.Models = Snapshot(Parse(
                        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
                {
                    // A failed refresh preserves the last complete snapshot, or text-only fallback on first use.
                }
                entry.NextRefresh = clock.GetUtcNow() + RefreshInterval;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return entry.Models.GetValueOrDefault(model);
        }
        finally { entry.Gate.Release(); }
    }

    internal static IReadOnlyList<OpenRouterDecisionsModel> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new JsonException("OpenRouter returned an empty or invalid Decisions catalog.");
        var models = new List<OpenRouterDecisionsModel>();
        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new JsonException("OpenRouter returned an invalid model ID.");
            IReadOnlyList<string> inputs = [];
            if (model.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object &&
                architecture.TryGetProperty("input_modalities", out var modalities) &&
                modalities.ValueKind == JsonValueKind.Array && modalities.GetArrayLength() > 0)
            {
                inputs = Array.AsReadOnly(modalities.EnumerateArray().Select(value =>
                    value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                        ? value.GetString()! : throw new JsonException("OpenRouter returned an invalid input modality.")).ToArray());
            }
            models.Add(new OpenRouterDecisionsModel
            {
                Id = id.GetString()!,
                CanonicalSlug = OptionalString(model, "canonical_slug"),
                Name = OptionalString(model, "name"),
                Description = OptionalString(model, "description"),
                Created = OptionalTimestamp(model),
                InputModalities = inputs
            });
        }
        return models.AsReadOnly();
    }

    private static Dictionary<string, IReadOnlyList<string>> Snapshot(IReadOnlyList<OpenRouterDecisionsModel> models)
    {
        var snapshot = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        // Missing capabilities are unresolved, not confirmed text-only.
        foreach (var model in models.Where(model => model.InputModalities.Count > 0))
        {
            snapshot[model.Id] = model.InputModalities;
            if (!string.IsNullOrWhiteSpace(model.CanonicalSlug))
                snapshot[model.CanonicalSlug] = model.InputModalities;
        }
        return snapshot;
    }

    // Optional metadata that is null, non-numeric, or out of range is treated as absent.
    private static DateTimeOffset? OptionalTimestamp(JsonElement model)
        => model.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number &&
           created.TryGetInt64(out var seconds) && seconds >= MinUnixSeconds && seconds <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private static string? OptionalString(JsonElement model, string name)
        => model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class Entry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset NextRefresh { get; set; } = DateTimeOffset.MinValue;
        internal Dictionary<string, IReadOnlyList<string>> Models { get; set; } = new(StringComparer.Ordinal);
    }
}
