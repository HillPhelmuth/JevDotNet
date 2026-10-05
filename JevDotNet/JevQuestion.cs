using System.Text.Json.Serialization;

namespace JevDotNet;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
public abstract record JevQuestion
{
    protected JevQuestion(JevValue instructions) => Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));

    [JsonPropertyName("instructions")]
    public JevValue Instructions { get; }
}

public sealed record NoulQuestion : JevQuestion
{
    public NoulQuestion(JevValue instructions, NoulCriteria? criteria = null) : base(instructions) => Criteria = criteria;

    [JsonPropertyName("criteria")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NoulCriteria? Criteria { get; }
}

public sealed record NoulCriteria(
    [property: JsonPropertyName("true"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JevValue? True = null,
    [property: JsonPropertyName("false"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JevValue? False = null);

public sealed record ChoiceQuestion : JevQuestion
{
    public ChoiceQuestion(JevValue instructions, IReadOnlyDictionary<string, JevValue?> criteria) : base(instructions)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 1 or > 255 || criteria.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choice criteria must contain 1 to 255 named options.", nameof(criteria));
        Criteria = new Dictionary<string, JevValue?>(criteria);
    }

    [JsonPropertyName("criteria")]
    public IReadOnlyDictionary<string, JevValue?> Criteria { get; }
}

public sealed record ScoreQuestion : JevQuestion
{
    public ScoreQuestion(JevValue instructions, IReadOnlyList<JevValue> criteria) : base(instructions)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 2 or > 10 || criteria.Any(value => value is null))
            throw new ArgumentException("Score criteria must contain 2 to 10 levels.", nameof(criteria));
        Criteria = criteria.ToArray();
    }

    [JsonPropertyName("criteria")]
    public IReadOnlyList<JevValue> Criteria { get; }
}
