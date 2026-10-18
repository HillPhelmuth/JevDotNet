using System.Text.Json;

namespace DecisionsDotNet;

internal static class JevResponseValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DecisionsResponse DeserializeAndValidate(string json, IReadOnlyDictionary<string, DecisionsQuestion> questions)
    {
        var result = JsonSerializer.Deserialize<DecisionsResponse>(json, JsonOptions)
            ?? throw new JsonException("The decision API returned an empty JSON response.");
        if (string.IsNullOrWhiteSpace(result.Model) || result.Answers is null || result.Usage is null ||
            result.Answers.Count != questions.Count)
            throw new JsonException("The decision API returned an incomplete evaluation response.");

        foreach (var question in questions)
        {
            if (!result.Answers.TryGetValue(question.Key, out var answer) || answer is null ||
                (question.Value is NoulQuestion && answer is not NoulAnswer) ||
                (question.Value is ChoiceQuestion && answer is not ChoiceAnswer) ||
                (question.Value is ScoreQuestion && answer is not ScoreAnswer) ||
                (question.Value is BoxQuestion && answer is not BoxAnswer) ||
                (answer is ChoiceAnswer choice && (string.IsNullOrWhiteSpace(choice.Choice) || choice.Probabilities is null)) ||
                (answer is ScoreAnswer score && (score.Legend is null || score.Probabilities is null)) ||
                (answer is BoxAnswer box && !IsValidBox(box)))
                throw new JsonException($"The decision API returned a missing or mismatched answer for '{question.Key}'.");
        }

        return result;
    }

    private static bool IsValidBox(BoxAnswer answer)
        => answer.Box is { } box && IsUnitInterval(answer.Confidence) &&
           IsUnitInterval(box.Top) && IsUnitInterval(box.Bottom) &&
           IsUnitInterval(box.Left) && IsUnitInterval(box.Right) &&
           box.Top < box.Bottom && box.Left < box.Right;

    private static bool IsUnitInterval(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
