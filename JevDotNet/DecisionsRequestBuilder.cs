namespace DecisionsDotNet;

/// <summary>A question ID that remembers its answer type, for reading answers without casts.</summary>
public readonly record struct DecisionKey<TAnswer>(string Id) where TAnswer : JevAnswer
{
    public override string ToString() => Id;
}

/// <summary>Builds a <see cref="DecisionsRequest"/> one named question at a time.</summary>
/// <example>
/// <code>
/// var request = DecisionsRequest.Create("My card was charged twice.")
///     .Noul("urgent", "Does this need immediate attention?", out var urgent)
///     .Choice("team", "Which team should handle this?", ("billing", "Payments and refunds"), ("technical", "Bugs and outages"))
///     .Score("frustration", "How frustrated is the customer?", "Calm", "Frustrated", "Very angry")
///     .Build("typesafe/jev-1.14");
/// double urgency = response.Get(urgent).Noul;
/// </code>
/// </example>
public sealed class DecisionsRequestBuilder
{
    private readonly DecisionsValue _state;
    private readonly Dictionary<string, DecisionsQuestion> _questions = new(StringComparer.Ordinal);

    public DecisionsRequestBuilder(DecisionsValue state)
        => _state = state ?? throw new ArgumentNullException(nameof(state));

    /// <summary>The questions added so far, keyed by ID.</summary>
    public IReadOnlyDictionary<string, DecisionsQuestion> Questions => _questions;

    public DecisionsRequestBuilder Question(string id, DecisionsQuestion question)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A question ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(question);
        if (!_questions.TryAdd(id, question))
            throw new ArgumentException($"A question named '{id}' was already added.", nameof(id));
        return this;
    }

    /// <summary>Adds a yes/no question answered with a probability from zero to one.</summary>
    public DecisionsRequestBuilder Noul(string id, DecisionsValue instructions,
        DecisionsValue? whenTrue = null, DecisionsValue? whenFalse = null)
        => Question(id, new NoulQuestion(instructions,
            whenTrue is null && whenFalse is null ? null : new NoulCriteria(whenTrue, whenFalse)));

    public DecisionsRequestBuilder Noul(string id, DecisionsValue instructions, out DecisionKey<NoulAnswer> key,
        DecisionsValue? whenTrue = null, DecisionsValue? whenFalse = null)
    {
        key = new(id);
        return Noul(id, instructions, whenTrue, whenFalse);
    }

    /// <summary>Adds a choice among options that need no description.</summary>
    public DecisionsRequestBuilder Choice(string id, DecisionsValue instructions, params string[] options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Question(id, new ChoiceQuestion(instructions,
            options.ToDictionary(option => option, _ => (DecisionsValue?)null, StringComparer.Ordinal)));
    }

    public DecisionsRequestBuilder Choice(string id, DecisionsValue instructions, out DecisionKey<ChoiceAnswer> key,
        params string[] options)
    {
        key = new(id);
        return Choice(id, instructions, options);
    }

    /// <summary>Adds a choice among options, each with a description.</summary>
    public DecisionsRequestBuilder Choice(string id, DecisionsValue instructions,
        params (string Option, DecisionsValue? Criterion)[] options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var criteria = new Dictionary<string, DecisionsValue?>(StringComparer.Ordinal);
        foreach (var (option, criterion) in options)
        {
            if (option is null || !criteria.TryAdd(option, criterion))
                throw new ArgumentException("Choice options must be unique and non-null.", nameof(options));
        }
        return Question(id, new ChoiceQuestion(instructions, criteria));
    }

    public DecisionsRequestBuilder Choice(string id, DecisionsValue instructions, out DecisionKey<ChoiceAnswer> key,
        params (string Option, DecisionsValue? Criterion)[] options)
    {
        key = new(id);
        return Choice(id, instructions, options);
    }

    /// <summary>Adds a score across ordered levels, lowest first.</summary>
    public DecisionsRequestBuilder Score(string id, DecisionsValue instructions, params DecisionsValue[] levels)
        => Question(id, new ScoreQuestion(instructions, levels));

    public DecisionsRequestBuilder Score(string id, DecisionsValue instructions, out DecisionKey<ScoreAnswer> key,
        params DecisionsValue[] levels)
    {
        key = new(id);
        return Score(id, instructions, levels);
    }

    /// <summary>Adds a bounding-box question. Requires an image in the state and a model with Box support.</summary>
    public DecisionsRequestBuilder Box(string id, DecisionsValue instructions)
        => Question(id, new BoxQuestion(instructions));

    public DecisionsRequestBuilder Box(string id, DecisionsValue instructions, out DecisionKey<BoxAnswer> key)
    {
        key = new(id);
        return Box(id, instructions);
    }

    /// <summary>Creates the request. The builder can keep adding questions for later requests.</summary>
    public DecisionsRequest Build(string model = "jev-latest") => new(_state, _questions, model);
}
