using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace JevDotNet.Demo.Components.Pages
{
    public partial class Chase
    {
        private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private ChaseGame Game = new();
        private ChaseDecisionRate Rate = new();
        private string SelectedModel = DemoDecisionModels.DefaultModelId;
        private ElementReference Board;
        private CancellationTokenSource? Session;
        private bool IsRunning;
        private bool IsThinking;
        private bool HasCost;
        private bool Disposed;
        private int RequestsSent;
        private int InputTokens;
        private int OutputTokens;
        private decimal TotalCost;
        private string StatusMessage = "Ready. Start the chase to make the first live decision.";
        private string? ErrorMessage;
        private DecisionsRequest? LastRequest;
        private JevResponse? LastResponse;

        private async Task StartAsync()
        {
            if (IsRunning || !Evaluation.HasApiKey || Game.IsRoundComplete) return;
            ErrorMessage = null;
            IsRunning = true;
            StatusMessage = "The model is choosing a move…";
            var session = new CancellationTokenSource();
            Session = session;
            _ = RunChaseAsync(session);
            await Board.FocusAsync();
        }

        private async Task RunChaseAsync(CancellationTokenSource session)
        {
            var token = session.Token;
            try
            {
                while (!token.IsCancellationRequested && !Game.IsRoundComplete)
                {
                    var turnStarted = Stopwatch.GetTimestamp();
                    var request = Game.BuildRequest(SelectedModel);
                    LastRequest = request;
                    IsThinking = true;
                    await InvokeAsync(StateHasChanged);

                    RequestsSent++;
                    var response = await Evaluation.EvaluateAsync(request, token);
                    if (token.IsCancellationRequested || Game.IsCaught) break;

                    var choice = response.GetAnswer<ChoiceAnswer>("move");
                    Game.ApplyChaserMove(choice.Choice);
                    Rate.Record(Stopwatch.GetElapsedTime(turnStarted));
                    LastResponse = response;
                    InputTokens += response.Usage.InputTokens;
                    OutputTokens += response.Usage.OutputTokens;
                    if (response.Usage.Cost is { } cost)
                    {
                        TotalCost += cost;
                        HasCost = true;
                    }
                    IsThinking = false;
                    StatusMessage = Game.IsCaught
                        ? $"The chaser caught you in {Game.ChaserMoves} moves. Reset to play again."
                        : Game.IsRoundComplete
                            ? $"The {ChaseGame.MaxChaserMoves}-move round is complete. Reset to play again."
                            : $"The chaser moved {choice.Choice}. Keep running!";
                    await InvokeAsync(StateHasChanged);
                    var remaining = ChaseDecisionRate.TurnInterval - Stopwatch.GetElapsedTime(turnStarted);
                    if (!Game.IsRoundComplete && remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (JevApiException error)
            {
                if (!token.IsCancellationRequested)
                    ErrorMessage = error.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "OpenRouter rejected the API key. Check the OpenRouter:ApiKey user secret.",
                        HttpStatusCode.PaymentRequired => "OpenRouter reports insufficient credits. Add credits to the account and try again.",
                        HttpStatusCode.TooManyRequests => "OpenRouter's rate limit was reached. Resume shortly.",
                        _ => $"OpenRouter returned HTTP {(int)error.StatusCode}: {error.ResponseBody}"
                    };
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException)
            {
                if (!token.IsCancellationRequested) ErrorMessage = error.Message;
            }
            finally
            {
                session.Dispose();
                if (ReferenceEquals(Session, session))
                {
                    Session = null;
                    IsRunning = false;
                    IsThinking = false;
                    if (ErrorMessage is not null) StatusMessage = "The chase is paused. Check the error above, then resume.";
                    if (!Disposed) await InvokeAsync(StateHasChanged);
                }
            }
        }

        private void Pause()
        {
            Session?.Cancel();
            IsRunning = false;
            IsThinking = false;
            if (!Game.IsRoundComplete) StatusMessage = "Paused. Resume when you're ready.";
        }

        private void Reset()
        {
            Pause();
            Session = null;
            Game = new ChaseGame();
            Rate = new ChaseDecisionRate();
            RequestsSent = 0;
            InputTokens = 0;
            OutputTokens = 0;
            TotalCost = 0;
            HasCost = false;
            LastRequest = null;
            LastResponse = null;
            ErrorMessage = null;
            StatusMessage = "Ready. Start the chase to make the first live decision.";
        }

        private async Task MoveRunnerAsync(string direction)
        {
            MoveRunner(direction);
            await Board.FocusAsync();
        }

        private void MoveRunner(string direction)
        {
            if (!IsRunning || !Game.TryMoveRunner(direction)) return;
            if (Game.IsCaught)
            {
                Pause();
                StatusMessage = "You ran into the chaser. Reset to play again.";
            }
        }

        private void HandleKeyDown(KeyboardEventArgs args)
        {
            var direction = args.Key switch
            {
                "ArrowUp" => "up",
                "ArrowDown" => "down",
                "ArrowLeft" => "left",
                "ArrowRight" => "right",
                _ => null
            };
            if (direction is not null) MoveRunner(direction);
        }

        private string CellLabel(GridPosition position)
        {
            var label = $"Column {position.Column}, row {position.Row}";
            if (ChaseGame.IsWall(position)) return label + ", wall";
            if (position == Game.Runner && position == Game.Chaser) return label + ", runner caught by the chaser";
            if (position == Game.Runner) return label + ", runner";
            if (position == Game.Chaser) return label + ", model chaser";
            return label + ", empty";
        }

        private static int Percent(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 100);

        public void Dispose()
        {
            Disposed = true;
            Session?.Cancel();
        }
    }
}