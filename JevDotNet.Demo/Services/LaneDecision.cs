using JevDotNet;

namespace JevDotNet.Demo.Services;

public sealed record LaneObstacle(int Lane, double TimeToCollisionMs);
public sealed record LaneSnapshot(int SessionId, bool Running, bool Crashed, double CarLane, int Passed, double ElapsedMs, LaneObstacle[] Obstacles);
public sealed record LaneEvent(int SessionId, string Reason, LaneSnapshot Snapshot);

public static class LaneDecision
{
    public const int MaxDecisions = 1000;
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(30);

    public static DecisionsRequest BuildRequest(LaneSnapshot snapshot, string model)
    {

        //var state = new
        //{
        //    current_lane = snapshot.CarLane,
        //    lanes = "Lane 1 is left, lane 2 is center, lane 3 is right. You can cross intermediate lanes.",
        //    obstacles = snapshot.Obstacles.Select(o => new { lane = o.Lane, time_to_collision_ms = Math.Round(o.TimeToCollisionMs) }),
        //    goal = "Avoid collisions with obstacles. Prefer staying in the current lane when it has no obstacles. The road keeps moving while you decide."
        //};
        var stateAsMarkdown = 
            $"""
            Current lane: {snapshot.CarLane}
            Lanes: Lane 1 is left, lane 2 is center, lane 3 is right. You can cross intermediate lanes.
            Obstacles:\n {string.Join("\n- ", snapshot.Obstacles.Select(o => $"(lane {o.Lane}, time to collision {Math.Round(o.TimeToCollisionMs)} ms)"))}

            Goal: Avoid collisions with obstacles. Prefer staying in the current lane when it has no obstacles. The road keeps moving while you decide.
            """;
        return new(JevValue.Text(stateAsMarkdown), new Dictionary<string, JevQuestion>
        {
            ["lane"] = new ChoiceQuestion("Which lane should the car choose?", new Dictionary<string, JevValue?>
            {
                ["lane_1"] = "Drive in the left lane.",
                ["lane_2"] = "Drive in the center lane.",
                ["lane_3"] = "Drive in the right lane."
            })
        }, DemoDecisionModels.RequireSupported(model));
    }

    public static int Validate(ChoiceAnswer answer)
    {
        var lane = answer.Choice switch { "lane_1" => 1, "lane_2" => 2, "lane_3" => 3, _ => 0 };
        if (lane == 0 || answer.Probabilities.Count != 3 ||
            Enumerable.Range(1, 3).Any(i => !answer.Probabilities.TryGetValue($"lane_{i}", out var p) || !double.IsFinite(p) || p < 0 || p > 1) ||
            Math.Abs(answer.Probabilities.Values.Sum() - 1) > 0.02)
            throw new InvalidOperationException("The model returned an invalid lane choice or probability distribution.");
        return lane;
    }
}
