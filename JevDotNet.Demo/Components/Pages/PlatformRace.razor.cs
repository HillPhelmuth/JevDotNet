using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DecisionsDotNet;
using JevDotNet.Demo.Components.Race;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace JevDotNet.Demo.Components.Pages;

public partial class PlatformRace
{
    private const string Clef = "cloudflare/clef";
    private const string ReadyMessage = "Ready to race.";

    private const string CodeExample = """
        using DecisionsDotNet;

        using var client = OpenRouterDecisionsClient.FromEnvironment();
        var request = DecisionsRequest.Create(DecisionsValue.FromObject(new
        {
            your_score = 0, opponent_score = 0, goal_score = 500
        }))
        .Choice("next_hop", "Both platforms are reachable. Pick the better climb.",
            ("p1", DecisionsValue.FromObject(new { climb_px = 78, horizontal_px = -94 })),
            ("p2", DecisionsValue.FromObject(new { climb_px = 156, horizontal_px = 53 })))
        .Build("typesafe/jev-1.14");

        var reply = await client.EvaluateAsync(request);
        var target = reply.GetAnswer<ChoiceAnswer>("next_hop").Choice;
        """;

    [Inject] private PlatformRaceDecisions Decisions { get; set; } = default!;
    [Inject] private ILogger<PlatformRace> Logger { get; set; } = default!;
    private IReadOnlyList<OpenRouterDecisionsModel> models = [];
    private RaceBoard board = default!;
    private PlatformRaceRound? round;
    private CancellationTokenSource? roundCancellation;
    private string left = "", right = "", status = ReadyMessage;
    private int nextRound;
    private long startedAt;
    private bool boardReady, changingBoard, disposed;
    private bool Locked => changingBoard || round?.Running == true;
    private bool CanStart => boardReady && !Locked && Decisions.Available && models.Count > 0;
    private string[] SelectedNames => [ModelName(left, "Left dog"), ModelName(right, "Right dog")];

    protected override void OnInitialized()
    {
        models = DemoDecisionModels.All;
        left = models.FirstOrDefault(model => model.Id == DemoDecisionModels.DefaultModelId)?.Id
            ?? models.FirstOrDefault()?.Id ?? "";
        right = models.FirstOrDefault(model => model.Id == Clef)?.Id
            ?? models.FirstOrDefault(model => model.Id != left)?.Id ?? left;
        if (right == left && models.FirstOrDefault(model => model.Id != left) is { } other) right = other.Id;
        if (models.Count == 0) status = "No Decisions models are available.";
    }

    private string ModelName(string id, string fallback) => models.FirstOrDefault(model => model.Id == id)?.Name ?? fallback;

    private void BoardReady(bool ready)
    {
        boardReady = ready;
        if (!ready) status = "The race board could not load. Reload the page to try again.";
    }

    private async Task StartAsync()
    {
        if (!CanStart || !models.Any(model => model.Id == left) || !models.Any(model => model.Id == right)) return;
        changingBoard = true;
        var current = new PlatformRaceRound(++nextRound, [left, right], SelectedNames);
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        round = current;
        roundCancellation = cancellation;
        startedAt = Stopwatch.GetTimestamp();
        status = "Race in progress.";
        try
        {
            if (await board.StartAsync(current.Id))
            {
                if (IsActive(current)) _ = WatchDeadlineAsync(current, token);
            }
            else await FinishAsync(current, "Return to this tab to start the race.");
        }
        catch (JSException)
        {
            await FinishAsync(current, "The race board lost its connection. Reload the page to try again.");
        }
        finally { changingBoard = false; }
    }

