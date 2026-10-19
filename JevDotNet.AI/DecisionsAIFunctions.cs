using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DecisionsDotNet.AI;

/// <summary>A question an agent writes for <see cref="DecisionsAIFunctions.AsOpenDecisionsAIFunction"/>.</summary>
public sealed record DecisionToolQuestion(
    [property: Description("Short unique ID for this question, such as \"urgent\". Answers are keyed by it.")] string Id,
    [property: Description("\"yes_no\" for a probability that the instructions are true, \"choice\" to pick one option, or \"score\" to rate on ordered levels.")] string Type,
    [property: Description("What to decide about the input, phrased as a question.")] string Instructions,
    [property: Description("For choice: two or more option names. Include a fallback such as \"other\".")] string[]? Options = null,
    [property: Description("For score: two to ten level descriptions, lowest first.")] string[]? Levels = null);

/// <summary>Exposes an <see cref="IDecisionsClient"/> as Microsoft.Extensions.AI tools for chat clients and agents.</summary>
public static class DecisionsAIFunctions
{
    /// <summary>
    /// Creates a tool that asks a fixed set of questions about text the model supplies. Use this when the app knows
    /// which decisions it needs, such as ticket triage, and the agent only decides when to ask.
    /// </summary>
    public static AIFunction AsAIFunction(this IDecisionsClient client, string name, string description,
        IReadOnlyDictionary<string, DecisionsQuestion> questions, string model)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        // Validates the questions now rather than on the agent's first call.
        var template = new DecisionsRequest("validate", questions, model);

        return AIFunctionFactory.Create(
            async ([Description("The text to evaluate, quoted in full.")] string input, CancellationToken cancellationToken) =>
                ToResult(await client.EvaluateAsync(new DecisionsRequest(input, template.Questions, model), cancellationToken)
                    .ConfigureAwait(false), template.Questions),
            new AIFunctionFactoryOptions { Name = name, Description = description });
    }

    /// <summary>
    /// Creates a tool that lets the model write its own yes/no, choice, and score questions about some text.
    /// Answers are calibrated probabilities from a classifier model rather than generated text.
    /// </summary>
    public static AIFunction AsOpenDecisionsAIFunction(this IDecisionsClient client, string model,
        string name = "evaluate_decisions", string? description = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        description ??= "Answers yes/no, multiple-choice, and rating questions about a piece of text with calibrated " +
            "probabilities. Use it to classify, route, or score content instead of judging it yourself. " +
            "Ask several independent questions in one call.";

        return AIFunctionFactory.Create(
            async ([Description("The text to evaluate, quoted in full.")] string input,
                [Description("One or more independent questions about the input.")] DecisionToolQuestion[] questions,
                CancellationToken cancellationToken) =>
            {
                var built = BuildQuestions(questions);
                return ToResult(await client.EvaluateAsync(new DecisionsRequest(input, built, model), cancellationToken)
                    .ConfigureAwait(false), built);
            },
            new AIFunctionFactoryOptions { Name = name, Description = description });
    }

    internal static IReadOnlyDictionary<string, DecisionsQuestion> BuildQuestions(DecisionToolQuestion[]? questions)
    {
        if (questions is null || questions.Length == 0)
            throw new ArgumentException("Ask at least one question.", nameof(questions));
        var built = new Dictionary<string, DecisionsQuestion>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (question is null || string.IsNullOrWhiteSpace(question.Id) || string.IsNullOrWhiteSpace(question.Instructions))
                throw new ArgumentException("Every question needs an id and instructions.", nameof(questions));
            DecisionsQuestion value = question.Type?.Trim().ToLowerInvariant() switch
            {
                "yes_no" or "noul" or "predicate" => new NoulQuestion(question.Instructions),
                "choice" => new ChoiceQuestion(question.Instructions,
                    (question.Options ?? []).Distinct(StringComparer.Ordinal)
                        .ToDictionary(option => option, _ => (DecisionsValue?)null, StringComparer.Ordinal)),
                "score" => new ScoreQuestion(question.Instructions,
                    (question.Levels ?? []).Select(level => (DecisionsValue)level).ToArray()),
                _ => throw new ArgumentException($"Question '{question.Id}' has unknown type '{question.Type}'. Use yes_no, choice, or score.",
                    nameof(questions))
            };
            if (!built.TryAdd(question.Id, value))
                throw new ArgumentException($"Question ID '{question.Id}' is used more than once.", nameof(questions));
        }
        return built;
    }

    /// <summary>A compact answer summary for a model to read.</summary>
    internal static JsonObject ToResult(DecisionsResponse response, IReadOnlyDictionary<string, DecisionsQuestion> questions)
    {
        var answers = new JsonObject();
        foreach (var id in questions.Keys)
        {
            answers[id] = response.Answers.TryGetValue(id, out var answer) ? answer switch
            {
                NoulAnswer noul => new JsonObject { ["type"] = "yes_no", ["probability_yes"] = Round(noul.Noul) },
                ChoiceAnswer choice => new JsonObject
                {
                    ["type"] = "choice", ["choice"] = choice.Choice, ["confidence"] = Round(choice.Confidence),
                    ["probabilities"] = new JsonObject(choice.Probabilities.Select(pair =>
                        KeyValuePair.Create(pair.Key, (JsonNode?)Round(pair.Value))))
                },
                ScoreAnswer score => new JsonObject
                {
                    ["type"] = "score",
                    ["score"] = Round(score.Score),
                    ["most_likely_level"] = MostLikely(score),
                    ["confidence"] = Round(score.Confidence)
                },
                BoxAnswer box => new JsonObject
                {
                    ["type"] = "box", ["top"] = Round(box.Box.Top), ["left"] = Round(box.Box.Left),
                    ["bottom"] = Round(box.Box.Bottom), ["right"] = Round(box.Box.Right), ["confidence"] = Round(box.Confidence)
                },
                RefusalAnswer => new JsonObject { ["type"] = "refusal" },
                _ => new JsonObject { ["type"] = "unknown" }
            } : new JsonObject { ["type"] = "missing" };
        }
        return new JsonObject { ["model"] = response.Model, ["answers"] = answers };
    }

    private static string? MostLikely(ScoreAnswer score)
    {
        var best = score.Probabilities.OrderByDescending(pair => pair.Value)
            .ThenBy(pair => int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : int.MaxValue)
            .Select(pair => pair.Key).FirstOrDefault();
        return best is not null && score.Legend.TryGetValue(best, out var label) ? label : best;
    }

    private static double Round(double value) => Math.Round(value, 3);
}
