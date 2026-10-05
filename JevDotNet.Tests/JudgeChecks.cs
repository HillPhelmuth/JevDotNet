using System.Net;
using System.Text.Json;
using JevDotNet;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

internal static class JudgeChecks
{
    public static async Task RunAsync()
    {
        var examples = JudgeCatalog.Load(Path.Combine("JevDotNet.Demo", "Data", "judge-data.json"));
        Check(examples.Count == 80, "80 quality examples");
        foreach (var evaluator in JudgeCatalog.All)
        {
            var group = examples.Where(example => example.Type == evaluator.Key).ToArray();
            Check(group.Length == 20 && Enumerable.Range(1, 5).All(score => group.Count(item => item.ExpectedScore == score) == 4), "Balanced " + evaluator.Name);
            var families = group.GroupBy(item => item.Id[..item.Id.LastIndexOf('-')]).ToArray();
            Check(families.Length == 4 && families.All(family => family.Select(item => item.ExpectedScore).Order().SequenceEqual(Enumerable.Range(1, 5))),
                "Four scenario families spanning the rubric");
            Check(group.Select(item => item.Input.Response).Distinct().Count() == 20, "Distinct authored responses");
            foreach (var example in group)
            {
                var request = JudgeCatalog.BuildRequest(example.Type, example.Input);
                var state = request.State.ToJsonElement();
                Check(request.Model == DemoDecisionModels.DefaultModelId && request.Questions.Count == 1 &&
                    request.Questions[JudgeCatalog.QuestionId] is ScoreQuestion { Criteria.Count: 5 }, "Five-level request");
                Check(state.EnumerateObject().Count() == (evaluator.NeedsReference || evaluator.NeedsContext ? 3 : 2), "Minimal evaluator inputs");
                Check(state.TryGetProperty("reference_answer", out _) == evaluator.NeedsReference &&
                    state.TryGetProperty("source_context", out _) == evaluator.NeedsContext, "Correct evaluator evidence");
                var json = JsonSerializer.Serialize(request);
                Check(!json.Contains("expectedScore") && !json.Contains("explanation") && !json.Contains(example.Id) &&
                    !json.Contains(example.Explanation), "No reference-label leakage");
            }
        }
        var sample = examples[0];
        Expect<JsonException>(() => JudgeCatalog.ValidateDataset(examples.Skip(1).ToArray()));
        Expect<JsonException>(() => JudgeCatalog.ValidateDataset(examples.Select((item, i) => i == 1 ? item with { Id = sample.Id } : item).ToArray()));
        Expect<JsonException>(() => JudgeCatalog.ValidateDataset(examples.Select((item, i) => i == 0 ? item with { Type = "unknown" } : item).ToArray()));
        Expect<JsonException>(() => JudgeCatalog.ValidateDataset(examples.Select((item, i) => i == 0 ? item with { ExpectedScore = 6 } : item).ToArray()));
        Expect<JsonException>(() => JudgeCatalog.ValidateDataset(examples.Select((item, i) => i == 0 ? item with { Input = new("", "text") } : item).ToArray()));
        Expect<ArgumentException>(() => JudgeCatalog.BuildRequest("completeness", new("query", "response")));
        Expect<ArgumentException>(() => JudgeCatalog.BuildRequest("groundedness", new("query", "response")));
        Expect<ArgumentException>(() => JudgeCatalog.BuildRequest("unknown", sample.Input));
        Expect<ArgumentException>(() => JudgeCatalog.BuildRequest("relevance", new("query", " ")));
        Expect<ArgumentException>(() => JudgeCatalog.BuildRequest("relevance", new(new string('x', 8001), "response")));
        Expect<ArgumentException>(() => JudgeCatalog.BuildRequest("relevance", sample.Input, "invalid/model"));
        var irrelevantEvidence = JudgeCatalog.BuildRequest("relevance", sample.Input with { ReferenceAnswer = "do not send", SourceContext = "do not send" });
        Check(!JsonSerializer.Serialize(irrelevantEvidence).Contains("do not send"), "Exclude irrelevant evidence");
        foreach (var model in DemoDecisionModels.All)
            Check(JudgeCatalog.BuildRequest("relevance", sample.Input, model.Id).Model == model.Id, "Model selection");

        for (var level = 0; level < 5; level++)
        {
            var distribution = Enumerable.Range(0, 5).Select(index => index == level ? 1d : 0d).ToArray();
            var result = JudgeCatalog.ReadResult(Response(level, distribution), TimeSpan.FromMilliseconds(12));
            Check(result.Rating == level + 1 && result.WeightedScore == level + 1, "Zero-based conversion");
            Check(result.Model == "resolved-judge" && result.Elapsed.TotalMilliseconds == 12 && result.Usage.Cost == 0.0001m, "Result metadata");
        }
        var fractional = JudgeCatalog.ReadResult(Response(2.6, [0, 0, .4, .6, 0]), TimeSpan.Zero);
        Check(fractional.Rating == 4 && fractional.WeightedScore == 3.6 && fractional.Probabilities[3] == .6, "Fractional score preserved");
        var tied = JudgeCatalog.ReadResult(Response(1.5, [0, .5, .5, 0, 0]), TimeSpan.Zero);
        Check(tied.Rating == 2 && tied.WeightedScore == 2.5, "Lower-level tie break");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -.1, 4.1 })
            Expect<JsonException>(() => JudgeCatalog.ReadResult(Response(invalid, [1, 0, 0, 0, 0]), TimeSpan.Zero));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -.1, 1.1 })
        {
            Expect<JsonException>(() => JudgeCatalog.ReadResult(Response(0, [invalid, 0, 0, 0, 0]), TimeSpan.Zero));
            Expect<JsonException>(() => JudgeCatalog.ReadResult(Response(0, [1, 0, 0, 0, 0], invalid), TimeSpan.Zero));
        }
        Expect<JsonException>(() => JudgeCatalog.ReadResult(Response(0, [.5, 0, 0, 0, 0]), TimeSpan.Zero));
        Expect<JsonException>(() => JudgeCatalog.ReadResult(Response(0, [1, 0, 0, 0]), TimeSpan.Zero));
        Expect<JsonException>(() => JudgeCatalog.ReadResult(Response(0, [1, 0, 0, 0, 0, 0]), TimeSpan.Zero));
        var badKeys = (ScoreAnswer)Response(0, [1, 0, 0, 0, 0]).Answers[JudgeCatalog.QuestionId];
        Expect<JsonException>(() => JudgeCatalog.ReadResult(ResponseWithAnswer(badKeys with
        {
            Probabilities = new Dictionary<string, double> { ["1"] = 1, ["2"] = 0, ["3"] = 0, ["4"] = 0, ["5"] = 0 }
        }), TimeSpan.Zero));
        _ = JudgeCatalog.ReadResult(Response(0, [.9995, 0, 0, 0, 0]), TimeSpan.Zero);

        var successful = new JudgeAttempt(sample, tied);
        var summary = JudgeSummary.Calculate([successful, new(sample with { ExpectedScore = 2 }, tied), new(sample, null, "failed")]);
        Check(summary.Successful == 2 && summary.Agreed == 1 && summary.MeanAbsoluteError == .5, "Summary ignores failures");
        Check(JudgeSummary.Calculate([]).MeanAbsoluteError is null, "Empty error is unavailable");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "fake" }).Build();
        var environment = new JudgeEnvironment();
        var fake = new FakeDecisions();
        var service = new JudgeService(new DemoEvaluationService(configuration, fake), environment);
        Check(service.LoadExamples().Count == 80 && service.HasApiKey, "Judge service configuration");
        var groupExamples = examples.Where(item => item.Type == "relevance").ToArray();
        var run = new JudgeRun(groupExamples, DemoDecisionModels.DefaultModelId);
        var updates = 0;
        await service.RunAsync(run, () => { updates++; return Task.CompletedTask; });
        Check(run.Complete && run.Attempts.Count == 20 && updates == 20, "Twenty sequential progress updates");
        Check(fake.Queries.SequenceEqual(groupExamples.Select(item => item.Input.Response)) && fake.MaxConcurrent == 1, "Original ordered sequential inputs");
        await ExpectAsync<InvalidOperationException>(() => service.RunAsync(run));

        fake = new FakeDecisions { FailCall = 2 };
        service = new JudgeService(new DemoEvaluationService(configuration, fake), environment);
        var partial = new JudgeRun(groupExamples, DemoDecisionModels.DefaultModelId);
        await service.RunAsync(partial);
        Check(partial.Complete && partial.Attempts.Count == 20 && partial.Attempts[1].Error is not null &&
            partial.Attempts[0].Result is not null && partial.Attempts[19].Result is not null, "Continue after failure");

        using var cancellation = new CancellationTokenSource();
        var cancelled = new JudgeRun(groupExamples, DemoDecisionModels.DefaultModelId);
        await service.RunAsync(cancelled, () => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
        Check(cancelled.Cancelled && !cancelled.Complete && cancelled.Attempts.Count == 1, "Cancellation preserves completed result");

        var delayed = new TaskCompletionSource<JevResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateClient = new DelegateDecisions((_, _) => { inFlight.SetResult(); return delayed.Task; });
        var lateService = new JudgeService(new DemoEvaluationService(configuration, lateClient), environment);
        using var lateCancellation = new CancellationTokenSource();
        var lateRun = new JudgeRun(groupExamples, DemoDecisionModels.DefaultModelId);
        var lateTask = lateService.RunAsync(lateRun, cancellationToken: lateCancellation.Token);
        await inFlight.Task;
        lateCancellation.Cancel();
        delayed.SetResult(Response(4, [0, 0, 0, 0, 1]));
        await lateTask;
        Check(lateRun.Cancelled && lateRun.Attempts.Count == 0, "Discard late response after cancellation");

        var noKeyConfig = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(noKeyConfig);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddScoped<DemoEvaluationService>();
        services.AddScoped<JudgeService>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var noKey = scope.ServiceProvider.GetRequiredService<JudgeService>();
        Check(!noKey.HasApiKey && noKey.LoadExamples().Count == 80, "Missing key still loads dataset");
        await ExpectAsync<InvalidOperationException>(() => noKey.EvaluateAsync("relevance", sample.Input));
        await ExpectAsync<InvalidOperationException>(() => noKey.RunAsync(new JudgeRun(groupExamples, DemoDecisionModels.DefaultModelId)));
    }

    private static JevResponse Response(double score, double[] probabilities, double confidence = .8) => ResponseWithAnswer(new ScoreAnswer
    {
        Score = score, Confidence = confidence,
        Probabilities = probabilities.Select((value, index) => (value, index)).ToDictionary(item => item.index.ToString(), item => item.value),
        Legend = Enumerable.Range(0, 5).ToDictionary(index => index.ToString(), index => $"Level {index}")
    });
    private static JevResponse ResponseWithAnswer(ScoreAnswer answer) => new()
    {
        Model = "resolved-judge", Answers = new Dictionary<string, JevAnswer> { [JudgeCatalog.QuestionId] = answer },
        Usage = new JevUsage { InputTokens = 10, OutputTokens = 5, Cost = .0001m }
    };
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("Failed: " + name); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class FakeDecisions : IDecisionsClient
    {
        public int FailCall;
        public List<string> Queries { get; } = [];
        public int MaxConcurrent;
        private int Active;
        public async Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            Queries.Add(request.State.ToJsonElement().GetProperty("response").GetString()!);
            MaxConcurrent = Math.Max(MaxConcurrent, ++Active);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (Queries.Count == FailCall) throw new JevApiException(HttpStatusCode.BadRequest, "test failure", "OpenRouter");
                return Response(4, [0, 0, 0, 0, 1]);
            }
            finally { Active--; }
        }
    }
    private sealed class DelegateDecisions(Func<DecisionsRequest, CancellationToken, Task<JevResponse>> evaluate) : IDecisionsClient
    {
        public Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default) => evaluate(request, cancellationToken);
    }
    private sealed class JudgeEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "JevDotNet.Demo";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetFullPath("JevDotNet.Demo");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
