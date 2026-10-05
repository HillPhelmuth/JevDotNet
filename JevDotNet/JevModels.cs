using System.Text.Json.Serialization;

namespace JevDotNet;

/// <summary>A typed Jev decisions request accepted by both direct TypeSafe and OpenRouter clients.</summary>
public sealed record DecisionsRequest
{
    public DecisionsRequest(JevValue state, IReadOnlyDictionary<string, JevQuestion> questions, string model = "jev-latest")
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0 || questions.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("At least one named question is required.", nameof(questions));
        Questions = new Dictionary<string, JevQuestion>(questions);
        Model = !string.IsNullOrWhiteSpace(model) ? model : throw new ArgumentException("A model is required.", nameof(model));
    }

    [JsonPropertyName("state")]
    public JevValue State { get; }

    [JsonPropertyName("model")]
    public string Model { get; }

    [JsonPropertyName("questions")]
    public IReadOnlyDictionary<string, JevQuestion> Questions { get; }
}

public sealed record JevResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("answers")]
    public required IReadOnlyDictionary<string, JevAnswer> Answers { get; init; }

    [JsonPropertyName("usage")]
    public required JevUsage Usage { get; init; }

    public TAnswer GetAnswer<TAnswer>(string questionId) where TAnswer : JevAnswer
    {
        if (!Answers.TryGetValue(questionId, out var answer))
            throw new KeyNotFoundException($"No answer was returned for question '{questionId}'.");
        return answer as TAnswer ?? throw new InvalidOperationException(
            $"Answer '{questionId}' is {answer.GetType().Name}, not {typeof(TAnswer).Name}.");
    }
}

public sealed record JevUsage
{
    [JsonPropertyName("cost")]
    public decimal? Cost { get; init; }

    [JsonPropertyName("input_tokens")]
    public required int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public required int OutputTokens { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulAnswer), "noul")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
public abstract record JevAnswer;

public sealed record NoulAnswer : JevAnswer
{
    [JsonPropertyName("noul")]
    public required double Noul { get; init; }
}

public sealed record ChoiceAnswer : JevAnswer
{
    [JsonPropertyName("choice")]
    public required string Choice { get; init; }

    [JsonPropertyName("probabilities")]
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }
}

public sealed record ScoreAnswer : JevAnswer
{
    [JsonPropertyName("score")]
    public required double Score { get; init; }

    [JsonPropertyName("legend")]
    public required IReadOnlyDictionary<string, string> Legend { get; init; }

    [JsonPropertyName("probabilities")]
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }
}

public sealed record JevModelList
{
    [JsonPropertyName("models")]
    public required IReadOnlyList<JevModel> Models { get; init; }
}

public sealed record JevModel
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("release_date")]
    public required string ReleaseDate { get; init; }
}
