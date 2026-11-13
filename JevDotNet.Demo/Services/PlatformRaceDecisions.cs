using DecisionsDotNet;

namespace JevDotNet.Demo.Services;

public sealed record RaceOption(int Id, int Climb, int Horizontal);
public sealed record RaceHop(int Run, int Racer, int Id, int Score, int OpponentScore, RaceOption[] Options);
public sealed record RaceProgress(int Score, int Hops, int Waits, int Misses, int Bones);
public sealed record RaceTransition(int Id, bool Used);
public sealed record RaceUpdate(int Run, RaceProgress[] Racers, RaceTransition[] Transitions, int? Winner, bool Hidden);

public sealed class PlatformRaceDecisions(
    [FromKeyedServices(PlatformRaceDecisions.ClientKey)] IDecisionsClient? client = null)
{
    public const string ClientKey = "platform-race";
    public const int Goal = 500;
    public const int MaxRequestsPerRacer = 120;
    public static readonly TimeSpan RoundLimit = TimeSpan.FromSeconds(120);
    public bool Available => client is not null;

    public static DecisionsRequest BuildRequest(string model, RaceHop hop)
    {
        if (hop.Options is not { Length: >= 1 and <= 5 } ||
            hop.Options.Any(p => p is null || p.Id <= 0 || p.Climb is < 1 or > 178 ||
                p.Horizontal is < -200 or > 200) ||
            hop.Options.Select(p => p.Id).Distinct().Count() != hop.Options.Length ||
            hop.Score is < 0 or > Goal || hop.OpponentScore is < 0 or > Goal)
            throw new ArgumentException("Invalid platform race snapshot.", nameof(hop));

        var options = hop.Options.ToDictionary(p => $"p{p.Id}", p => (DecisionsValue?)DecisionsValue.FromObject(new
        {
            climb_px = p.Climb,
            horizontal_px = p.Horizontal
        }));

        var state = DecisionsValue.FromObject(new
        {
            goal_score = Goal,
            your_score = hop.Score,
            opponent_score = hop.OpponentScore
        });
        return DecisionsRequest.Create(state).Question("next_hop", new ChoiceQuestion(
            "The game has verified that every listed platform can be landed on from your current position. " +
            "Choose one platform to climb toward the finish. Prefer more height, using the shorter sideways move for similar climbs. " +
            "climb_px is the height gained; horizontal_px is the signed distance along the shortest path around the wrap.", options)).Build(model);
    }

    public async Task<(int Target, decimal? Cost)> ChooseAsync(
        DecisionsRequest request, RaceHop hop, CancellationToken cancellationToken)
    {
        var response = await (client ?? throw new InvalidOperationException("Configure an OpenRouter API key."))
            .EvaluateAsync(request, cancellationToken);
        var answer = response.GetAnswer<ChoiceAnswer>("next_hop");
        foreach (var option in hop.Options)
            if (answer.Choice == $"p{option.Id}") return (option.Id, response.Usage.Cost);

        throw new InvalidOperationException("The model selected a platform that was not offered.");
    }
}
