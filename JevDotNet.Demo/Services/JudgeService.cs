using System.Diagnostics;
using System.Text.Json;
using JevDotNet;

namespace JevDotNet.Demo.Services;

public sealed record JudgeAttempt(JudgeExample Example, JudgeResult? Result, string? Error = null);
public sealed record JudgeSummary(int Successful, int Agreed, double? MeanAbsoluteError)
{
    public static JudgeSummary Calculate(IReadOnlyList<JudgeAttempt> attempts)
    {
        var valid = attempts.Where(attempt => attempt.Result is not null).ToArray();
        return new(valid.Length, valid.Count(attempt => attempt.Result!.Rating == attempt.Example.ExpectedScore),
            valid.Length == 0 ? null : valid.Average(attempt => Math.Abs(attempt.Result!.Rating - attempt.Example.ExpectedScore)));
    }
}

public sealed class JudgeRun
{
    public JudgeRun(IReadOnlyList<JudgeExample> examples, string model)
    {
        if (examples.Count == 0 || examples.Select(item => item.Type).Distinct().Count() != 1 ||
            examples.Select(item => item.Id).Distinct().Count() != examples.Count)
            throw new ArgumentException("Select unique examples from one evaluator.", nameof(examples));
        Examples = Array.AsReadOnly(examples.ToArray());
        Model = DemoDecisionModels.RequireSupported(model);
    }

    public IReadOnlyList<JudgeExample> Examples { get; }
    public string Model { get; }
    private readonly List<JudgeAttempt> _attempts = [];
    public IReadOnlyList<JudgeAttempt> Attempts => _attempts.AsReadOnly();
    internal void Add(JudgeAttempt attempt) => _attempts.Add(attempt);
    public bool Complete { get; internal set; }
    public bool Cancelled { get; internal set; }
}

public sealed class JudgeService(DemoEvaluationService evaluation, IWebHostEnvironment environment)
{
    public bool HasApiKey => evaluation.HasApiKey;
    public IReadOnlyList<JudgeExample> LoadExamples() => JudgeCatalog.Load(Path.Combine(environment.ContentRootPath, "Data", "judge-data.json"));

    public async Task<JudgeResult> EvaluateAsync(string type, JudgeInput input,
        string model = DemoDecisionModels.DefaultModelId, CancellationToken cancellationToken = default)
    {
        var request = JudgeCatalog.BuildRequest(type, input, model);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var response = await evaluation.EvaluateAsync(request, cancellationToken);
        stopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        return JudgeCatalog.ReadResult(response, stopwatch.Elapsed);
    }

    public async Task RunAsync(JudgeRun run, Func<Task>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (!HasApiKey) throw new InvalidOperationException("Configure the OpenRouter:ApiKey user secret before evaluating.");
        if (run.Attempts.Count > 0 || run.Complete || run.Cancelled) throw new InvalidOperationException("Start a new run to evaluate the dataset again.");
        try
        {
            foreach (var example in run.Examples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JudgeAttempt attempt;
                try
                {
                    var result = await EvaluateAsync(example.Type, example.Input, run.Model, cancellationToken);
                    attempt = new(example, result);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is JevApiException or HttpRequestException or JsonException or
                    InvalidOperationException or ArgumentException or OperationCanceledException)
                {
                    attempt = new(example, null, error is OperationCanceledException ? "The request timed out. Try again." : error.Message);
                }
                cancellationToken.ThrowIfCancellationRequested();
                run.Add(attempt);
                if (onProgress is not null) await onProgress();
            }
            cancellationToken.ThrowIfCancellationRequested();
            run.Complete = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { run.Cancelled = true; }
    }
}
