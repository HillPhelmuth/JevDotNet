using System.Collections.Concurrent;
using System.Text.Json;

namespace DecisionsDotNet;

/// <summary>Shares public catalog snapshots, never API keys or evaluation data.</summary>
internal sealed class OpenRouterModelCatalog(TimeProvider clock)
{
    internal static OpenRouterModelCatalog Shared { get; } = new(TimeProvider.System);
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
                    using var response = await client.GetAsync(new Uri(baseUri,
                        "api/v1/models?output_modalities=decisions"), cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    var snapshot = Parse(document.RootElement);
                    entry.Models = snapshot;
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

    private static Dictionary<string, IReadOnlyList<string>> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new JsonException("OpenRouter returned an empty or invalid Decisions catalog.");
        var models = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new JsonException("OpenRouter returned an invalid model ID.");
            if (!model.TryGetProperty("architecture", out var architecture) || architecture.ValueKind != JsonValueKind.Object ||
                !architecture.TryGetProperty("input_modalities", out var modalities) ||
                modalities.ValueKind != JsonValueKind.Array || modalities.GetArrayLength() == 0)
                continue; // Missing capabilities are unresolved, not confirmed text-only.
            var inputs = modalities.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()! : throw new JsonException("OpenRouter returned an invalid input modality.")).ToArray();
            models[id.GetString()!] = Array.AsReadOnly(inputs);
            if (model.TryGetProperty("canonical_slug", out var slug) && slug.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(slug.GetString()))
                models[slug.GetString()!] = Array.AsReadOnly(inputs);
        }
        return models;
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset NextRefresh { get; set; } = DateTimeOffset.MinValue;
        internal Dictionary<string, IReadOnlyList<string>> Models { get; set; } = new(StringComparer.Ordinal);
    }
}
