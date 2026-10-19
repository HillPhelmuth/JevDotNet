using DecisionsDotNet;

namespace JevDotNet.Demo.Services;

public sealed class ImageDemoAsset(string id, string title, string fileName, string alt, string question, string target)
{
    private readonly Lazy<byte[]> _bytes = new(() => FileHelper.ExtractImageBytesFromAssembly(fileName));
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Alt { get; } = alt;
    public string Question { get; } = question;
    public string Target { get; } = target;
    public string PreviewUrl => $"/demo-images/{Id}";
    public ReadOnlyMemory<byte> Bytes => _bytes.Value;
}

public static class ImageDemoCatalog
{
    public const int MaxPromptLength = 1000;
    public static IReadOnlyList<ImageDemoAsset> All { get; } = Array.AsReadOnly(new[]
    {
        new ImageDemoAsset("breakfast", "Half-eaten breakfast", "Rustic Half-Eaten Breakfast Table.png",
            "A rustic breakfast table with scrambled eggs, bacon, bread, and a cup of coffee.",
            "Does this image show a meal that has already been partially eaten?", "the coffee cup in the upper-right of the image"),
        new ImageDemoAsset("invasion", "New York · Alien invasion", "New York - Alien Invasion.png",
            "An illustrated alien invasion above the New York skyline and Brooklyn Bridge.",
            "Does this image depict a fictional alien invasion?", "the largest spacecraft near the upper-left of the image")
    });

    public static ImageDemoAsset? Find(string id) => All.FirstOrDefault(image => image.Id == id);

    public static IReadOnlyList<OpenRouterDecisionsModel> SupportedModels(bool locate)
        => DemoDecisionModels.All.Where(model => model.SupportsImageInput &&
            (!locate || model.Id == "typesafe/jev-1.14" ||
                model.CanonicalSlug?.StartsWith("typesafe/jev-1.14-", StringComparison.Ordinal) == true)).ToArray();

    public static DecisionsRequest BuildRequest(string imageId, string modelId, string prompt, bool locate)
    {
        var image = Find(imageId) ?? throw new ArgumentException("Select one of the bundled images.", nameof(imageId));
        if (!SupportedModels(locate).Any(model => model.Id == modelId))
            throw new ArgumentException("Select a supported model for this image demo.", nameof(modelId));
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptLength)
            throw new ArgumentException($"Enter {(locate ? "a target" : "a question")} of 1 to {MaxPromptLength} characters.", nameof(prompt));

        var questions = locate
            ? new Dictionary<string, DecisionsQuestion>
            {
                ["target"] = new BoxQuestion($"Locate {prompt.Trim()} in the supplied image."),
                ["visible"] = new NoulQuestion($"Is {prompt.Trim()} visible in the supplied image?")
            }
            : new Dictionary<string, DecisionsQuestion>
            {
                ["question"] = new NoulQuestion(prompt.Trim()),
                ["scene"] = new ChoiceQuestion("Which description best matches the supplied image?",
                    new Dictionary<string, DecisionsValue?>
                    {
                        ["meal"] = "A meal, food, or a dining table.",
                        ["city"] = "A city scene or skyline.",
                        ["other"] = "Neither a meal nor a city scene."
                    }),
                ["activity"] = new ScoreQuestion("How visually busy is the supplied image?",
                    new DecisionsValue[] { "Sparse: a few isolated objects.", "Moderate: several objects with clear separation.",
                        "Busy: many overlapping objects or events." })
            };
        return new DecisionsRequest(DecisionsValue.FromParts(locate ? image.Title : prompt.Trim(),
            DecisionsValue.ImageFromBytes(image.Bytes, "image/png")), questions, modelId);
    }
}
