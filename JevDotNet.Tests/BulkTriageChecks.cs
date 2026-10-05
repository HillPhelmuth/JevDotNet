using System.Net;
using System.Text.Json;
using JevDotNet;
using JevDotNet.Demo.Services;
using Microsoft.Extensions.Configuration;

internal static class BulkTriageChecks
{
    public static async Task RunAsync()
    {
        var messages = BulkTriageCatalog.Load(Path.Combine("JevDotNet.Demo", "Data", "table-data.json"));
        Check(messages.Count == 95 && messages[0].Id == "m001" && messages[^1].Id == "m095", "Dataset IDs");
        var request = BulkTriageCatalog.BuildJevRequest(messages);
        Check(request.Model == BulkTriageCatalog.JevModel && request.Questions.Count == 475, "Bulk request count");
        var state = request.State.ToJsonElement();
        Check(state.GetProperty("messages").GetArrayLength() == 95 &&
            state.GetRawText().Contains(messages[0].Text, StringComparison.Ordinal) &&
            !state.GetRawText().Contains("Cancelling", StringComparison.Ordinal), "Model state excludes reference scores");
        var firstQuestion = (NoulQuestion)request.Questions["m001_cancel"];
        Check(firstQuestion.Instructions.ToJsonElement().GetString()!.Contains("message m001") &&
            firstQuestion.Criteria!.True!.ToJsonElement().GetString()!.Contains("shopping around"), "Question criteria");
        Check(BulkTriageCatalog.BuildJevRequest([messages[0]]).Questions.Count == 5, "Individual question count");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenRouter:ApiKey"] = "test-key"
        }).Build();
        var decisions = new FakeDecisions();
        var luna = new FakeLuna();
        var service = new BulkTriageService(config, null!, decisions, luna);
        var batch = new BulkTriageRun(messages, JevBulkMode.Batched);
        await service.RunAsync(batch);
        Check(batch.Complete && decisions.Calls == 1 && luna.Calls == 95 &&
            batch.Jev.Count == 95 && batch.Luna.Count == 95 &&
            decisions.RequestSizes.SequenceEqual([95]), "Full batch and Luna call counts");
        Check(batch.Jev["m095"].Scores!.NeedsPerson == 0.8, "Batch answer mapping");

        decisions.Calls = 0;
        decisions.RequestSizes.Clear();
        luna.Calls = 0;
        var grouped = new BulkTriageRun(messages, JevBulkMode.Batched, batchSize: 20);
        await service.RunAsync(grouped);
        Check(grouped.Complete && decisions.Calls == 5 && luna.Calls == 95 &&
            decisions.RequestSizes.SequenceEqual([20, 20, 20, 20, 15]) &&
            grouped.JevRequests.Count == 5 && grouped.Jev["m095"].RequestNumber == 5,
            "Configurable batch sizes and response mapping");

        decisions.Calls = 0;
        decisions.RequestSizes.Clear();
        luna.Calls = 0;
        var remainder = new BulkTriageRun(messages.Take(5).ToArray(), JevBulkMode.Batched, batchSize: 2);
        await service.RunAsync(remainder);
        Check(remainder.Complete && decisions.RequestSizes.SequenceEqual([2, 2, 1]) &&
            remainder.JevRequests.Count == 3 && luna.Calls == 5, "Final single-message remainder");
        ExpectBatchSize(1);
        ExpectBatchSize(96);

        decisions.Calls = 0;
        decisions.RequestSizes.Clear();
        luna.Calls = 0;
        var individual = new BulkTriageRun(messages, JevBulkMode.OneAtATime);
        await service.RunAsync(individual);
        Check(individual.Complete && decisions.Calls == 95 && luna.Calls == 95 &&
            individual.Jev.Count == 95 && decisions.RequestSizes.All(size => size == 1),
            "Individual call counts");
        var comparison = BulkTriageComparison.Calculate(individual);
        Check(comparison.Compared == 475 && comparison.Agreed == 0 &&
            Math.Abs(comparison.MeanAbsoluteGapPoints - 60) < 0.001 &&
            comparison.Messages.Count == 95 && comparison.Messages[0].DisagreedCategories.Count == 5,
            "Comparison scores");

        decisions.Fail = true;
        var rejected = new BulkTriageRun(messages, JevBulkMode.Batched);
        luna.Calls = 0;
        await service.RunAsync(rejected);
        Check(rejected.JevError is not null && rejected.Jev.Count == 0 && luna.Calls == 0,
            "Rejected batch does not fall back or call Luna");
        decisions.Fail = false;

        decisions.FailId = "m003";
        luna.Calls = 0;
        var partial = new BulkTriageRun(messages, JevBulkMode.OneAtATime);
        await service.RunAsync(partial);
        var partialComparison = BulkTriageComparison.Calculate(partial);
        Check(partial.Complete && partial.Jev["m003"].Scores is null && luna.Calls == 95 &&
            partialComparison.Compared == 470 && partialComparison.Messages.Count == 94,
            "Partial failure uses valid paired answers only");
        decisions.FailId = null;

        using var cancellation = new CancellationTokenSource();
        var cancelled = new BulkTriageRun(messages, JevBulkMode.OneAtATime);
        try
        {
            await service.RunAsync(cancelled, () =>
            {
                if (cancelled.Jev.Count == 2) cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check(cancelled.Jev.Count == 2, "Cancellation stops calls");

        foreach (var invalid in new[]
        {
            "{}",
            "{\"cancelling\":1.2,\"refund\":0,\"angry\":0,\"bug\":0,\"needs_person\":0}",
            "{\"cancelling\":0,\"refund\":0,\"angry\":0,\"bug\":0,\"needs_person\":\"yes\"}"
        })
        {
            try { LunaTriageAgent.ParseScores(invalid); throw new Exception("Expected invalid Luna scores"); }
            catch (JsonException) { }
        }
        await CheckLunaWireAsync(messages[0]);
    }

    private static async Task CheckLunaWireAsync(BulkTriageMessage message)
    {
        var bodies = new List<string>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            Check(request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal), "Luna endpoint");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = "test",
                    @object = "chat.completion",
                    created = 1,
                    model = BulkTriageCatalog.LunaModel,
                    choices = new[] { new { index = 0, finish_reason = "stop", message = new
                    {
                        role = "assistant",
                        content = "{\"cancelling\":0.9,\"refund\":0.1,\"angry\":0.2,\"bug\":0.3,\"needs_person\":0.4}"
                    } } }
                }))
            };
        }));
        var agent = new LunaTriageAgent("test-key", http);
        var scores = await agent.EvaluateAsync(message);
        await agent.EvaluateAsync(message);
        Check(scores.Cancelling == 0.9 && bodies.Count == 2, "Independent Luna calls and response parsing");
        using var document = JsonDocument.Parse(bodies[0]);
        using var secondDocument = JsonDocument.Parse(bodies[1]);
        var root = document.RootElement;
        Check(root.GetProperty("messages").GetArrayLength() == 2 &&
            secondDocument.RootElement.GetProperty("messages").GetArrayLength() == 2,
            "Luna does not retain prior messages");
        Check(root.GetProperty("model").GetString() == BulkTriageCatalog.LunaModel, "Luna model");
        var format = root.GetProperty("response_format");
        Check(format.GetProperty("type").GetString() == "json_schema", "Luna response format");
        Check(format.GetProperty("json_schema").GetProperty("strict").GetBoolean(), "Luna strict schema");
        var schema = format.GetProperty("json_schema").GetProperty("schema");
        Check(schema.GetProperty("required").GetArrayLength() == 5 &&
            schema.GetProperty("additionalProperties").GetBoolean() == false, "Luna schema");
        Check(bodies[0].Contains(message.Text, StringComparison.Ordinal) &&
            !bodies[0].Contains("98.0%", StringComparison.Ordinal), "Luna excludes reference scores");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }

    private static void ExpectBatchSize(int size)
    {
        try
        {
            _ = new BulkTriageRun([], JevBulkMode.Batched, batchSize: size);
            throw new Exception($"Expected rejection for batch size {size}");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    private sealed class FakeDecisions : IDecisionsClient
    {
        public int Calls;
        public bool Fail;
        public string? FailId;
        public List<int> RequestSizes { get; } = [];
        public Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            RequestSizes.Add(request.State.ToJsonElement().GetProperty("messages").GetArrayLength());
            if (Fail || (FailId is not null && request.Questions.ContainsKey($"{FailId}_cancel")))
                throw new JevApiException(HttpStatusCode.BadRequest, "too many questions", "OpenRouter");
            var answers = request.Questions.Keys.ToDictionary(key => key,
                key => (JevAnswer)new NoulAnswer { Noul = 0.8 });
            return Task.FromResult(new JevResponse
            {
                Model = request.Model, Answers = answers,
                Usage = new JevUsage { InputTokens = 1, OutputTokens = 1 }
            });
        }
    }

    private sealed class FakeLuna : ILunaTriageClient
    {
        public int Calls;
        public Task<TriageScores> EvaluateAsync(BulkTriageMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new TriageScores(0.2, 0.2, 0.2, 0.2, 0.2));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}
