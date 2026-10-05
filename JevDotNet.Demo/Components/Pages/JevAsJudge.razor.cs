using System.Text.Json;
using JevDotNet.Demo.Services;

namespace JevDotNet.Demo.Components.Pages;

public partial class JevAsJudge
{
    private IReadOnlyList<JudgeExample> AllExamples = [];
    private IReadOnlyList<JudgeExample> Examples = [];
    private string Type = "relevance";
    private string Model = DemoDecisionModels.DefaultModelId;
    private JudgeEvaluator Evaluator => JudgeCatalog.Find(Type);
    private JudgeExample? Example;
    private JudgeInput Input = new("", "");
    private bool Modified;
    private JudgeResult? Result;
    private JudgeRun? Run;
    private JudgeComparisonRun? ComparisonRun;
    private CancellationTokenSource? Cancellation;
    private bool IsBusy;
    private bool DatasetBusy;
    private bool BulkSelected;
    private bool JevDetailsSelected;
    private bool Disposed;
    private string? LoadError;
    private string? Error;
    private string? Status;

    protected override void OnInitialized()
    {
        try { AllExamples = Judge.LoadExamples(); EvaluatorChanged(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { LoadError = error.Message; }
    }

    private void EvaluatorChanged()
    {
        Run = null;
        ComparisonRun = null;
        Examples = AllExamples.Where(example => example.Type == Type).OrderBy(example => example.ExpectedScore).ThenBy(example => example.Id).ToArray();
        SelectExample(Examples[0].Id);
    }

    private void ModelChanged() { Run = null; ComparisonRun = null; Result = null; Error = null; Status = null; }
    private void InspectExample(string id)
    {
        if (IsBusy || Disposed) return;
        SelectExample(id);
        BulkSelected = false;
    }
    private void SelectExample(string id)
    {
        if (IsBusy || Disposed) return;
        Example = Examples.First(example => example.Id == id);
        Input = Example.Input with { };
        Modified = false;
        Result = Run?.Attempts.FirstOrDefault(attempt => attempt.Example.Id == id)?.Result;
        Error = null;
        Status = null;
    }
    private void InputChanged(JudgeInput input)
    {
        if (IsBusy || Disposed) return;
        Input = input;
        Modified = true;
        Result = null;
        Error = null;
        Status = null;
    }
    private void ResetExample()
    {
        if (IsBusy || Example is null) return;
        Input = Example.Input with { };
        Modified = false;
        Result = null;
        Error = null;
        Status = null;
    }

    private async Task JudgeResponseAsync()
    {
        if (IsBusy || Disposed || !Judge.HasApiKey || Example is null) return;
        Error = null;
        Result = null;
        try { JudgeCatalog.ValidateInput(Evaluator, Input); }
        catch (ArgumentException error) { Error = error.Message; return; }
        using var cancellation = BeginOperation(false);
        try
        {
            var result = await Judge.EvaluateAsync(Type, Input, Model, cancellation.Token);
            if (!Disposed && !cancellation.IsCancellationRequested) { Result = result; Status = "Response judged."; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!Disposed) Status = "Request cancelled."; }
        catch (Exception error) when (error is JevApiException or HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (!Disposed) Error = error is OperationCanceledException ? "The request timed out. Try again." : error.Message;
        }
        finally { EndOperation(); }
    }

    private async Task CompareDatasetAsync()
    {
        if (IsBusy || Disposed || !Judge.HasApiKey || Examples.Count == 0) return;
        Error = null;
        Result = null;
        var run = new JudgeComparisonRun(Examples, Model);
        ComparisonRun = run;
        Run = run.Decisions;
        using var cancellation = BeginOperation(true);
        Status = "Comparing original examples with GPT-6 Luna…";
        try
        {
            await Comparison.RunAsync(run, () =>
            {
                if (Disposed) return Task.CompletedTask;
                return InvokeAsync(() => { if (!Disposed) StateHasChanged(); });
            }, cancellation.Token);
            if (!Disposed) Status = run.Cancelled ? "Comparison cancelled. Stored judgments are preserved." : "Comparison complete.";
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { if (!Disposed) Error = error.Message; }
        finally { EndOperation(); }
    }

    private CancellationTokenSource BeginOperation(bool dataset)
    {
        IsBusy = true;
        DatasetBusy = dataset;
        Status = dataset ? $"Running the original {Examples.Count} examples…" : "Judging response…";
        Cancellation = new CancellationTokenSource();
        return Cancellation;
    }
    private void EndOperation()
    {
        Cancellation = null;
        if (!Disposed) { IsBusy = false; DatasetBusy = false; }
    }
    private void Cancel() => Cancellation?.Cancel();
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Cancellation?.Cancel();
        // The operation owns its CTS and disposes it as it unwinds.
        return ValueTask.CompletedTask;
    }
}
