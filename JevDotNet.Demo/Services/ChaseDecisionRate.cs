namespace JevDotNet.Demo.Services;

/// <summary>Average completed decisions per paced chaser turn, excluding paused time.</summary>
public sealed class ChaseDecisionRate
{
    public static readonly TimeSpan TurnInterval = TimeSpan.FromMilliseconds(400);

    private double _pacedSeconds;

    public int CompletedDecisions { get; private set; }
    public double DecisionsPerSecond => CompletedDecisions == 0 ? 0 : CompletedDecisions / _pacedSeconds;

    public void Record(TimeSpan turnElapsed)
    {
        if (turnElapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(turnElapsed));

        _pacedSeconds += Math.Max(TurnInterval.TotalSeconds, turnElapsed.TotalSeconds);
        CompletedDecisions++;
    }
}
