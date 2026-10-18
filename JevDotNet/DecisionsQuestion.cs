using System.Text.Json.Serialization;

namespace DecisionsDotNet;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
[JsonDerivedType(typeof(BoxQuestion), "box")]
public abstract record DecisionsQuestion
{
    protected DecisionsQuestion(DecisionsValue instructions) => Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));

    [JsonPropertyName("instructions")]
    public DecisionsValue Instructions { get; }
}

public sealed record NoulQuestion : DecisionsQuestion
{
    public NoulQuestion(DecisionsValue instructions, NoulCriteria? criteria = null) : base(instructions) => Criteria = criteria;

    [JsonPropertyName("criteria")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NoulCriteria? Criteria { get; }
}

/// <summary>Locates one target in an image. Requires a model supporting Box questions, such as Jev 1.14.</summary>
public sealed record BoxQuestion : DecisionsQuestion
{
    public BoxQuestion(DecisionsValue instructions) : base(instructions) { }
}

public sealed record NoulCriteria(
    [property: JsonPropertyName("true"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DecisionsValue? True = null,
    [property: JsonPropertyName("false"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DecisionsValue? False = null);

public sealed record ChoiceQuestion : DecisionsQuestion
{
    public ChoiceQuestion(DecisionsValue instructions, IReadOnlyDictionary<string, DecisionsValue?> criteria) : base(instructions)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 1 or > 255 || criteria.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choice criteria must contain 1 to 255 named options.", nameof(criteria));
        Criteria = new Dictionary<string, DecisionsValue?>(criteria);
    }

    [JsonPropertyName("criteria")]
    public IReadOnlyDictionary<string, DecisionsValue?> Criteria { get; }
}

public sealed record ScoreQuestion : DecisionsQuestion
{
    public ScoreQuestion(DecisionsValue instructions, IReadOnlyList<DecisionsValue> criteria) : base(instructions)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 2 or > 10 || criteria.Any(value => value is null))
            throw new ArgumentException("Score criteria must contain 2 to 10 levels.", nameof(criteria));
        Criteria = criteria.ToArray();
    }

    [JsonPropertyName("criteria")]
    public IReadOnlyList<DecisionsValue> Criteria { get; }
}
