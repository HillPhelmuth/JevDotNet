namespace JevDotNet.Demo.Services;

public enum RaceRequestStatus { InFlight, Queued, Used, Dropped, Error }

public sealed class RaceRequest(int id, int racer, double elapsed)
{
    public int Id { get; } = id;
    public int Racer { get; } = racer;
    public double Elapsed { get; } = elapsed;
    public RaceRequestStatus Status { get; set; }
    public double? Latency { get; set; }
    public decimal? Cost { get; set; }
    public string? Detail { get; set; }
}

public sealed class PlatformRaceRound(int id, string[] models, string[] names)
{
    private readonly Dictionary<int, RaceRequest> requests = [];
    private readonly int[] counts = new int[2];
    private readonly int[] lastIds = new int[2];
    private readonly RaceRequest?[] active = new RaceRequest?[2];

    public int Id { get; } = id;
    public string[] Models { get; } = models;
    public string[] Names { get; } = names;
    public RaceProgress[] Progress { get; private set; } = [new(0, 0, 0, 0, 0), new(0, 0, 0, 0, 0)];
    public IReadOnlyCollection<RaceRequest> Requests => requests.Values;
    public bool Running { get; private set; } = true;

    public bool CanRequest(RaceHop hop) => Running && hop.Run == Id && hop.Racer is 0 or 1 &&
        hop.Id > lastIds[hop.Racer] && !requests.ContainsKey(hop.Id) && active[hop.Racer] is null;

    public bool AtLimit(int racer) => counts[racer] >= PlatformRaceDecisions.MaxRequestsPerRacer;

    public RaceRequest Begin(RaceHop hop, double elapsed)
    {
        var request = new RaceRequest(hop.Id, hop.Racer, elapsed);
        lastIds[hop.Racer] = hop.Id;
        counts[hop.Racer]++;
        active[hop.Racer] = request;
        requests.Add(hop.Id, request);
        return request;
    }

    public void EndRequest(RaceRequest request, RaceRequestStatus status)
    {
        request.Status = status;
        active[request.Racer] = null;
    }

    public void Update(RaceUpdate update)
    {
        Progress = update.Racers;
        foreach (var transition in update.Transitions)
            if (requests.TryGetValue(transition.Id, out var request) && request.Status == RaceRequestStatus.Queued)
                EndRequest(request, transition.Used ? RaceRequestStatus.Used : RaceRequestStatus.Dropped);
    }

    public void Stop()
    {
        Running = false;
        foreach (var request in active)
            if (request is not null) EndRequest(request, RaceRequestStatus.Dropped);
    }

    public static double? Percentile(IEnumerable<RaceRequest> rows, double percentile)
    {
        var values = rows.Where(r => r.Latency.HasValue).Select(r => r.Latency!.Value).Order().ToArray();
        if (values.Length == 0) return null;
        var index = (values.Length - 1) * percentile;
        var lower = (int)index;
        return values[lower] + (values[(int)Math.Ceiling(index)] - values[lower]) * (index - lower);
    }
}
