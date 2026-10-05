using System.Net;
using System.Text.Json;
using JevDotNet;
using JevDotNet.Demo.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class DemoChecks
{
    public static async Task RunAsync()
    {
        foreach (var scenario in DemoScenarioCatalog.All)
        {
            var request = DemoScenarioCatalog.BuildRequest(scenario.Slug, scenario.SampleInput);
            Check(request.Model == DemoDecisionModels.DefaultModelId, $"{scenario.Slug}: default model");
            Check(request.Questions.Count == 3, $"{scenario.Slug}: question count");
            Check(request.Questions.Values.Count(question => question is ChoiceQuestion) == 1, $"{scenario.Slug}: choice");
            Check(request.Questions.Values.Count(question => question is NoulQuestion) == 1, $"{scenario.Slug}: noul");
            Check(request.Questions.Values.Count(question => question is ScoreQuestion) == 1, $"{scenario.Slug}: score");
            Check(request.State.ToJsonElement().ValueKind == JsonValueKind.Object, $"{scenario.Slug}: structured state");
            var json = JsonSerializer.Serialize(request);
            Check(json.Contains("\"type\":\"choice\"") && json.Contains("\"type\":\"noul\"") && json.Contains("\"type\":\"score\""),
                $"{scenario.Slug}: wire discriminators");
        }

        Check(DemoDecisionModels.All.Count >= 6 &&
            DemoDecisionModels.All.Select(model => model.Id).Distinct().Count() == DemoDecisionModels.All.Count,
            "Distinct Decisions model options");
        foreach (var scenario in DemoScenarioCatalog.All)
        foreach (var model in DemoDecisionModels.All)
            Check(DemoScenarioCatalog.BuildRequest(scenario.Slug, scenario.SampleInput, model.Id).Model == model.Id,
                $"{scenario.Slug}: selected {model.Id}");
        ExpectArgument(() => DemoScenarioCatalog.BuildRequest("support", "Test", "unsupported/model"),
            "Unsupported model");

        Check(DemoScenarioCatalog.BuildRequest("content", "A new post").State.ToJsonElement()
            .GetProperty("community_rules").GetArrayLength() == 2, "Content policy context");
        Check(DemoScenarioCatalog.BuildRequest("leads", "A new inquiry").State.ToJsonElement()
            .TryGetProperty("ideal_customer_profile", out _), "Lead profile context");

        ExpectArgument(() => DemoScenarioCatalog.BuildRequest("support", "   "), "Empty input");
        ExpectArgument(() => DemoScenarioCatalog.BuildRequest("support", new string('x', DemoScenarioCatalog.MaxInputLength + 1)), "Oversized input");
        ExpectArgument(() => DemoScenarioCatalog.BuildRequest("unknown", "text"), "Unknown scenario");

        Check(DemoScenarioCatalog.Recommend("support", Response(
            ("team", Choice("billing", 0.5)), ("urgent", Noul(0.9)), ("frustration", Score(2)))).Title == "Human review suggested",
            "Support low confidence gate");
        Check(DemoScenarioCatalog.Recommend("support", Response(
            ("team", Choice("billing", 0.8)), ("urgent", Noul(0.8)), ("frustration", Score(0)))).Title.Contains("priority"),
            "Support priority");
        Check(DemoScenarioCatalog.Recommend("content", Response(
            ("possible_violation", Noul(0.8)), ("category", Choice("spam", 0.9)), ("severity", Score(1.8)))).Title == "Priority human review",
            "Content review priority");
        Check(DemoScenarioCatalog.Recommend("content", Response(
            ("possible_violation", Noul(0.9)), ("category", Choice("spam", 0.4)), ("severity", Score(2)))).Title == "Human review suggested",
            "Content low confidence gate");
        Check(DemoScenarioCatalog.Recommend("leads", Response(
            ("intent", Choice("buying", 0.9)), ("demo_interest", Noul(0.8)), ("fit", Score(1.8)))).Title == "Sales follow-up suggested",
            "Lead follow-up");
        Check(DemoScenarioCatalog.Recommend("leads", Response(
            ("intent", Choice("buying", 0.3)), ("demo_interest", Noul(0.8)), ("fit", Score(1.8)))).Title == "Manual qualification suggested",
            "Lead low confidence gate");

        var noKey = new DemoEvaluationService(new ConfigurationBuilder().Build());
        Check(!noKey.HasApiKey, "Missing key state");
        var noKeyServices = new ServiceCollection();
        noKeyServices.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        noKeyServices.AddScoped<DemoEvaluationService>();
        using (var provider = noKeyServices.BuildServiceProvider())
            Check(!provider.GetRequiredService<DemoEvaluationService>().HasApiKey,
                "Missing-key demo resolves through DI");
        var directKeyOnly = new DemoEvaluationService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TypeSafe:ApiKey"] = "direct-key"
            }).Build());
        Check(!directKeyOnly.HasApiKey, "Direct TypeSafe key does not enable OpenRouter demo");
        try
        {
            await noKey.EvaluateAsync(DemoScenarioCatalog.BuildRequest("support", "test"));
            throw new Exception("Expected missing-key error");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("OpenRouter:ApiKey")) { }

        var stubClient = new StubClient();
        var configured = new DemoEvaluationService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "fake-key" }).Build(),
            stubClient);
        Check(configured.HasApiKey, "Configured key state");
        try
        {
            await configured.EvaluateAsync(DemoScenarioCatalog.BuildRequest("support", "test"));
            throw new Exception("Expected API error");
        }
        catch (JevApiException error) when (error.StatusCode == HttpStatusCode.PaymentRequired && error.Service == "OpenRouter") { }
        Check(stubClient.Calls == 1, "Demo delegates evaluation to injected Decisions client");
    }

    private static JevResponse Response(params (string Id, JevAnswer Answer)[] answers) => new()
    {
        Model = "jev-1.13.0",
        Answers = answers.ToDictionary(item => item.Id, item => item.Answer),
        Usage = new JevUsage { InputTokens = 5, OutputTokens = 2 }
    };

    private static ChoiceAnswer Choice(string value, double confidence) => new()
    {
        Choice = value, Confidence = confidence, Probabilities = new Dictionary<string, double> { [value] = 1 }
    };
    private static NoulAnswer Noul(double value) => new() { Noul = value };
    private static ScoreAnswer Score(double value) => new()
    {
        Score = value, Confidence = 0.9,
        Legend = new Dictionary<string, string> { ["0"] = "Low", ["1"] = "Medium", ["2"] = "High" },
        Probabilities = new Dictionary<string, double> { ["0"] = 0, ["1"] = 0, ["2"] = 1 }
    };

    private static void ExpectArgument(Action action, string name)
    {
        try { action(); throw new Exception($"Expected argument error: {name}"); }
        catch (ArgumentException) { }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }

    private sealed class StubClient : IDecisionsClient
    {
        public int Calls { get; private set; }

        public Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<JevResponse>(
                new JevApiException(HttpStatusCode.PaymentRequired, "insufficient credits", "OpenRouter"));
        }
    }
}
