using DecisionsDotNet;
using JevDotNet.Demo.Services;

public sealed class PlatformRaceChecks
{
    private static RaceHop Hop(int racer, int id, int round = 1) =>
        new(round, racer, id, 40, 31, [new(12, 78, -109), new(13, 156, 47)]);

    [Fact]
    public async Task SendsStructuredChoicesAndRejectsUnlistedAnswers()
    {
        var hop = Hop(0, 1);
        var request = PlatformRaceDecisions.BuildRequest("test/model", hop);
        var question = Assert.IsType<ChoiceQuestion>(Assert.Single(request.Questions).Value);
        Assert.Equal("test/model", request.Model);
        Assert.Equal(31, request.State.ToJsonElement().GetProperty("opponent_score").GetInt32());
        Assert.Equal(-109, question.Criteria["p12"]!.ToJsonElement().GetProperty("horizontal_px").GetInt32());
        Assert.Equal(156, question.Criteria["p13"]!.ToJsonElement().GetProperty("climb_px").GetInt32());
        Assert.Equal(["p12", "p13"], question.Criteria.Keys);

        var client = new ControlledClient();
        var service = new PlatformRaceDecisions(client);
        var first = service.ChooseAsync(request, hop, TestContext.Current.CancellationToken);
        var otherHop = Hop(1, 2);
        var other = service.ChooseAsync(PlatformRaceDecisions.BuildRequest("test/other", otherHop), otherHop,
            TestContext.Current.CancellationToken);
        client.Reply("test/other", "p13", .0001m);
        Assert.Equal((13, .0001m), await other);
        Assert.False(first.IsCompleted);
        client.Reply("test/model", "stay", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
    }

    [Fact]
    public async Task StoppingDropsPendingRequestsAndCancelsEvaluation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new ControlledClient();
        var service = new PlatformRaceDecisions(client);
        var round = new PlatformRaceRound(1, ["test/model", "test/other"], ["Left", "Right"]);
        var hop = Hop(0, 1);
        Assert.True(round.CanRequest(hop));
        var row = round.Begin(hop, 0);
        Assert.False(round.CanRequest(Hop(0, 2)));
        Assert.True(round.CanRequest(Hop(1, 2)));
        var pending = service.ChooseAsync(PlatformRaceDecisions.BuildRequest(round.Models[0], hop), hop, cancellation.Token);

        round.Stop();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(RaceRequestStatus.Dropped, row.Status);
        var next = new PlatformRaceRound(2, round.Models, round.Names);
        Assert.False(next.CanRequest(hop));
        Assert.True(next.CanRequest(Hop(0, 1, round: 2)));
    }

    [Fact]
    public void RoundKeepsWholeRunStatisticsAndLimitsEachRacer()
    {
        var round = new PlatformRaceRound(1, ["test/model", "test/other"], ["Left", "Right"]);
        for (var id = 1; id <= PlatformRaceDecisions.MaxRequestsPerRacer; id++)
        {
            var hop = Hop(0, id);
            Assert.True(round.CanRequest(hop));
            Assert.False(round.AtLimit(0));
            var row = round.Begin(hop, id / 10.0);
            row.Latency = id;
            row.Cost = .0001m;
            row.Status = RaceRequestStatus.Queued;
            round.Update(new(1, [new(id, id, 0, 0, 0), new(0, 0, 0, 0, 0)], [new(id, true)], null, false));
        }
        Assert.True(round.AtLimit(0));
        Assert.False(round.AtLimit(1));
        Assert.False(round.CanRequest(Hop(0, 1)));
        Assert.All(round.Requests, row => Assert.Equal(RaceRequestStatus.Used, row.Status));
        Assert.Equal(60.5, PlatformRaceRound.Percentile(round.Requests, .50));
        Assert.Equal(.012m, round.Requests.Sum(row => row.Cost));
    }

    private sealed class ControlledClient : IDecisionsClient
    {
        private readonly Dictionary<string, TaskCompletionSource<DecisionsResponse>> replies = [];

        public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            var reply = new TaskCompletionSource<DecisionsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            replies.Add(request.Model, reply);
            return reply.Task.WaitAsync(cancellationToken);
        }

        public void Reply(string model, string target, decimal? cost) => replies[model].SetResult(new()
        {
            Model = model,
            Answers = new Dictionary<string, JevAnswer>
            {
                ["next_hop"] = new ChoiceAnswer { Choice = target, Confidence = 1, Probabilities = new Dictionary<string, double> { [target] = 1 } }
            },
            Usage = new DecisionsUsage { InputTokens = 1, OutputTokens = 1, Cost = cost }
        });
    }
}
