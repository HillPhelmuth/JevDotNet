using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;
using System.Net;
using System.Text.Json;

namespace JevDotNet.Demo.Components.Pages
{
    public partial class Example
    {
        [Parameter] public string ScenarioId { get; set; } = string.Empty;

        private DemoScenario? Scenario;
        private string Input = string.Empty;
        private string SelectedModel = DemoDecisionModels.DefaultModelId;
        private string? ActiveSlug;
        private string? ValidationError;
        private string? ErrorMessage;
        private bool IsBusy;
        private DecisionsRequest? Request;
        private JevResponse? Response;
        private DemoRecommendation? Recommendation;
        private CancellationTokenSource? Cancellation;
        private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private IReadOnlyDictionary<string, JevQuestion> Questions
            => Scenario is null ? new Dictionary<string, JevQuestion>() : DemoScenarioCatalog.BuildRequest(Scenario.Slug, "Preview").Questions;
        private string? RequestJson => Request is null ? null : JsonSerializer.Serialize(Request, PrettyJson);
        private string? ResponseJson => Response is null ? null : JsonSerializer.Serialize(Response, PrettyJson);

        protected override void OnParametersSet()
        {
            Scenario = DemoScenarioCatalog.Find(ScenarioId);
            if (ActiveSlug == Scenario?.Slug) return;
            Cancellation?.Cancel();
            Cancellation = null;
            ActiveSlug = Scenario?.Slug;
            Input = Scenario?.SampleInput ?? string.Empty;
            SelectedModel = DemoDecisionModels.DefaultModelId;
            ValidationError = null;
            ErrorMessage = null;
            Request = null;
            Response = null;
            Recommendation = null;
            IsBusy = false;
        }

        private async Task EvaluateAsync()
        {
            if (IsBusy || Scenario is null || !Evaluation.HasApiKey) return;
            ValidationError = null;
            ErrorMessage = null;
            Request = null;
            Response = null;
            Recommendation = null;
            try { Request = DemoScenarioCatalog.BuildRequest(Scenario.Slug, Input, SelectedModel); }
            catch (ArgumentException error) { ValidationError = error.Message; return; }

            IsBusy = true;
            var cancellation = new CancellationTokenSource();
            Cancellation = cancellation;
            var token = cancellation.Token;
            var slug = Scenario.Slug;
            try
            {
                var response = await Evaluation.EvaluateAsync(Request, token);
                if (!token.IsCancellationRequested && ActiveSlug == slug)
                {
                    Response = response;
                    Recommendation = DemoScenarioCatalog.Recommend(slug, response);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                if (ActiveSlug == slug) ErrorMessage = "The request timed out. Try again.";
            }
            catch (JevApiException error)
            {
                if (ActiveSlug == slug) ErrorMessage = error.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "OpenRouter rejected the API key. Check the OpenRouter:ApiKey user secret.",
                    HttpStatusCode.PaymentRequired => "OpenRouter reports insufficient credits. Add credits to the account and try again.",
                    HttpStatusCode.BadRequest => $"OpenRouter rejected the request: {error.ResponseBody}",
                    HttpStatusCode.RequestEntityTooLarge => "The request is too large for OpenRouter. Shorten the input and try again.",
                    (HttpStatusCode)429 => "OpenRouter's rate limit was reached. Try again shortly.",
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or (HttpStatusCode)529 => "OpenRouter or its provider is temporarily unavailable. Try again shortly.",
                    _ => $"OpenRouter returned HTTP {(int)error.StatusCode}: {error.ResponseBody}"
                };
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException)
            {
                if (ActiveSlug == slug) ErrorMessage = error.Message;
            }
            finally
            {
                cancellation.Dispose();
                if (ReferenceEquals(Cancellation, cancellation))
                {
                    IsBusy = false;
                    Cancellation = null;
                }
            }
        }

        public void Dispose()
        {
            Cancellation?.Cancel();
        }

        private void OnModelChanged()
        {
            ValidationError = null;
            ErrorMessage = null;
            Request = null;
            Response = null;
            Recommendation = null;
        }

        private static string DisplayName(string value) => string.Join(' ', value.Split('_').Select(part =>
            part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
        private static string QuestionType(JevQuestion question) => question switch { ChoiceQuestion => "CHOICE", ScoreQuestion => "SCORE", _ => "NOUL" };
        private static string QuestionClass(JevQuestion question) => question switch { ChoiceQuestion => "choice", ScoreQuestion => "score", _ => "noul" };
        private static string AnswerType(JevAnswer answer) => answer switch { ChoiceAnswer => "CHOICE", ScoreAnswer => "SCORE", _ => "NOUL" };
        private static string AnswerClass(JevAnswer answer) => answer switch { ChoiceAnswer => "choice", ScoreAnswer => "score", _ => "noul" };
        private static int Percent(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
        private static string WidthStyle(double value) => $"width: {Percent(value)}%";
    }
}