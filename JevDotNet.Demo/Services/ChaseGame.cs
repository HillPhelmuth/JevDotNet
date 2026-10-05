using JevDotNet;

namespace JevDotNet.Demo.Services;

public readonly record struct GridPosition(int Column, int Row);

public sealed class ChaseGame
{
    public const int GridSize = 6;
    public const int MaxChaserMoves = 100;

    private static readonly IReadOnlyDictionary<string, GridPosition> Directions =
        new Dictionary<string, GridPosition>
        {
            ["up"] = new(0, -1),
            ["down"] = new(0, 1),
            ["left"] = new(-1, 0),
            ["right"] = new(1, 0)
        };

    private static readonly HashSet<GridPosition> Walls =
    [
        new(2, 2), new(5, 2), new(3, 4),
        new(4, 4), new(2, 5), new(5, 5)
    ];

    public GridPosition Runner { get; private set; } = new(6, 6);
    public GridPosition Chaser { get; private set; } = new(1, 1);
    public int RunnerMoves { get; private set; }
    public int ChaserMoves { get; private set; }
    public bool IsCaught => Runner == Chaser;
    public bool IsRoundComplete => IsCaught || ChaserMoves >= MaxChaserMoves;
    public static bool IsWall(GridPosition position) => Walls.Contains(position);

    public bool TryMoveRunner(string direction)
    {
        if (IsRoundComplete || !TryStep(Runner, direction, out var next)) return false;
        Runner = next;
        RunnerMoves++;
        return true;
    }

    public DecisionsRequest BuildRequest(string modelId = DemoDecisionModels.DefaultModelId)
    {
        modelId = DemoDecisionModels.RequireSupported(modelId);
        if (IsRoundComplete) throw new InvalidOperationException("This chase round is complete.");

        var distance = ShortestDistance(Chaser, Runner);
        var wallLocations = string.Join("; ", Walls.OrderBy(wall => wall.Row).ThenBy(wall => wall.Column)
            .Select(PositionText));
        var state = $"A chase on a {GridSize} by {GridSize} grid. " +
            $"The runner is at {PositionText(Runner)}. " +
            $"The chaser is at {PositionText(Chaser)}, {distance} steps away around the walls. " +
            $"Walls at: {wallLocations}. " +
            "The chaser wants to catch the runner as fast as possible.";

        var criteria = new Dictionary<string, JevValue?>();
        foreach (var direction in Directions.Keys)
        {
            if (!TryStep(Chaser, direction, out var next)) continue;
            var remaining = ShortestDistance(next, Runner);
            criteria[direction] = $"Chaser moves {direction} to {PositionText(next)}. " +
                $"The runner is then {remaining} steps away around the walls.";
        }

        return new DecisionsRequest(state,
            new Dictionary<string, JevQuestion>
            {
                ["move"] = new ChoiceQuestion("Which move brings the chaser closest to the runner?", criteria)
            }, modelId);
    }

    public void ApplyChaserMove(string direction)
    {
        if (IsRoundComplete || !TryStep(Chaser, direction, out var next))
            throw new InvalidOperationException($"The model selected an unavailable move: '{direction}'.");
        Chaser = next;
        ChaserMoves++;
    }

    public static int ShortestDistance(GridPosition start, GridPosition goal)
    {
        var visited = new HashSet<GridPosition> { start };
        var queue = new Queue<(GridPosition Position, int Steps)>();
        queue.Enqueue((start, 0));
        while (queue.TryDequeue(out var current))
        {
            if (current.Position == goal) return current.Steps;
            foreach (var direction in Directions.Keys)
            {
                if (TryStep(current.Position, direction, out var next) && visited.Add(next))
                    queue.Enqueue((next, current.Steps + 1));
            }
        }
        throw new InvalidOperationException("No route exists between the runner and chaser.");
    }

    private static bool TryStep(GridPosition position, string direction, out GridPosition next)
    {
        next = default;
        if (!Directions.TryGetValue(direction, out var delta)) return false;
        next = new(position.Column + delta.Column, position.Row + delta.Row);
        return next.Column is >= 1 and <= GridSize && next.Row is >= 1 and <= GridSize && !IsWall(next);
    }

    private static string PositionText(GridPosition position)
        => $"column {position.Column}, row {position.Row}";
}
