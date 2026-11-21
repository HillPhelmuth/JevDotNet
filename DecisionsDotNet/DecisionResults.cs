using System.Diagnostics.CodeAnalysis;

namespace DecisionsDotNet;

/// <summary>The outcome of a <see cref="DecisionModel.VerdictAsync"/> call.</summary>
public enum VerdictOutcome
{
    /// <summary>The probability is above the unsure band.</summary>
    Yes,
    /// <summary>The probability is below the unsure band.</summary>
    No,
    /// <summary>The probability falls inside the unsure band, so neither branch is safe to take automatically.</summary>
    Unsure,
    /// <summary>The model declined to answer. <see cref="DecisionVerdict.Probability"/> and <see cref="DecisionVerdict.Answer"/> are null.</summary>
    Refused,
}

/// <summary>A three-valued answer to a yes/no question, with the evidence behind it.</summary>
public sealed record DecisionVerdict
{
    internal DecisionVerdict(VerdictOutcome outcome, NoulAnswer? answer, DecisionsResponse response)
    {
        Outcome = outcome;
        Answer = answer;
        Response = response;
    }

    /// <summary>Yes, No, Unsure, or Refused.</summary>
    public VerdictOutcome Outcome { get; }

    /// <summary>The probability, from zero to one, that the answer is yes. Null when the model refused.</summary>
    public double? Probability => Answer?.Noul;

    /// <summary>The raw answer. Null when the model refused.</summary>
    public NoulAnswer? Answer { get; }

    /// <summary>The full response, with the model that answered and token usage.</summary>
    public DecisionsResponse Response { get; }

    /// <summary>Classifies a probability against an unsure band, where <paramref name="low"/> and <paramref name="high"/> are themselves unsure.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The band is not <c>0 &lt;= low &lt;= high &lt;= 1</c>, or the probability is not finite.</exception>
    public static VerdictOutcome Classify(double probability, double low, double high)
    {
        DecisionGuard.UnsureBand(low, high);
        if (!double.IsFinite(probability) || probability is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(probability), probability, "A probability must be a finite value from 0 to 1.");
        return probability > high ? VerdictOutcome.Yes : probability < low ? VerdictOutcome.No : VerdictOutcome.Unsure;
    }
}

/// <summary>An enum option chosen by the model, with the evidence behind it.</summary>
/// <typeparam name="TEnum">The enum whose members were offered.</typeparam>
public sealed record DecisionChoice<TEnum> where TEnum : struct, Enum
{
    internal DecisionChoice(TEnum value, double selectedProbability, IReadOnlyDictionary<TEnum, double> probabilities,
        ChoiceAnswer answer, DecisionsResponse response)
    {
        Value = value;
        SelectedProbability = selectedProbability;
        Probabilities = probabilities;
        Answer = answer;
        Response = response;
    }

    /// <summary>The most likely option.</summary>
    public TEnum Value { get; }

    /// <summary>The probability the model gave <see cref="Value"/>, from zero to one. Compare it with your own routing threshold.</summary>
    public double SelectedProbability { get; }

    /// <summary>The probability of each option offered, keyed by enum member.</summary>
    public IReadOnlyDictionary<TEnum, double> Probabilities { get; }

    /// <summary>The raw answer, with the wire labels.</summary>
    public ChoiceAnswer Answer { get; }

    /// <summary>The full response, with the model that answered and token usage.</summary>
    public DecisionsResponse Response { get; }
}

/// <summary>A rating on ordered enum levels, with the evidence behind it.</summary>
/// <typeparam name="TEnum">The enum whose members are the levels, lowest value first.</typeparam>
public sealed record DecisionScore<TEnum> where TEnum : struct, Enum
{
    internal DecisionScore(TEnum value, IReadOnlyList<TEnum> levels, ScoreAnswer answer, DecisionsResponse response)
    {
        Value = value;
        Levels = levels;
        Answer = answer;
        Response = response;
    }

    /// <summary>The level nearest the probability-weighted <see cref="Score"/>.</summary>
    public TEnum Value { get; }

    /// <summary>The probability-weighted level, counted from zero for the first entry of <see cref="Levels"/>, so it can be fractional.</summary>
    public double Score => Answer.Score;

    /// <summary>The levels offered, lowest first.</summary>
    public IReadOnlyList<TEnum> Levels { get; }

    /// <summary>The raw answer.</summary>
    public ScoreAnswer Answer { get; }

    /// <summary>The full response, with the model that answered and token usage.</summary>
    public DecisionsResponse Response { get; }
}

/// <summary>A rating on ordered levels described in text, with the evidence behind it.</summary>
public sealed record DecisionScore
{
    internal DecisionScore(int level, int levelCount, ScoreAnswer answer, DecisionsResponse response)
    {
        Level = level;
        LevelCount = levelCount;
        Answer = answer;
        Response = response;
    }

    /// <summary>The zero-based index of the level nearest the probability-weighted <see cref="Score"/>.</summary>
    public int Level { get; }

    /// <summary>The number of levels offered.</summary>
    public int LevelCount { get; }

    /// <summary>The probability-weighted level, counted from zero for the lowest level, so it can be fractional.</summary>
    public double Score => Answer.Score;

    /// <summary>The raw answer.</summary>
    public ScoreAnswer Answer { get; }

    /// <summary>The full response, with the model that answered and token usage.</summary>
    public DecisionsResponse Response { get; }
}

/// <summary>The model declined to answer a question whose verb has no way to return a refusal, such as <see cref="DecisionModel.MatchesAsync"/>.</summary>
/// <remarks>Use <see cref="DecisionModel.VerdictAsync"/> to receive refusals as <see cref="VerdictOutcome.Refused"/> instead.</remarks>
public sealed class DecisionRefusedException : Exception
{
    /// <summary>Creates the exception for a refused question.</summary>
    public DecisionRefusedException(string questionId, DecisionsResponse response)
        : base($"The model refused to answer question '{questionId}'.")
    {
        QuestionId = questionId ?? throw new ArgumentNullException(nameof(questionId));
        Response = response ?? throw new ArgumentNullException(nameof(response));
    }

    /// <summary>The ID of the refused question.</summary>
    public string QuestionId { get; }

    /// <summary>The full response, which can hold answers to other questions.</summary>
    public DecisionsResponse Response { get; }
}
