namespace DecisionsDotNet;

internal static class ModelInputModalities
{
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Snapshot(
        IReadOnlyDictionary<string, IReadOnlyList<string>> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (model, modalities) in overrides)
        {
            if (string.IsNullOrWhiteSpace(model) || modalities is null || modalities.Count == 0 ||
                modalities.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Modality overrides require a model ID and at least one nonempty input modality.", nameof(overrides));
            copy.Add(model, Array.AsReadOnly(modalities.ToArray()));
        }
        return copy;
    }
}
