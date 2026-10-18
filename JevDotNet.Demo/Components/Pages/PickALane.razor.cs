using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DecisionsDotNet;
using JevDotNet.Demo.Components.Lanes;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace JevDotNet.Demo.Components.Pages;

public partial class PickALane
{
    [Inject] private DemoEvaluationService Evaluation { get; set; } = default!;
    private LaneRoad Road = default!;
    private bool Ready, Running, Started, Starting, Complete, Thinking, Disposed;
    private string Model = DemoDecisionModels.DefaultModelId;
    private int Seconds = 3, SessionId, Decisions, Requests, InputTokens, OutputTokens;
    private double LatencyMs;
    private long LastRequestStarted;
    private decimal? Cost;
    private string Status = "Ready. Start driving to let the model take the wheel.";
    private string? Error;
    private LaneSnapshot Snapshot = new(0, false, false, 2, 0, 0, []);
    private DecisionsRequest? Request;
    private DecisionsResponse? Response;
    private CancellationTokenSource? Session;
    private readonly List<Task> Loops = [];
    private void RoadReady() => Ready = true;

    private async Task StartAsync()
    {
        if (!Ready || Starting || Running || Complete || !Evaluation.HasApiKey) return;
        Starting = true;
        var id = ++SessionId;
        try
        {
            // A canceled provider can still be finishing. Never overlap evaluations across resumes/resets.
            await Task.WhenAll(Loops);
            if (Disposed || id != SessionId) return;
            if (!Started) Snapshot = await Road.ResetAsync(id, Seconds);
            if (Disposed || id != SessionId) return;
            if (!await Road.StartAsync(id)) { Status = "Return to this browser tab to start driving."; return; }
            if (Disposed || id != SessionId) return;
            Started = Running = true;
            Error = null;
            Status = "Driving. The model is choosing a lane…";
            var session = new CancellationTokenSource();
            Session = session;
            Loops.RemoveAll(t => t.IsCompleted);
            Loops.Add(RunAsync(id, session));
        }
        finally { Starting = false; }
    }

    private bool Active(int id) => !Disposed && Running && id == SessionId;

    private async Task RunAsync(int id, CancellationTokenSource session)
    {
        var token = session.Token;
        try
        {
            while (Active(id))
            {
                if (LastRequestStarted != 0)
                {
                    var wait = LaneDecision.Interval - Stopwatch.GetElapsedTime(LastRequestStarted);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                }
                if (!Active(id)) break;
                var turn = Stopwatch.GetTimestamp();
                var snapshot = await Road.SnapshotAsync();
                if (!Active(id)) break;
                Snapshot = snapshot;
                if (!snapshot.Running) { await FinishAsync(snapshot); break; }
                Request = LaneDecision.BuildRequest(snapshot, Model);
                Thinking = true;
                LastRequestStarted = Stopwatch.GetTimestamp();
                Requests++;
                StateHasChanged();
                var response = await Evaluation.EvaluateAsync(Request, token);
                if (!Active(id) || token.IsCancellationRequested) break;
                var answer = response.GetAnswer<ChoiceAnswer>("lane");
                var lane = LaneDecision.Validate(answer);
                if (!await Road.SteerAsync(id, lane))
                {
                    if (Active(id))
                    {
                        var stopped = await Road.SnapshotAsync();
                        if (Active(id)) await FinishAsync(stopped);
                    }
                    break;
                }
                if (!Active(id)) break;
                Response = response;
                LatencyMs = Stopwatch.GetElapsedTime(turn).TotalMilliseconds;
                Decisions++;
                InputTokens += response.Usage.InputTokens;
                OutputTokens += response.Usage.OutputTokens;
                if (response.Usage.Cost is { } cost) Cost = (Cost ?? 0) + cost;
                Snapshot = await Road.SnapshotAsync();
                if (!Active(id)) break;
                if (!Snapshot.Running) { await FinishAsync(Snapshot); StateHasChanged(); break; }
                Thinking = false;
                Status = $"Model chose lane {lane}. {Snapshot.Passed} barriers passed.";
                if (Decisions >= LaneDecision.MaxDecisions)
                {
                    Snapshot = await Road.PauseAsync(id);
                    if (Active(id))
                    {
                        if (Snapshot.Crashed) await FinishAsync(Snapshot);
                        else { Complete = true; Running = false; Status = $"100-decision round complete. {Snapshot.Passed} barriers passed. Reset to drive again."; }
                    }
                }
                StateHasChanged();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is JevApiException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or JSException)
        {
            if (Active(id))
            {
                Error = error is JevApiException api ? api.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "OpenRouter rejected the API key. Check OpenRouter:ApiKey.",
                    HttpStatusCode.PaymentRequired => "OpenRouter reports insufficient credits.",
                    HttpStatusCode.TooManyRequests => "OpenRouter's rate limit was reached. Resume shortly.",
                    _ => $"OpenRouter returned HTTP {(int)api.StatusCode}."
                } : error.Message;
                await PauseAsync();
            }
        }
        finally
        {
            if (ReferenceEquals(Session, session)) { Session = null; Thinking = false; }
            session.Dispose();
            if (!Disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task FinishAsync(LaneSnapshot snapshot)
    {
        Snapshot = snapshot;
        if (snapshot.Crashed)
        {
            Complete = true;
            Status = $"Crash! {snapshot.Passed} barriers passed in {snapshot.ElapsedMs / 1000:0.0} seconds. Reset to drive again.";
        }
        else Status = "Paused. Resume when ready.";
        Running = Thinking = false;
        Session?.Cancel();
        await Task.CompletedTask;
    }

    private async Task HandleRoadEventAsync(LaneEvent update)
    {
        if (Disposed || update.SessionId != SessionId || !Running) return;
        await FinishAsync(update.Snapshot);
        if (update.Reason == "hidden") Status = "Paused because this browser tab was hidden. Resume when ready.";
    }

    private async Task PauseAsync()
    {
        Running = Thinking = false;
        Session?.Cancel();
        Session = null;
        var id = SessionId;
        var snapshot = await Road.PauseAsync(id);
        if (id != SessionId || Disposed) return;
        Snapshot = snapshot;
        if (snapshot.Crashed) await FinishAsync(snapshot);
        else Status = Error is null ? "Paused. Resume when ready." : "Paused. Check the error above, then resume.";
    }

    private async Task ResetAsync()
    {
        Running = Thinking = false;
        Session?.Cancel(); Session = null;
        var id = ++SessionId;
        Snapshot = await Road.ResetAsync(id, Seconds);
        Started = Complete = false;
        Decisions = Requests = InputTokens = OutputTokens = 0;
        LatencyMs = 0; Cost = null; Request = null; Response = null; Error = null;
        Status = "Ready. Start driving to let the model take the wheel.";
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true; Running = false;
        Session?.Cancel(); Session = null;
        await Task.WhenAll(Loops);
    }
}
