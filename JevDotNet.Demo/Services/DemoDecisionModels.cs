namespace JevDotNet.Demo.Services;

public sealed record DemoDecisionModel(string Id, string Name);

public static class DemoDecisionModels
{
    public const string DefaultModelId = "typesafe/jev-1.13";

    public static IReadOnlyList<DemoDecisionModel> All { get; } =
    [
        new(DefaultModelId, "TypeSafe: Jev 1.13"),
        new("~typesafe/jev-latest", "TypeSafe: Jev Latest"),
        new("jaredpalmer/kev-4b", "Jared Palmer: Kev 4B"),
        new("respan/span-01", "Respan: Span-01"),
        new("respan/span-01-lite", "Respan: Span-01 Lite"),
        new("respan/span-01-lite:free", "Respan: Span-01 Lite (free)"),
        new("upstage/solar-decide", "Upstage: Solar Decide"),
        new("inception/mercury-decide:free", "Inception: Mercury Decide (free)"),
        new("togethercomputer/tev1-4b-experimental", "Together: Tev1 4B Experimental"),
        new("cloudflare/clef", "Cloudflare: Clef"),
        new("cloudflare/clef-flash", "Cloudflare: Clef Flash"),
        new("liquid/d1", "LiquidAI: D1"),
        new("perplexity/pplx-decider-v1-27b", "Perplexity: Decider V1 27B")
    ];

    public static bool IsSupported(string? modelId)
        => All.Any(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));

    public static string RequireSupported(string? modelId)
        => IsSupported(modelId)
            ? modelId!
            : throw new ArgumentException("Select one of the available Decisions models.", nameof(modelId));
}
