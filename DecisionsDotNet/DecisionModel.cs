using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace DecisionsDotNet;

#pragma warning disable DECISIONS002 // Decision sets are still experimental; DecideAsync uses them.

/// <summary>
/// A Decisions client bound to one provider-specific model, with single-question verbs that fit ordinary C# control flow.
/// Every verb is one network call; put cheap checks first so <c>&amp;&amp;</c> can skip it.
/// </summary>
/// <example>
/// <code>
/// var model = client.ForModel("typesafe/jev-1.14");
/// if (ticket.IsOpen &amp;&amp; await model.MatchesAsync(ticket.Text, "Is the customer asking for a refund?", minProbability: 0.75))
///     await QueueRefundReviewAsync(ticket);
/// </code>
/// </example>
public sealed class DecisionModel
{
    /// <summary>The question ID the single-question verbs send and read back.</summary>
    public const string QuestionId = "decision";

    /// <summary>Binds a client to a model. No network call is made.</summary>
    public DecisionModel(IDecisionsClient client, string modelId)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        ModelId = !string.IsNullOrWhiteSpace(modelId) ? modelId : throw new ArgumentException("A model ID is required.", nameof(modelId));
    }

    /// <summary>The client every verb calls.</summary>
    public IDecisionsClient Client { get; }

    /// <summary>The provider-specific model ID sent with every request.</summary>
    public string ModelId { get; }

    /// <summary>Builds the request with this model and evaluates it.</summary>
    public Task<DecisionsResponse> EvaluateAsync(DecisionsRequestBuilder request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Client.EvaluateAsync(request.Build(ModelId), cancellationToken);
    }

    /// <summary>Returns the probability, from zero to one, that the answer to a yes/no question is yes.</summary>
    /// <exception cref="DecisionRefusedException">The model declined to answer.</exception>
    public async Task<double> ProbabilityAsync(DecisionsValue input, DecisionsValue instructions,
        NoulCriteria? criteria = null, CancellationToken cancellationToken = default)
    {
        var response = await AskAsync(input, new NoulQuestion(instructions, criteria), cancellationToken).ConfigureAwait(false);
        return DecisionInterpretation.Noul(response, QuestionId).Noul;
    }

    /// <summary>Returns true when the probability of yes is at least <paramref name="minProbability"/>.</summary>
    /// <param name="input">The text, data, or images to judge.</param>
    /// <param name="instructions">A yes/no question about the input.</param>
    /// <param name="minProbability">Your threshold, from zero to one. Pick it for the cost of a false yes versus a false no.</param>
    /// <param name="criteria">Optional descriptions of what counts as yes and as no.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minProbability"/> is not a finite value from 0 to 1. No request is sent.</exception>
    /// <exception cref="DecisionRefusedException">The model declined to answer. A refusal is never reported as false.</exception>
    public async Task<bool> MatchesAsync(DecisionsValue input, DecisionsValue instructions, double minProbability,
        NoulCriteria? criteria = null, CancellationToken cancellationToken = default)
    {
        DecisionGuard.Probability(minProbability, nameof(minProbability));
        return await ProbabilityAsync(input, instructions, criteria, cancellationToken).ConfigureAwait(false) >= minProbability;
    }

    /// <summary>Answers a yes/no question as Yes, No, Unsure, or Refused.</summary>
    /// <param name="input">The text, data, or images to judge.</param>
    /// <param name="instructions">A yes/no question about the input.</param>
    /// <param name="unsureBand">Probabilities from <c>Low</c> to <c>High</c>, inclusive, are Unsure; above is Yes and below is No.</param>
    /// <param name="criteria">Optional descriptions of what counts as yes and as no.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentOutOfRangeException">The band is not <c>0 &lt;= Low &lt;= High &lt;= 1</c>. No request is sent.</exception>
    public async Task<DecisionVerdict> VerdictAsync(DecisionsValue input, DecisionsValue instructions,
        (double Low, double High) unsureBand, NoulCriteria? criteria = null, CancellationToken cancellationToken = default)
    {
        DecisionGuard.UnsureBand(unsureBand.Low, unsureBand.High, nameof(unsureBand));
        var response = await AskAsync(input, new NoulQuestion(instructions, criteria), cancellationToken).ConfigureAwait(false);
        return DecisionInterpretation.Verdict(response, QuestionId, unsureBand.Low, unsureBand.High);
    }

    /// <summary>
    /// Chooses one member of <typeparamref name="TEnum"/>. Member names are the options and a <see cref="System.ComponentModel.DescriptionAttribute"/>
    /// on a member describes when it applies. Include a fallback member such as <c>Other</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two members share a value, so an answer could not map back to one member.</exception>
    /// <exception cref="DecisionRefusedException">The model declined to answer.</exception>
    public Task<DecisionChoice<TEnum>> ChooseAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
        DecisionsValue input, DecisionsValue instructions, CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
    {
        var members = DecisionEnum<TEnum>.Members;
        return ChooseCoreAsync(input, instructions, members.Select(member => (member.Value, Criterion(member.Description))).ToArray(),
            cancellationToken);
    }

    /// <summary>
    /// Chooses one of the given enum members, each with its own criterion. Use it to offer a subset of an enum,
    /// to vary the criteria by context, or for enums you cannot annotate.
    /// </summary>
    /// <param name="input">The text, data, or images to judge.</param>
    /// <param name="instructions">What to decide, phrased as a question.</param>
    /// <param name="criteria">The members to offer, each with a description of when it applies, or null when the name is enough.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<DecisionChoice<TEnum>> ChooseAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
        DecisionsValue input, DecisionsValue instructions, IReadOnlyDictionary<TEnum, DecisionsValue?> criteria,
        CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return ChooseCoreAsync(input, instructions, criteria.Select(pair => (pair.Key, pair.Value)).ToArray(), cancellationToken);
    }

    /// <summary>
    /// Rates the input on the members of <typeparamref name="TEnum"/>, ordered by value from lowest to highest.
    /// A <see cref="System.ComponentModel.DescriptionAttribute"/> on a member describes the level; otherwise its name is used.
    /// </summary>
    /// <exception cref="ArgumentException">The enum has fewer than 2 or more than 10 members.</exception>
    /// <exception cref="DecisionRefusedException">The model declined to answer.</exception>
    public Task<DecisionScore<TEnum>> ScoreAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
        DecisionsValue input, DecisionsValue instructions, CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
        => ScoreCoreAsync(input, instructions,
            DecisionEnum<TEnum>.Members.Select(member => (member.Value, (DecisionsValue)(member.Description ?? member.Label))).ToArray(),
            cancellationToken);

    /// <summary>Rates the input on the given enum members, ordered by value from lowest to highest, each with its own level description.</summary>
    public Task<DecisionScore<TEnum>> ScoreAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
        DecisionsValue input, DecisionsValue instructions, IReadOnlyDictionary<TEnum, DecisionsValue> levels,
        CancellationToken cancellationToken = default)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(levels);
        return ScoreCoreAsync(input, instructions,
            levels.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToArray(), cancellationToken);
    }

    /// <summary>Rates the input on 2 to 10 levels described in text, lowest first.</summary>
    /// <exception cref="DecisionRefusedException">The model declined to answer.</exception>
    public async Task<DecisionScore> ScoreAsync(DecisionsValue input, DecisionsValue instructions,
        IReadOnlyList<DecisionsValue> levels, CancellationToken cancellationToken = default)
    {
        var question = new ScoreQuestion(instructions, levels);
        var response = await AskAsync(input, question, cancellationToken).ConfigureAwait(false);
        return DecisionInterpretation.Score(response, QuestionId, question.Criteria.Count);
    }

    /// <summary>
    /// Asks every question declared on <typeparamref name="T"/> about the input in one request, and returns a populated instance.
    /// Mark properties with <see cref="NoulAttribute"/>, <see cref="ChoiceAttribute"/>, or <see cref="ScoreAttribute"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not a valid decision set. No request is sent.</exception>
    /// <exception cref="DecisionRefusedException">The model refused a question whose property cannot hold a refusal; only <see cref="DecisionVerdict"/> can.</exception>
    [Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
    [RequiresUnreferencedCode(DecisionSets.TrimMessage)]
    [RequiresDynamicCode(DecisionSets.AotMessage)]
    public async Task<T> DecideAsync<[DynamicallyAccessedMembers(DecisionSets.Members)] T>(DecisionsValue input,
        CancellationToken cancellationToken = default) where T : class
        => (await DecideWithResponseAsync<T>(input, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Like <see cref="DecideAsync{T}"/>, and also returns the response, for logging and usage accounting.</summary>
    [Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
    [RequiresUnreferencedCode(DecisionSets.TrimMessage)]
    [RequiresDynamicCode(DecisionSets.AotMessage)]
    public async Task<DecisionSetResult<T>> DecideWithResponseAsync<[DynamicallyAccessedMembers(DecisionSets.Members)] T>(
        DecisionsValue input, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(input);
        var plan = DecisionSetPlan.For(typeof(T));
        var response = await Client.EvaluateAsync(new DecisionsRequest(input, plan.Questions, ModelId), cancellationToken)
            .ConfigureAwait(false);
        return new((T)plan.Read(response), response);
    }

    private async Task<DecisionChoice<TEnum>> ChooseCoreAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
        DecisionsValue input, DecisionsValue instructions, (TEnum Value, DecisionsValue? Criterion)[] options,
        CancellationToken cancellationToken)
        where TEnum : struct, Enum
    {
        var criteria = new Dictionary<string, DecisionsValue?>(StringComparer.Ordinal);
        foreach (var (value, criterion) in options)
            criteria.Add(DecisionEnum<TEnum>.Label(value), criterion);
        var response = await AskAsync(input, new ChoiceQuestion(instructions, criteria), cancellationToken).ConfigureAwait(false);
        return DecisionInterpretation.Choice(response, QuestionId, options.Select(option => option.Value).ToHashSet());
    }

    private async Task<DecisionScore<TEnum>> ScoreCoreAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
        DecisionsValue input, DecisionsValue instructions, (TEnum Value, DecisionsValue Level)[] levels,
        CancellationToken cancellationToken)
        where TEnum : struct, Enum
    {
        foreach (var (value, _) in levels) DecisionEnum<TEnum>.Label(value);
        var question = new ScoreQuestion(instructions, levels.Select(level => level.Level).ToArray());
        var response = await AskAsync(input, question, cancellationToken).ConfigureAwait(false);
        return DecisionInterpretation.Score(response, QuestionId, levels.Select(level => level.Value).ToArray());
    }

    private Task<DecisionsResponse> AskAsync(DecisionsValue input, DecisionsQuestion question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var request = new DecisionsRequest(input, new Dictionary<string, DecisionsQuestion> { [QuestionId] = question }, ModelId);
        return Client.EvaluateAsync(request, cancellationToken);
    }

    private static DecisionsValue? Criterion(string? description) => description is null ? null : DecisionsValue.Text(description);
}

/// <summary>Binds Decisions clients to models for the conditional verbs on <see cref="DecisionModel"/>.</summary>
public static class DecisionModelExtensions
{
    /// <summary>Binds the client to a provider-specific model ID. No network call is made.</summary>
    public static DecisionModel ForModel(this IDecisionsClient client, string modelId) => new(client, modelId);

    /// <summary>
    /// Registers a <see cref="DecisionModel"/> that binds the registered <see cref="IDecisionsClient"/> to <paramref name="modelId"/>,
    /// so it can be injected directly. Requests built by hand still name their model.
    /// </summary>
    public static IServiceCollection AddDecisionModel(this IServiceCollection services, string modelId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return services.AddTransient(provider => provider.GetRequiredService<IDecisionsClient>().ForModel(modelId));
    }

    /// <summary>
    /// Registers a keyed <see cref="DecisionModel"/> for <paramref name="modelId"/>. It uses the keyed client registered under
    /// <paramref name="clientServiceKey"/>, or the unkeyed <see cref="IDecisionsClient"/> when that is null.
    /// </summary>
    public static IServiceCollection AddKeyedDecisionModel(this IServiceCollection services, object serviceKey, string modelId,
        object? clientServiceKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return services.AddKeyedTransient(serviceKey, (provider, _) =>
            (clientServiceKey is null
                ? provider.GetRequiredService<IDecisionsClient>()
                : provider.GetRequiredKeyedService<IDecisionsClient>(clientServiceKey)).ForModel(modelId));
    }
}
