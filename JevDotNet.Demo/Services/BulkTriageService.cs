using System.Diagnostics;
using JevDotNet;

namespace JevDotNet.Demo.Services;

public enum JevBulkMode { Batched, OneAtATime }

public sealed record TriageCallResult(TriageScores? Scores, TimeSpan Elapsed, string? Error = null,
    int? RequestNumber = null);
public sealed record JevRequestTiming(int Number, string FirstMessageId, string LastMessageId,
    int MessageCount, TimeSpan Elapsed, string? Error = null);

public sealed class BulkTriageRun(
    IReadOnlyList<BulkTriageMessage> messages,
    JevBulkMode mode,
    string jevModel = DemoDecisionModels.DefaultModelId,
    int batchSize = 95)
{
    public IReadOnlyList<BulkTriageMessage> Messages { get; } = messages;
    public JevBulkMode Mode { get; } = mode;
    public string JevModel { get; } = DemoDecisionModels.RequireSupported(jevModel);
    public int BatchSize { get; } = mode switch
    {
        JevBulkMode.OneAtATime => 1,
        JevBulkMode.Batched when batchSize is >= 2 and <= 95 => batchSize,
        _ => throw new ArgumentOutOfRangeException(nameof(batchSize), "Enter 2 to 95 messages per request.")
    };
    public Dictionary<string, TriageCallResult> Jev { get; } = [];
    public Dictionary<string, TriageCallResult> Luna { get; } = [];
    public List<JevRequestTiming> JevRequests { get; } = [];
    public TimeSpan JevTotal { get; internal set; }
    public TimeSpan LunaTotal { get; internal set; }
    public string? JevError { get; internal set; }
    public bool Complete { get; internal set; }
    public bool Cancelled { get; internal set; }
}

public sealed class BulkTriageService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IDecisionsClient? decisionsClient = null,
    ILunaTriageClient? lunaClient = null)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(configuration["OpenRouter:ApiKey"])
        && decisionsClient is not null && lunaClient is not null;

    public IReadOnlyList<BulkTriageMessage> LoadMessages()
        => BulkTriageCatalog.Load(Path.Combine(environment.ContentRootPath, "Data", "table-data.json"));

    public async Task RunAsync(BulkTriageRun run, Func<Task>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            throw new InvalidOperationException("Configure the OpenRouter:ApiKey user secret before evaluating.");
        if (run.Messages.Count == 0)
            throw new ArgumentException("The run needs messages.", nameof(run));

        var messagesPerRequest = run.Mode == JevBulkMode.Batched ? run.BatchSize : 1;
        foreach (var batch in run.Messages.Chunk(messagesPerRequest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestNumber = run.JevRequests.Count + 1;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await decisionsClient!.EvaluateAsync(
                    BulkTriageCatalog.BuildJevRequest(batch, run.JevModel), cancellationToken);
                stopwatch.Stop();
                var scores = batch.Select(message => (message.Id,
                    Scores: BulkTriageCatalog.ReadJevScores(response, message.Id))).ToArray();
                foreach (var answer in scores)
                    run.Jev[answer.Id] = new(answer.Scores, stopwatch.Elapsed, RequestNumber: requestNumber);
                run.JevRequests.Add(new(requestNumber, batch[0].Id, batch[^1].Id,
                    batch.Length, stopwatch.Elapsed));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is JevApiException or HttpRequestException or
                System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException)
            {
                stopwatch.Stop();
                run.JevRequests.Add(new(requestNumber, batch[0].Id, batch[^1].Id,
                    batch.Length, stopwatch.Elapsed, error.Message));
                if (run.Mode == JevBulkMode.Batched)
                {
                    run.JevTotal += stopwatch.Elapsed;
                    run.JevError = $"Decisions request {requestNumber} ({batch[0].Id}–{batch[^1].Id}) failed: {error.Message}";
                    if (onProgress is not null) await onProgress();
                    return;
                }
                run.Jev[batch[0].Id] = new(null, stopwatch.Elapsed, error.Message,
                    RequestNumber: requestNumber);
            }
            run.JevTotal += stopwatch.Elapsed;
            if (onProgress is not null) await onProgress();
        }
        var messageSets = run.Messages.Chunk(20);
        foreach (var chunk in messageSets)
        {
            var tasks = chunk.Select(message =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                return EvaluateMessage(run, onProgress, message, stopwatch, cancellationToken);
                
            }).ToArray();
            await Task.WhenAll(tasks);
        }
        //foreach (var message in run.Messages)
        //{
        //    cancellationToken.ThrowIfCancellationRequested();
        //    var stopwatch = Stopwatch.StartNew();
        //    try
        //    {
        //        await EvaluateMessage(run, onProgress, message, stopwatch, cancellationToken);
        //    }
        //    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        //    catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or
        //        InvalidOperationException or TaskCanceledException or System.ClientModel.ClientResultException)
        //    {
        //        stopwatch.Stop();
        //        run.Luna[message.Id] = new(null, stopwatch.Elapsed, error.Message);
        //    }
        //    run.LunaTotal += stopwatch.Elapsed;
        //    if (onProgress is not null) await onProgress();
        //}
        run.Complete = true;
    }

    private async Task EvaluateMessage(BulkTriageRun run, Func<Task>? onProgress, BulkTriageMessage message, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var scores = await lunaClient!.EvaluateAsync(message, cancellationToken);
        scores.Validate();
        stopwatch.Stop();
        run.Luna[message.Id] = new(scores, stopwatch.Elapsed);
        run.LunaTotal += stopwatch.Elapsed;
        if (onProgress is not null) await onProgress();
    }
}

