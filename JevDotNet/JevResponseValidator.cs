using System.Text.Json;

namespace JevDotNet;

internal static class JevResponseValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JevResponse DeserializeAndValidate(string json, IReadOnlyDictionary<string, JevQuestion> questions)
    {
        var result = JsonSerializer.Deserialize<JevResponse>(json, JsonOptions)
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
                (answer is ChoiceAnswer choice && (string.IsNullOrWhiteSpace(choice.Choice) || choice.Probabilities is null)) ||
                (answer is ScoreAnswer score && (score.Legend is null || score.Probabilities is null)))
                throw new JsonException($"The decision API returned a missing or mismatched answer for '{question.Key}'.");
        }

        return result;
    }
}
