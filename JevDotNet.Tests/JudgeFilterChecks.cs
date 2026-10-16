using JevDotNet;
using JevDotNet.Demo.Components.Judge;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

internal static class JudgeFilterChecks
{
    public static async Task RunAsync()
    {
        Check(!JudgeResultFilter.DiffersFromExpected(3, null, null), "Pending and failed ratings are excluded");
        Check(!JudgeResultFilter.DiffersFromExpected(3, 3, 3), "Matching ratings are excluded");
        Check(JudgeResultFilter.DiffersFromExpected(3, 2, 3), "Jev-only divergence is included");
        Check(JudgeResultFilter.DiffersFromExpected(3, 3, 4), "Luna-only divergence is included");
        Check(JudgeResultFilter.DiffersFromExpected(3, 2, 2), "Judges agreeing on the wrong score are included");
        Check(JudgeResultFilter.DiffersFromExpected(3, null, 4), "Completed divergent rating is included before its counterpart");

        var titles = new[] { "filter-match", "filter-jev", "filter-luna", "filter-both", "filter-failed" };
        var examples = titles.Select(title => new JudgeExample(title, "relevance", title, new("Query", "Response"), 3, "Reference")).ToArray();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "fake" }).Build();
        var judge = new JudgeService(new DemoEvaluationService(config, new FilterDecisions()), new TestEnvironment());
        var comparison = new JudgeComparisonRun(examples, DemoDecisionModels.DefaultModelId);
        await judge.RunAsync(comparison.Decisions);
        int?[] lunaRatings = [3, 3, 4, 2, null];
        for (var index = 0; index < examples.Length; index++)
        {
            var rating = lunaRatings[index];
            comparison.Luna.Add(rating is null ? new(examples[index], null, "failed") : new(examples[index], new(
                new(rating.Value, "Rationale", "unused", "fake", TimeSpan.Zero, DateTimeOffset.UtcNow, null, null), true)));
        }
        var metrics = JudgeComparisonSummary.Calculate(comparison);
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var parameters = new Dictionary<string, object?>
        {
            [nameof(JudgeDatasetResults.Evaluator)] = JudgeCatalog.Find("relevance"),
            [nameof(JudgeDatasetResults.Examples)] = examples,
            [nameof(JudgeDatasetResults.Run)] = comparison.Decisions,
            [nameof(JudgeDatasetResults.OnlyDifferences)] = true
        };
        var jev = await RenderAsync<JudgeDatasetResults>(renderer, parameters);
        CheckRows(jev, ["filter-jev", "filter-both"], ["filter-match", "filter-luna", "filter-failed"]);
        parameters[nameof(JudgeDatasetResults.OnlyDifferences)] = false;
        CheckRows(await RenderAsync<JudgeDatasetResults>(renderer, parameters), titles, []);
        parameters[nameof(JudgeDatasetResults.Run)] = null;
        parameters[nameof(JudgeDatasetResults.OnlyDifferences)] = true;
        Check((await RenderAsync<JudgeDatasetResults>(renderer, parameters)).Contains("No completed ratings differ from expected."), "Empty Jev filter message");

        var comparisonParameters = new Dictionary<string, object?>
        {
            [nameof(JudgeComparisonResults.Run)] = comparison,
            [nameof(JudgeComparisonResults.OnlyDifferences)] = true
        };
        CheckRows(await RenderAsync<JudgeComparisonResults>(renderer, comparisonParameters),
            ["filter-jev", "filter-luna", "filter-both"], ["filter-match", "filter-failed"]);
        comparisonParameters[nameof(JudgeComparisonResults.OnlyDifferences)] = false;
        CheckRows(await RenderAsync<JudgeComparisonResults>(renderer, comparisonParameters), titles, []);
        Check(JudgeComparisonSummary.Calculate(comparison) == metrics, "Filtering preserves whole-run metrics");
        comparisonParameters[nameof(JudgeComparisonResults.Run)] = new JudgeComparisonRun(examples, DemoDecisionModels.DefaultModelId);
        comparisonParameters[nameof(JudgeComparisonResults.OnlyDifferences)] = true;
        Check((await RenderAsync<JudgeComparisonResults>(renderer, comparisonParameters)).Contains("No completed ratings differ from expected."), "Empty comparison filter message");
    }

    private static Task<string> RenderAsync<T>(HtmlRenderer renderer, Dictionary<string, object?> parameters) where T : IComponent =>
        renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());

    private static void CheckRows(string html, string[] included, string[] excluded)
    {
        foreach (var title in included) Check(html.Contains($">{title}</button>"), "Visible row " + title);
        foreach (var title in excluded) Check(!html.Contains($">{title}</button>"), "Hidden row " + title);
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("Failed: " + name); }

    private sealed class FilterDecisions : IDecisionsClient
    {
        private int Index;
        private readonly int?[] Ratings = [3, 2, 3, 2, null];
        public Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        {
            var rating = Ratings[Index++] ?? throw new InvalidOperationException("failed");
            return Task.FromResult(new JevResponse
            {
                Model = request.Model, Usage = new() { InputTokens = 0, OutputTokens = 0 },
                Answers = new Dictionary<string, JevAnswer> { [JudgeCatalog.QuestionId] = new ScoreAnswer
                {
                    Score = rating - 1, Confidence = 1, Legend = new Dictionary<string, string>(),
                    Probabilities = Enumerable.Range(0, 5).ToDictionary(level => level.ToString(), level => level == rating - 1 ? 1d : 0d)
                } }
            });
        }
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "JevDotNet.Demo";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetFullPath("JevDotNet.Demo");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