    private async Task WatchDeadlineAsync(PlatformRaceRound current, CancellationToken token)
    {
        try
        {
            var remaining = PlatformRaceDecisions.RoundLimit - Stopwatch.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
            await InvokeAsync(async () =>
            {
                if (!IsActive(current)) return;
                await FinishAsync(current, "Time limit reached.");
                if (!disposed) StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private bool IsActive(PlatformRaceRound current) => !disposed && round == current && current.Running;

    private async Task OnHopAsync(RaceHop hop)
    {
        if (round is not { } current || !IsActive(current) || !current.CanRequest(hop)) return;
        if (current.AtLimit(hop.Racer))
        {
            await FinishAsync(current, "Request limit reached.");
            return;
        }

        var token = roundCancellation!.Token;
        var row = current.Begin(hop, Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
        var started = Stopwatch.GetTimestamp();
        StateHasChanged();

        int? target;
        try
        {
            var request = PlatformRaceDecisions.BuildRequest(current.Models[hop.Racer], hop);
            var result = await Decisions.ChooseAsync(request, hop, token);
            if (!IsActive(current)) return;
            target = result.Target;
            row.Cost = result.Cost;
            row.Latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            row.Detail = $"Platform {target}";
            row.Status = RaceRequestStatus.Queued;
            StateHasChanged();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error) when (error is JevApiException or HttpRequestException or JsonException or
            InvalidOperationException or KeyNotFoundException or ArgumentException or OperationCanceledException)
        {
            if (!IsActive(current)) return;
            target = null;
            row.Latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            row.Detail = ErrorMessage(error);
            current.EndRequest(row, RaceRequestStatus.Error);
            Logger.LogDebug(error, "Platform Race decision {RequestId} failed", hop.Id);
            if (error is JevApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.PaymentRequired })
            {
                await FinishAsync(current, row.Detail);
                return;
            }
        }

        try
        {
            var queued = await board.QueueAsync(hop, target);
            if (!queued && IsActive(current) && row.Status == RaceRequestStatus.Queued)
                current.EndRequest(row, RaceRequestStatus.Dropped);
        }
        catch (JSException)
        {
            await FinishAsync(current, "The race board lost its connection.");
        }
    }

    private static string ErrorMessage(Exception error) => error switch
    {
        JevApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "OpenRouter rejected the API key or its permissions.",
        JevApiException { StatusCode: HttpStatusCode.PaymentRequired } => "OpenRouter reports insufficient credits.",
        JevApiException api => $"OpenRouter returned HTTP {(int)api.StatusCode}.",
        HttpRequestException => "Could not reach OpenRouter.",
        OperationCanceledException => "The request timed out.",
        _ => "The platform choice could not be read."
    };

    private async Task OnUpdateAsync(RaceUpdate update)
    {
        if (round is not { } current || !IsActive(current) || update.Run != current.Id ||
            update.Racers is not { Length: 2 } || update.Racers.Any(racer => racer is null) ||
            update.Transitions is null || update.Transitions.Any(transition => transition is null)) return;
        current.Update(update);
        if (update.Winner is 0 or 1)
            await FinishAsync(current, $"{(update.Winner == 0 ? "Left" : "Right")} dog wins! {current.Names[update.Winner.Value]} reached 500 points.");
        else if (update.Winner == -1)
            await FinishAsync(current, "Tie! Both dogs reached 500 points together.");
        else if (update.Hidden)
            await FinishAsync(current, "Race stopped when you left this tab.");
    }

    private Task StopAsync() => round is { } current ? FinishAsync(current, "Race stopped.") : Task.CompletedTask;

    private async Task ResetAsync()
    {
        if (changingBoard) return;
        changingBoard = true;
        try
        {
            await StopAsync();
            if (disposed) return;
            round = null;
            status = ReadyMessage;
            await board.ResetAsync();
        }
        catch (JSException) { status = "The race board could not reset. Reload the page to try again."; }
        finally { changingBoard = false; }
    }

    private async Task FinishAsync(PlatformRaceRound current, string message)
    {
        if (!IsActive(current)) return;
        current.Stop();
        CancelRequests();
        status = message;
        try { await board.StopAsync(current.Id); }
        catch (JSException) { }
    }

    private void CancelRequests()
    {
        roundCancellation?.Cancel();
        roundCancellation?.Dispose();
        roundCancellation = null;
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        CancelRequests();
        return ValueTask.CompletedTask;
    }
}
