using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace DecisionsDotNet;

/// <summary>A typed Jev decisions request accepted by both direct TypeSafe and OpenRouter clients.</summary>
public sealed record DecisionsRequest
{
    public DecisionsRequest(DecisionsValue state, IReadOnlyDictionary<string, DecisionsQuestion> questions, string model = "jev-latest")
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0 || questions.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("At least one named question is required.", nameof(questions));
        Questions = new Dictionary<string, DecisionsQuestion>(questions);
        Model = !string.IsNullOrWhiteSpace(model) ? model : throw new ArgumentException("A model is required.", nameof(model));
    }

    [JsonPropertyName("state")]
    public DecisionsValue State { get; }

    [JsonPropertyName("model")]
    public string Model { get; }

    [JsonPropertyName("questions")]
    public IReadOnlyDictionary<string, DecisionsQuestion> Questions { get; }

    /// <summary>Starts a request with fluent question methods.</summary>
    public static DecisionsRequestBuilder Create(DecisionsValue state) => new(state);
}

public sealed record DecisionsResponse
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
    public required DecisionsUsage Usage { get; init; }

    public TAnswer GetAnswer<TAnswer>(string questionId) where TAnswer : JevAnswer
    {
        if (!Answers.TryGetValue(questionId, out var answer))
            throw new KeyNotFoundException($"No answer was returned for question '{questionId}'.");
        return answer as TAnswer ?? throw new InvalidOperationException(
            $"Answer '{questionId}' is {answer.GetType().Name}, not {typeof(TAnswer).Name}.");
    }

    /// <summary>Reads the answer for a key returned by <see cref="DecisionsRequestBuilder"/>.</summary>
    public TAnswer Get<TAnswer>(DecisionKey<TAnswer> key) where TAnswer : JevAnswer => GetAnswer<TAnswer>(key.Id);

    public bool TryGetAnswer<TAnswer>(string questionId, [NotNullWhen(true)] out TAnswer? answer) where TAnswer : JevAnswer
    {
        answer = Answers.TryGetValue(questionId, out var value) ? value as TAnswer : null;
        return answer is not null;
    }
}

public sealed record DecisionsUsage
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
[JsonDerivedType(typeof(BoxAnswer), "box")]
[JsonDerivedType(typeof(RefusalAnswer), "refusal")]
public abstract record JevAnswer;

/// <summary>The model declined to answer this question. Only the OpenAI Decisions API returns refusals; other answers in the response can still be present.</summary>
public sealed record RefusalAnswer : JevAnswer;

/// <summary>A single bounding box with confidence from zero to one.</summary>
public sealed record BoxAnswer : JevAnswer
{
    [JsonPropertyName("box")]
    public required BoundingBox Box { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }
}

/// <summary>Image edges as fractions from zero to one, measured from the top-left corner.</summary>
public sealed record BoundingBox
{
    [JsonPropertyName("top")]
    public required double Top { get; init; }

    [JsonPropertyName("bottom")]
    public required double Bottom { get; init; }

    [JsonPropertyName("left")]
    public required double Left { get; init; }

    [JsonPropertyName("right")]
    public required double Right { get; init; }
}

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

/// <summary>A model from OpenRouter's public Decisions catalog.</summary>
public sealed record OpenRouterDecisionsModel
{
    public required string Id { get; init; }
    public string? CanonicalSlug { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? Created { get; init; }

    /// <summary>Advertised input modalities, such as text and image. Empty when the catalog omits them.</summary>
    public IReadOnlyList<string> InputModalities { get; init; } = [];

    public bool SupportsImageInput => InputModalities.Contains("image", StringComparer.Ordinal);
}
