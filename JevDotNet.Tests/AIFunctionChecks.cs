using System.Text.Json;
using System.Text.Json.Nodes;
using DecisionsDotNet;
using DecisionsDotNet.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

internal static class AIFunctionChecks
{
    public static async Task RunAsync()
    {
        DecisionsRequest? sent = null;
        var client = new FakeClient(request =>
        {
            sent = request;
            return new DecisionsResponse
            {
                Model = "jev-1.14.0",
                Usage = new DecisionsUsage { InputTokens = 1, OutputTokens = 1 },
                Answers = request.Questions.ToDictionary(pair => pair.Key, pair => pair.Value switch
                {
                    NoulQuestion => (JevAnswer)new NoulAnswer { Noul = 0.91234 },
                    ChoiceQuestion => new ChoiceAnswer { Choice = "billing", Confidence = 0.7,
                        Probabilities = new Dictionary<string, double> { ["billing"] = 0.8, ["other"] = 0.2 } },
                    ScoreQuestion => new ScoreAnswer { Score = 1.4, Confidence = 0.5,
                        Legend = new Dictionary<string, string> { ["0"] = "Calm", ["1"] = "Upset", ["2"] = "Furious" },
                        Probabilities = new Dictionary<string, double> { ["0"] = 0.1, ["1"] = 0.4, ["2"] = 0.5 } },
                    _ => throw new InvalidOperationException()
                })
            };
        });

        var triage = client.AsAIFunction("triage_ticket", "Scores a ticket.",
            DecisionsRequest.Create("unused")
                .Noul("refund", "Wants money back?")
                .Choice("team", "Which team?", "billing", "other")
                .Questions, "typesafe/jev-1.14");
        Check(triage.Name == "triage_ticket" && triage.Description == "Scores a ticket.", "Fixed tool metadata");
        var schema = triage.JsonSchema.GetProperty("properties");
        Check(schema.TryGetProperty("input", out _) && !schema.TryGetProperty("cancellationToken", out _), "Fixed tool schema");

        var result = Parse(await triage.InvokeAsync(new AIFunctionArguments { ["input"] = "Charged twice" }));
        Check(sent!.State.AsText() == "Charged twice" && sent.Model == "typesafe/jev-1.14" && sent.Questions.Count == 2, "Fixed tool request");
        Check(result["answers"]!["refund"]!["probability_yes"]!.GetValue<double>() == 0.912, "Yes/no result rounded");
        Check(result["answers"]!["team"]!["choice"]!.GetValue<string>() == "billing", "Choice result");

        var open = client.AsOpenDecisionsAIFunction("gpt-6-luna");
        Check(open.Name == "evaluate_decisions" && open.JsonSchema.GetProperty("properties").TryGetProperty("questions", out _),
            "Open tool schema");
        var questions = JsonSerializer.SerializeToElement(new[]
        {
            new { id = "angry", type = "score", instructions = "How angry?", levels = new[] { "Calm", "Upset", "Furious" } },
            new { id = "refund", type = "yes_no", instructions = "Wants a refund?", levels = (string[]?)null }
        });
        result = Parse(await open.InvokeAsync(new AIFunctionArguments { ["input"] = "Ugh", ["questions"] = questions }));
        Check(sent!.Model == "gpt-6-luna" && sent.Questions["angry"] is ScoreQuestion { Criteria.Count: 3 } &&
            sent.Questions["refund"] is NoulQuestion, "Open tool builds questions");
        Check(result["answers"]!["angry"]!["most_likely_level"]!.GetValue<string>() == "Furious", "Score most likely level");

        try
        {
            await open.InvokeAsync(new AIFunctionArguments
            {
                ["input"] = "x",
                ["questions"] = JsonSerializer.SerializeToElement(new[] { new { id = "a", type = "guess", instructions = "?" } })
            });
            throw new Exception("Expected an unknown type to be rejected");
        }
        catch (ArgumentException error) when (error.Message.Contains("yes_no")) { }

        try { client.AsAIFunction("t", "d", new Dictionary<string, DecisionsQuestion>(), "m"); throw new Exception("Expected empty questions to fail"); }
        catch (ArgumentException) { }

        // The README's agent wiring compiles against Microsoft Agent Framework.
        AIAgent agent = new ChatClientAgent(new NoChat(), instructions: "Call triage_ticket first.", tools: [triage]);
        Check(agent is not null, "Agent accepts the tool");
    }

    private static JsonObject Parse(object? value) => JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject();

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }

    private sealed class FakeClient(Func<DecisionsRequest, DecisionsResponse> respond) : IDecisionsClient
    {
        public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(respond(request));
    }

    private sealed class NoChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
