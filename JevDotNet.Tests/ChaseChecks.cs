using System.Text.Json;
using JevDotNet;
using JevDotNet.Demo.Services;

internal static class ChaseChecks
{
    public static void Run()
    {
        var game = new ChaseGame();
        Check(game.Runner == new GridPosition(6, 6) && game.Chaser == new GridPosition(1, 1), "Initial pieces");
        Check(ChaseGame.ShortestDistance(game.Chaser, game.Runner) == 10, "Initial shortest path");
        Check(ChaseGame.IsWall(new GridPosition(2, 2)) && ChaseGame.IsWall(new GridPosition(5, 5)), "Wall map");

        var request = game.BuildRequest();
        Check(request.Model == DemoDecisionModels.DefaultModelId, "Default game model");
        foreach (var model in DemoDecisionModels.All)
            Check(game.BuildRequest(model.Id).Model == model.Id, $"Game selected {model.Id}");
        try
        {
            game.BuildRequest("unsupported/model");
            throw new Exception("Expected unsupported game model");
        }
        catch (ArgumentException) { }
        Check(request.Questions.Count == 1 && request.Questions["move"] is ChoiceQuestion, "One choice per chaser move");
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(request)))
        {
            var root = json.RootElement;
            var state = root.GetProperty("state").GetString()!;
            var criteria = root.GetProperty("questions").GetProperty("move").GetProperty("criteria");
            Check(state.Contains("column 6, row 6") && state.Contains("column 1, row 1") &&
                state.Contains("10 steps away") && state.Contains("column 3, row 4"), "Worded chase state");
            Check(criteria.EnumerateObject().Count() == 2 &&
                criteria.GetProperty("up").GetString() ==
                    "Chaser moves up to column 1, row 0. The runner is then 11 steps away around the walls." &&
                criteria.GetProperty("right").GetString() ==
                    "Chaser moves right to column 2, row 1. The runner is then 9 steps away around the walls.",
                "Original legal move descriptions and distances preserved");
        }

        var answer = new ChoiceAnswer
        {
            Choice = "up",
            Confidence = 0.8,
            Probabilities = new Dictionary<string, double> { ["up"] = 0.1, ["right"] = 0.9 }
        };
        var initialMove = game.ResolveMove(answer);
        Check(initialMove.Choice == "down", "Returned choice is ignored in favor of derived legal probabilities");
        Check(Math.Abs(initialMove.Probabilities["down"] - 0.9) < 0.001 &&
            Math.Abs(initialMove.Probabilities["left"] - 0.1) < 0.001, "Opposite probabilities are complements");
        var downGame = new ChaseGame();
        downGame.ApplyChaserMove("down");
        var wallCriteria = ((ChoiceQuestion)downGame.BuildRequest().Questions["move"]).Criteria;
        Check(wallCriteria.Count == 2 && wallCriteria["right"]!.ToJsonElement().GetString() ==
            "Chaser moves right to column 2, row 2. The runner is then 8 steps away around the walls.",
            "Wall destination retains concrete criterion without unavailable wording");
        var wallMove = downGame.ResolveMove(answer);
        Check(wallMove.Choice != "right", "Wall destination cannot be selected");
        downGame.ApplyChaserMove("down");
        var downCriteria = ((ChoiceQuestion)downGame.BuildRequest().Questions["move"]).Criteria;
        Check(downCriteria.Count == 2 && downCriteria.ContainsKey("up") && downCriteria.ContainsKey("right"),
            "Exactly up and right criteria");
        Check(downCriteria["up"]!.ToJsonElement().GetString() ==
            "Chaser moves up to column 1, row 2. The runner is then 9 steps away around the walls.",
            "Legal up criterion retains move description and distance");
        var downMove = downGame.ResolveMove(answer with
        {
            Probabilities = new Dictionary<string, double> { ["up"] = 0.1, ["right"] = 0.2 }
        });
        Check(downMove.Choice == "down", "Derived down probability selects a move");
        var leftGame = new ChaseGame();
        leftGame.ApplyChaserMove("right");
        var leftMove = leftGame.ResolveMove(answer with
        {
            Probabilities = new Dictionary<string, double> { ["up"] = 0.8, ["right"] = 0.2 }
        });
        Check(leftMove.Choice == "left", "Derived left probability selects a move");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -0.1, 1.1 })
        {
            try
            {
                game.ResolveMove(answer with
                {
                    Probabilities = new Dictionary<string, double> { ["up"] = invalid, ["right"] = 0.2 }
                });
                throw new Exception("Expected invalid chase probability");
            }
            catch (InvalidOperationException) { }
        }

        Check(game.TryMoveRunner("up") && game.Runner == new GridPosition(6, 5), "Runner legal move");
        Check(!game.TryMoveRunner("left") && game.Runner == new GridPosition(6, 5), "Runner blocked by wall");
        Check(!game.TryMoveRunner("outside"), "Unknown runner direction rejected");
        game.ApplyChaserMove("right");
        Check(game.Chaser == new GridPosition(2, 1) && game.ChaserMoves == 1, "Jev choice applied");
        try
        {
            game.ApplyChaserMove("down");
            throw new Exception("Expected invalid chaser move");
        }
        catch (InvalidOperationException) { }

        var caught = new ChaseGame();
        foreach (var direction in new[] { "left", "left", "left", "left", "left", "up", "up", "up", "up", "up" })
            Check(caught.TryMoveRunner(direction), "Runner route to chaser");
        Check(caught.IsCaught && caught.IsRoundComplete, "Caught round ends");
        try
        {
            caught.BuildRequest();
            throw new Exception("Expected completed round to reject requests");
        }
        catch (InvalidOperationException) { }

        var capped = new ChaseGame();
        Check(ChaseGame.MaxChaserMoves == 100, "100-move game limit");
        for (var move = 0; move < ChaseGame.MaxChaserMoves - 1; move++)
            capped.ApplyChaserMove(move % 2 == 0 ? "down" : "up");
        Check(!capped.IsRoundComplete, "Round continues through move 99");
        capped.ApplyChaserMove("down");
        Check(capped.IsRoundComplete && !capped.IsCaught, "Round request cap");

        var rate = new ChaseDecisionRate();
        Check(rate.DecisionsPerSecond == 0, "Decision rate starts at zero");
        rate.Record(TimeSpan.FromMilliseconds(200));
        Check(Math.Abs(rate.DecisionsPerSecond - 2.5) < 0.001, "Rate includes 400ms turn pacing");
        rate.Record(TimeSpan.FromSeconds(1));
        Check(Math.Abs(rate.DecisionsPerSecond - (2.0 / 1.4)) < 0.001,
            "Rate accounts for slower model decisions");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }
}
