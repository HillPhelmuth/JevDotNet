using System.Text.Json;
using JevDotNet;
using JevDotNet.Demo.Services;

internal static class LaneChecks
{
    public static void Run()
    {
        var snapshot = new LaneSnapshot(1, true, false, 2, 0, 1000, [new(2, 1234.56)]);
        foreach (var model in DemoDecisionModels.All)
        {
            var request = LaneDecision.BuildRequest(snapshot, model.Id);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
            var root = json.RootElement;
            Check(request.Model == model.Id, "Selected lane model");
            Check(request.Questions.Count == 1, "Single lane question");
            var criteria = root.GetProperty("questions").GetProperty("lane").GetProperty("criteria");
            Check(criteria.EnumerateObject().Count() == 3 && criteria.TryGetProperty("lane_3", out _), "All three lanes available including blocked lanes");
            Check(root.GetProperty("state").GetProperty("obstacles")[0].GetProperty("distance_to_obstacle").GetDouble() == 1235, "Precomputed barrier timing");
        }
        try { LaneDecision.BuildRequest(snapshot, "unsupported"); throw new Exception("Accepted unknown model"); }
        catch (ArgumentException) { }
        foreach (var lane in new[] { 1, 2, 3 })
            Check(LaneDecision.Validate(Answer($"lane_{lane}", .2, .3, .5)) == lane, "Returned choice steers even when not largest probability");
        foreach (var invalid in new[] { Answer("other", .2, .3, .5), Answer("lane_1", double.NaN, .3, .5), Answer("lane_1", -.1, .6, .5), Answer("lane_1", .1, .1, .1), Answer("lane_1", 1.1, 0, 0), Answer("lane_1", .2, .3, .5) with { Probabilities = new Dictionary<string, double> { ["lane_1"] = 1 } } })
        {
            try { LaneDecision.Validate(invalid); throw new Exception("Accepted malformed lane response"); }
            catch (InvalidOperationException) { }
        }
        Check(LaneDecision.Interval.TotalMilliseconds == 500 && LaneDecision.MaxDecisions == 100, "Request and round bounds");
    }
    private static ChoiceAnswer Answer(string choice, double a, double b, double c) => new()
    {
        Choice = choice, Confidence = .5,
        Probabilities = new Dictionary<string, double> { ["lane_1"] = a, ["lane_2"] = b, ["lane_3"] = c }
    };
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
}