public sealed record CategoryAgreement(string Key, string Label, int Agreed, int Compared,
    double MeanAbsoluteGapPoints);
public sealed record MessageAgreement(string Id, string Text, IReadOnlyList<string> DisagreedCategories,
    double MeanAbsoluteGapPoints);
public sealed record BulkTriageComparison(int Agreed, int Compared, double MeanAbsoluteGapPoints,
    IReadOnlyList<CategoryAgreement> Categories, IReadOnlyList<MessageAgreement> Messages)
{
    public static BulkTriageComparison Calculate(BulkTriageRun run)
    {
        var pairs = run.Messages.Select(message =>
        {
            run.Jev.TryGetValue(message.Id, out var jev);
            run.Luna.TryGetValue(message.Id, out var luna);
            return (message, jev: jev?.Scores, luna: luna?.Scores);
        }).Where(pair => pair.jev is not null && pair.luna is not null).ToArray();

        var categories = BulkTriageCatalog.Categories.Select(category =>
        {
            var gaps = pairs.Select(pair => Math.Abs(
                pair.jev!.Get(category.Key) - pair.luna!.Get(category.Key)) * 100).ToArray();
            var agreed = pairs.Count(pair =>
                (pair.jev!.Get(category.Key) >= 0.5) == (pair.luna!.Get(category.Key) >= 0.5));
            return new CategoryAgreement(category.Key, category.Label, agreed, pairs.Length,
                gaps.Length == 0 ? 0 : gaps.Average());
        }).ToArray();

        var messages = pairs.Select(pair =>
        {
            var disagreed = BulkTriageCatalog.Categories.Where(category =>
                (pair.jev!.Get(category.Key) >= 0.5) != (pair.luna!.Get(category.Key) >= 0.5))
                .Select(category => category.Label).ToArray();
            var gap = BulkTriageCatalog.Categories.Average(category => Math.Abs(
                pair.jev!.Get(category.Key) - pair.luna!.Get(category.Key)) * 100);
            return new MessageAgreement(pair.message.Id, pair.message.Text, disagreed, gap);
        }).ToArray();
        var compared = categories.Sum(category => category.Compared);
        return new BulkTriageComparison(categories.Sum(category => category.Agreed), compared,
            compared == 0 ? 0 : categories.Sum(category => category.MeanAbsoluteGapPoints * category.Compared) / compared,
            categories, messages);
    }
}
