using System.Net;
using System.Text.Json;
using DecisionsDotNet;

internal static class BatchChecks
{
    private sealed record Message(string Id, string Text);

    public static async Task RunAsync()
    {
        var messages = Enumerable.Range(1, 5).Select(i => new Message($"m{i}", $"Text {i}")).ToArray();
        var options = new DecisionsBatchOptions<Message>
        {
            ItemId = message => message.Id,
            State = batch => DecisionsValue.FromObject(new { messages = batch }),
            Questions = message => new Dictionary<string, DecisionsQuestion>
            {
                ["refund"] = new NoulQuestion($"Does {message.Id} want a refund?"),
                ["angry"] = new NoulQuestion($"Is {message.Id} angry?")
            },
            Model = "typesafe/jev-1.14",
            ItemsPerRequest = 2
        };

        var bodies = new List<JsonElement>();
        var client = new FakeClient(request =>
        {
            bodies.Add(JsonSerializer.SerializeToElement(request));
            if (request.Questions.ContainsKey("m3_refund")) throw new JevApiException(HttpStatusCode.BadRequest, "too big", "OpenRouter");
            return Answer(request.Questions.Keys);
        });

        var results = new List<DecisionsBatchResult<Message>>();
        await foreach (var result in client.EvaluateBatchesAsync(messages, options))
            results.Add(result);

        Check(results.Select(result => result.Items.Count).SequenceEqual(new[] { 2, 2, 1 }), "Batch sizes");
        Check(results.Select(result => result.Number).SequenceEqual(new[] { 1, 2, 3 }), "Batch numbers");
        Check(bodies[0].GetProperty("questions").EnumerateObject().Select(p => p.Name)
            .SequenceEqual(new[] { "m1_refund", "m1_angry", "m2_refund", "m2_angry" }), "Prefixed question IDs");
        Check(bodies[0].GetProperty("state").GetProperty("messages").GetArrayLength() == 2 &&
            bodies[0].GetProperty("model").GetString() == "typesafe/jev-1.14", "Batch state and model");
        Check(results[0].Succeeded && results[0].GetAnswer<NoulAnswer>(messages[1], "angry").Noul == 0.25, "Per-item answer");
        Check(!results[1].Succeeded && results[1].Error is JevApiException, "Failed batch is reported");
        Check(results[2].Succeeded, "Later batches continue after a failure");
        try { results[1].GetAnswer<NoulAnswer>(messages[2], "refund"); throw new Exception("Expected failure"); }
        catch (InvalidOperationException error) when (error.InnerException is JevApiException) { }

        try
        {
            await foreach (var _ in client.EvaluateBatchesAsync(messages, new DecisionsBatchOptions<Message>
            {
                ItemId = options.ItemId, State = options.State, Questions = options.Questions,
                ItemsPerRequest = 2, ContinueOnError = false
            })) { }
            throw new Exception("Expected the failure to propagate");
        }
        catch (JevApiException) { }

        try
        {
            await foreach (var _ in client.EvaluateBatchesAsync(new[] { messages[0], messages[0] }, options)) { }
            throw new Exception("Expected duplicate IDs to be rejected");
        }
        catch (ArgumentException) { }

        using var cancel = new CancellationTokenSource();
        var seen = 0;
        try
        {
            await foreach (var _ in new FakeClient(request => Answer(request.Questions.Keys))
                .EvaluateBatchesAsync(messages, options, cancel.Token))
            {
                seen++;
                cancel.Cancel();
            }
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) { }
        Check(seen == 1, "Cancellation stops further batches");
    }

    private static DecisionsResponse Answer(IEnumerable<string> ids) => new()
    {
        Model = "jev-1.14.0",
        Answers = ids.ToDictionary(id => id, id => (JevAnswer)new NoulAnswer { Noul = id.EndsWith("angry") ? 0.25 : 0.75 }),
        Usage = new DecisionsUsage { InputTokens = 1, OutputTokens = 1 }
    };

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }

    private sealed class FakeClient(Func<DecisionsRequest, DecisionsResponse> respond) : IDecisionsClient
    {
        public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(respond(request));
    }
}
