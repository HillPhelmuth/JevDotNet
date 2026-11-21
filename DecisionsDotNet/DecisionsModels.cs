using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace DecisionsDotNet;

/// <summary>A typed decisions request accepted by every provider client. The model ID is provider specific.</summary>
public sealed record DecisionsRequest
{
    /// <summary>Creates a request.</summary>
    /// <param name="state">The text, structured data, or images the questions are about.</param>
    /// <param name="questions">At least one question, keyed by the ID its answer is returned under.</param>
    /// <param name="model">The provider-specific model ID, such as <c>jev-latest</c> for TypeSafe or <c>typesafe/jev-1.14</c> for OpenRouter.</param>
    public DecisionsRequest(DecisionsValue state, IReadOnlyDictionary<string, DecisionsQuestion> questions, string model)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0 || questions.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("At least one named question is required.", nameof(questions));
        Questions = new Dictionary<string, DecisionsQuestion>(questions);
        Model = !string.IsNullOrWhiteSpace(model) ? model : throw new ArgumentException("A model is required.", nameof(model));
    }

    /// <summary>The text, structured data, or images the questions are about.</summary>
    [JsonPropertyName("state")]
    public DecisionsValue State { get; }

    /// <summary>The provider-specific model ID.</summary>
    [JsonPropertyName("model")]
    public string Model { get; }

    /// <summary>The questions, keyed by the ID each answer is returned under.</summary>
    [JsonPropertyName("questions")]
    public IReadOnlyDictionary<string, DecisionsQuestion> Questions { get; }

    /// <summary>Starts a request with fluent question methods.</summary>
    public static DecisionsRequestBuilder Create(DecisionsValue state) => new(state);
}

/// <summary>The result of one evaluation: one answer per question, the model that answered, and token usage.</summary>
public sealed record DecisionsResponse
{
    /// <summary>The provider's ID for the evaluation. Null when the provider sends none: direct TypeSafe, Microsoft Foundry, and Azure OpenAI responses.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The provider that answered. Null for direct TypeSafe responses.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>The model that answered, which can be more specific than the alias requested.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>One answer per question, keyed by question ID.</summary>
    [JsonPropertyName("answers")]
    public required IReadOnlyDictionary<string, DecisionsAnswer> Answers { get; init; }

    /// <summary>Token counts and, where the provider reports it, cost.</summary>
    [JsonPropertyName("usage")]
    public required DecisionsUsage Usage { get; init; }

    /// <summary>Reads the answer to a question as its concrete type.</summary>
    /// <exception cref="KeyNotFoundException">No answer was returned for the question.</exception>
    /// <exception cref="InvalidOperationException">The answer is of another type, such as a refusal.</exception>
    public TAnswer GetAnswer<TAnswer>(string questionId) where TAnswer : DecisionsAnswer
    {
        if (!Answers.TryGetValue(questionId, out var answer))
            throw new KeyNotFoundException($"No answer was returned for question '{questionId}'.");
        return answer as TAnswer ?? throw new InvalidOperationException(
            $"Answer '{questionId}' is {answer.GetType().Name}, not {typeof(TAnswer).Name}.");
    }

    /// <summary>Reads the answer for a key returned by <see cref="DecisionsRequestBuilder"/>.</summary>
    public TAnswer Get<TAnswer>(DecisionKey<TAnswer> key) where TAnswer : DecisionsAnswer => GetAnswer<TAnswer>(key.Id);

    /// <summary>Reads the answer for a key returned by <see cref="DecisionsRequestBuilder"/>, or returns false when it is absent or of another type, such as a refusal.</summary>
    public bool TryGet<TAnswer>(DecisionKey<TAnswer> key, [NotNullWhen(true)] out TAnswer? answer) where TAnswer : DecisionsAnswer
        => TryGetAnswer(key.Id, out answer);

    /// <summary>Reads the answer to a question, or returns false when it is absent or of another type, such as a refusal.</summary>
    public bool TryGetAnswer<TAnswer>(string questionId, [NotNullWhen(true)] out TAnswer? answer) where TAnswer : DecisionsAnswer
    {
        answer = Answers.TryGetValue(questionId, out var value) ? value as TAnswer : null;
        return answer is not null;
    }
}

/// <summary>Token counts for an evaluation.</summary>
public sealed record DecisionsUsage
{
    /// <summary>The cost the provider reports. Null when it reports none, as with direct TypeSafe responses.</summary>
    [JsonPropertyName("cost")]
    public decimal? Cost { get; init; }

    /// <summary>Input tokens billed.</summary>
    [JsonPropertyName("input_tokens")]
    public required int InputTokens { get; init; }

