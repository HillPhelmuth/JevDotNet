namespace JevDotNet.Demo.Services;

public sealed record LunaJudgeAttempt(JudgeExample Example, StoredLunaJudgeResult? Judgment, string? Error = null);
public sealed class JudgeComparisonRun(IReadOnlyList<JudgeExample> examples, string model)
{
    public JudgeRun Decisions { get; } = new(examples, model);
    public List<LunaJudgeAttempt> Luna { get; } = [];
    public bool Complete { get; internal set; }
    public bool Cancelled { get; internal set; }
}
public sealed record JudgeComparisonSummary(int Paired, int Agreed, double? MeanGap,
    int LunaSuccessful, int LunaReferenceAgreement, double? LunaReferenceError,
    TimeSpan DecisionTime, TimeSpan LunaOriginalTime, int Stored, int Live)
{
    public static JudgeComparisonSummary Calculate(JudgeComparisonRun run)
    {
        var luna = run.Luna.Where(item => item.Judgment is not null).ToArray();
        var pairs = run.Decisions.Attempts.Where(item => item.Result is not null)
            .Select(item => (Decision: item, Luna: luna.FirstOrDefault(other => other.Example.Id == item.Example.Id)))
            .Where(pair => pair.Luna is not null).ToArray();
        return new(pairs.Length, pairs.Count(pair => pair.Decision.Result!.Rating == pair.Luna!.Judgment!.Result.Rating),
            pairs.Length == 0 ? null : pairs.Average(pair => Math.Abs(pair.Decision.Result!.Rating - pair.Luna!.Judgment!.Result.Rating)),
            luna.Length, luna.Count(item => item.Judgment!.Result.Rating == item.Example.ExpectedScore),
            luna.Length == 0 ? null : luna.Average(item => Math.Abs(item.Judgment!.Result.Rating - item.Example.ExpectedScore)),
            TimeSpan.FromTicks(run.Decisions.Attempts.Sum(item => item.Result?.Elapsed.Ticks ?? 0)),
            TimeSpan.FromTicks(luna.Sum(item => item.Judgment!.Result.Elapsed.Ticks)),
            luna.Count(item => item.Judgment!.FromCache), luna.Count(item => !item.Judgment!.FromCache));
    }
}

public sealed class JudgeComparisonService(JudgeService judge, LunaJudgeCache cache, ILunaJudgeClient? luna = null)
{
    public async Task RunAsync(JudgeComparisonRun run, Func<Task>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (run.Luna.Count > 0 || run.Complete || run.Cancelled) throw new InvalidOperationException("Start a new comparison run.");
        await judge.RunAsync(run.Decisions, onProgress, cancellationToken);
        if (run.Decisions.Cancelled) { run.Cancelled = true; return; }
        try
        {
            foreach (var example in run.Decisions.Examples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LunaJudgeAttempt attempt;
                try
                {
                    var result = await cache.GetOrEvaluateAsync(LunaJudgeAgent.BuildRequest(example.Type, example.Input), luna, cancellationToken);
                    attempt = new(example, result);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or System.ClientModel.ClientResultException or
                    System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
                { attempt = new(example, null, error.Message); }
                cancellationToken.ThrowIfCancellationRequested();
                run.Luna.Add(attempt);
                if (onProgress is not null) await onProgress();
            }
            run.Complete = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { run.Cancelled = true; }
    }
}
