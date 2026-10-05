using System.Globalization;
using System.Text.Json;
using JevDotNet;

namespace JevDotNet.Demo.Services;

public sealed record TriageScores(double Cancelling, double Refund, double Angry, double Bug, double NeedsPerson)
{
    public double Get(string key) => key switch
    {
        "cancel" => Cancelling,
        "refund" => Refund,
        "angry" => Angry,
        "bug" => Bug,
        "human" => NeedsPerson,
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    public void Validate()
    {
        foreach (var category in BulkTriageCatalog.Categories)
        {
            var value = Get(category.Key);
            if (!double.IsFinite(value) || value is < 0 or > 1)
                throw new JsonException($"The {category.Label} probability must be between 0 and 1.");
        }
    }
}

public sealed record BulkTriageMessage(string Id, string Text, TriageScores Reference);
public sealed record TriageCategory(string Key, string Label, string Question, string Yes, string No);

public static class BulkTriageCatalog
{
    public const string JevModel = "typesafe/jev-1.13";
    public const string LunaModel = "openai/gpt-6-luna";

    public static IReadOnlyList<TriageCategory> Categories { get; } =
    [
        new("cancel", "Cancelling", "Is this customer at risk of cancelling?",
            "They threaten to leave, plan to, or are shopping around.",
            "A question, a bug report, praise, or a routine request."),
        new("refund", "Refund", "Does this customer want money back?",
            "Asks for a refund, a credit, or a reversed charge.", "Money is not the ask."),
        new("angry", "Angry", "Is this customer angry or frustrated?",
            "Irritated, exasperated, threatening, or repeatedly let down.", "Neutral, curious, or pleased."),
        new("bug", "Bug", "Is this customer reporting something broken?",
            "An error, crash, failure, or wrong behavior.", "A question or request about something that works."),
        new("human", "Needs a person", "Does this need a person, not a help article?",
            "Account-specific, urgent, emotional, or needs a manual action.",
            "A generic how-to a help article fully answers.")
    ];

    public static IReadOnlyList<BulkTriageMessage> Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("The triage dataset must be an array.");

        var messages = new List<BulkTriageMessage>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var text = row.GetProperty("Message").GetString();
            if (string.IsNullOrWhiteSpace(text))
                throw new JsonException("Every triage row needs a message.");
            var reference = new TriageScores(
                ParsePercentage(row, "Cancelling"), ParsePercentage(row, "Refund"),
                ParsePercentage(row, "Angry"), ParsePercentage(row, "Bug"),
                ParsePercentage(row, "Needs a person"));
            reference.Validate();
            messages.Add(new($"m{messages.Count + 1:000}", text, reference));
        }
        if (messages.Count == 0)
            throw new JsonException("The triage dataset is empty.");
        return messages;
    }

    public static DecisionsRequest BuildJevRequest(
        IReadOnlyList<BulkTriageMessage> messages,
        string modelId = DemoDecisionModels.DefaultModelId)
    {
        if (messages.Count == 0)
            throw new ArgumentException("Select at least one message.", nameof(messages));
        modelId = DemoDecisionModels.RequireSupported(modelId);
        var questions = new Dictionary<string, JevQuestion>();
        foreach (var message in messages)
        foreach (var category in Categories)
            questions[QuestionId(message.Id, category.Key)] = new NoulQuestion(
                $"{category.Question} (message {message.Id})",
                new NoulCriteria(category.Yes, category.No));

        // Only IDs and message text enter either model request; reference scores stay in the UI.
        var state = JevValue.FromObject(new
        {
            messages = messages.Select(message => new { id = message.Id, text = message.Text }).ToArray()
        });
        return new DecisionsRequest(state, questions, modelId);
    }

    public static TriageScores ReadJevScores(JevResponse response, string id)
    {
        var values = Categories.ToDictionary(category => category.Key,
            category => response.GetAnswer<NoulAnswer>(QuestionId(id, category.Key)).Noul);
        var scores = new TriageScores(values["cancel"], values["refund"], values["angry"],
            values["bug"], values["human"]);
        scores.Validate();
        return scores;
    }

    public static string QuestionId(string messageId, string categoryKey) => $"{messageId}_{categoryKey}";

    private static double ParsePercentage(JsonElement row, string name)
    {
        var raw = row.GetProperty(name).GetString();
        if (raw is null || !raw.EndsWith('%') ||
            !double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new JsonException($"Invalid percentage for {name}.");
        return value / 100;
    }
}