    /// <summary>Output tokens billed.</summary>
    [JsonPropertyName("output_tokens")]
    public required int OutputTokens { get; init; }
}

/// <summary>The answer to one question. Its concrete type matches the question, except that OpenAI can return a <see cref="RefusalAnswer"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulAnswer), "noul")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
[JsonDerivedType(typeof(BoxAnswer), "box")]
[JsonDerivedType(typeof(RefusalAnswer), "refusal")]
public abstract record DecisionsAnswer;

/// <summary>The model declined to answer this question. Only the OpenAI Decisions API returns refusals; other answers in the response can still be present.</summary>
public sealed record RefusalAnswer : DecisionsAnswer;

/// <summary>A single bounding box with confidence from zero to one.</summary>
public sealed record BoxAnswer : DecisionsAnswer
{
    /// <summary>The located rectangle. One is always returned, even when the target is absent.</summary>
    [JsonPropertyName("box")]
    public required BoundingBox Box { get; init; }

    /// <summary>The model's confidence in the box, from zero to one.</summary>
    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }
}

/// <summary>Image edges as fractions from zero to one, measured from the top-left corner.</summary>
public sealed record BoundingBox
{
    /// <summary>The top edge.</summary>
    [JsonPropertyName("top")]
    public required double Top { get; init; }

    /// <summary>The bottom edge.</summary>
    [JsonPropertyName("bottom")]
    public required double Bottom { get; init; }

    /// <summary>The left edge.</summary>
    [JsonPropertyName("left")]
    public required double Left { get; init; }

    /// <summary>The right edge.</summary>
    [JsonPropertyName("right")]
    public required double Right { get; init; }
}

/// <summary>The answer to a <see cref="NoulQuestion"/>.</summary>
public sealed record NoulAnswer : DecisionsAnswer
{
    /// <summary>The probability, from zero to one, that the answer is yes.</summary>
    [JsonPropertyName("noul")]
    public required double Noul { get; init; }
}

/// <summary>The answer to a <see cref="ChoiceQuestion"/>.</summary>
public sealed record ChoiceAnswer : DecisionsAnswer
{
    /// <summary>The most likely option.</summary>
    [JsonPropertyName("choice")]
    public required string Choice { get; init; }

    /// <summary>The probability of each option, keyed by option name.</summary>
    [JsonPropertyName("probabilities")]
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    /// <summary>The confidence in the chosen option, from zero to one.</summary>
    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }
}

/// <summary>The answer to a <see cref="ScoreQuestion"/>.</summary>
public sealed record ScoreAnswer : DecisionsAnswer
{
    /// <summary>The probability-weighted level, counted from zero for the lowest level, so it can be fractional.</summary>
    [JsonPropertyName("score")]
    public required double Score { get; init; }

    /// <summary>The level descriptions, keyed by zero-based level index.</summary>
    [JsonPropertyName("legend")]
    public required IReadOnlyDictionary<string, string> Legend { get; init; }

    /// <summary>The probability of each level, keyed by its zero-based index.</summary>
    [JsonPropertyName("probabilities")]
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    /// <summary>The confidence in the score, from zero to one.</summary>
    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }
}

/// <summary>The models available to a TypeSafe account.</summary>
public sealed record TypeSafeModelList
{
    /// <summary>The models and aliases, as TypeSafe lists them.</summary>
    [JsonPropertyName("models")]
    public required IReadOnlyList<TypeSafeModel> Models { get; init; }
}

/// <summary>A model or alias on the direct TypeSafe API.</summary>
public sealed record TypeSafeModel
{
    /// <summary>The model ID to pass as the request model.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>TypeSafe's description of the model.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>The release date, as TypeSafe formats it.</summary>
    [JsonPropertyName("release_date")]
    public required string ReleaseDate { get; init; }
}

/// <summary>A model from OpenRouter's public Decisions catalog.</summary>
public sealed record OpenRouterDecisionsModel
{
    /// <summary>The model ID to pass as the request model.</summary>
    public required string Id { get; init; }
    /// <summary>OpenRouter's pinned slug for the model, when listed.</summary>
    public string? CanonicalSlug { get; init; }
    /// <summary>The display name.</summary>
    public string? Name { get; init; }
    /// <summary>OpenRouter's description of the model.</summary>
    public string? Description { get; init; }
    /// <summary>When the model was added to OpenRouter, when listed.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>Advertised input modalities, such as text and image. Empty when the catalog omits them.</summary>
    public IReadOnlyList<string> InputModalities { get; init; } = [];

    /// <summary>True when the model advertises image input.</summary>
    public bool SupportsImageInput => InputModalities.Contains("image", StringComparer.Ordinal);
}
