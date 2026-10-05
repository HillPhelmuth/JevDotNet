using System.Text.Json;

namespace JevDotNet.Demo.Services;

public static class JudgeComparisonReport
{
    public static async Task RunAsync(JudgeService judge, JudgeComparisonService comparison, string path)
    {
        var examples = judge.LoadExamples();
        var runs = new List<JudgeComparisonRun>();
        foreach (var evaluator in JudgeCatalog.All)
        {
            var run = new JudgeComparisonRun(examples.Where(item => item.Type == evaluator.Key)
                .OrderBy(item => item.ExpectedScore).ThenBy(item => item.Id).ToArray(), DemoDecisionModels.DefaultModelId);
            await comparison.RunAsync(run);
            runs.Add(run);
            var summary = JudgeComparisonSummary.Calculate(run);
            var decisions = JudgeSummary.Calculate(run.Decisions.Attempts);
            Console.WriteLine($"{evaluator.Name}: paired {summary.Paired}, judge agreement {summary.Agreed}/{summary.Paired}, " +
                $"Decisions/reference {decisions.Agreed}/{decisions.Successful}, Luna/reference {summary.LunaReferenceAgreement}/{summary.LunaSuccessful}, " +
                $"Luna live {summary.Live}, stored {summary.Stored}");
        }
        var report = new
        {
            createdAt = DateTimeOffset.UtcNow, decisionModel = DemoDecisionModels.DefaultModelId, lunaModel = LunaJudgeAgent.ModelId,
            evaluators = runs.Select(run => new
            {
                type = run.Decisions.Examples[0].Type, summary = JudgeComparisonSummary.Calculate(run),
                decisions = JudgeSummary.Calculate(run.Decisions.Attempts),
                items = run.Decisions.Examples.Select(example => new
                {
                    id = example.Id, expected = example.ExpectedScore,
                    decision = run.Decisions.Attempts.FirstOrDefault(item => item.Example.Id == example.Id),
                    luna = run.Luna.FirstOrDefault(item => item.Example.Id == example.Id)
                })
            })
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var failed = runs.Sum(run => run.Decisions.Attempts.Count(item => item.Error is not null) + run.Luna.Count(item => item.Error is not null));
        Console.WriteLine($"Comparison report: {path}. Failed judgments: {failed}.");
        if (failed > 0) Environment.ExitCode = 1;
    }
}
