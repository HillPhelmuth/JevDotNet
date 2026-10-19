using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DecisionsDotNet;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;

namespace JevDotNet.Demo.Components.Images;

public partial class ImageDemoWorkspace
{
    [Parameter] public bool Locate { get; set; }
    private string Title => Locate ? "Find it in the image" : "Image decisions";
    private IReadOnlyList<OpenRouterDecisionsModel> Models => ImageDemoCatalog.SupportedModels(Locate);
    private string ImageId = ImageDemoCatalog.All[0].Id;
    private ImageDemoAsset Image => ImageDemoCatalog.Find(ImageId)!;
    private string ModelId = string.Empty;
    private string Prompt = string.Empty;
    private bool? ActiveMode;
    private bool Busy;
    private bool ShowBox = true;
    private bool Disposed;
    private string? Error;
    private DecisionsRequest? Request;
    private DecisionsResponse? Response;
    private TimeSpan Elapsed;
    private CancellationTokenSource? Cancellation;

    protected override void OnParametersSet()
    {
        if (ActiveMode == Locate) return;
        ActiveMode = Locate;
        ModelId = Models.FirstOrDefault(model => model.Id == DemoDecisionModels.DefaultModelId)?.Id
            ?? Models.FirstOrDefault()?.Id ?? string.Empty;
        ChangeImage(ImageDemoCatalog.All[0].Id);
    }

    private void ClearResult()
    {
        Request = null;
        Response = null;
        Error = null;
        ShowBox = true;
    }

    private void ChangeImage(string id)
    {
        if (Busy || ImageDemoCatalog.Find(id) is not { } image) return;
        ImageId = id;
        Prompt = Locate ? image.Target : image.Question;
        ClearResult();
    }

    private void ChangeModel(string id) { if (!Busy) { ModelId = id; ClearResult(); } }
    private void ChangePrompt(string prompt) { if (!Busy) { Prompt = prompt; ClearResult(); } }

    private async Task EvaluateAsync()
    {
        if (Busy || !Evaluation.HasApiKey || Disposed) return;
        ClearResult();
        try { Request = ImageDemoCatalog.BuildRequest(ImageId, ModelId, Prompt, Locate); }
        catch (ArgumentException error) { Error = error.Message; return; }

        Busy = true;
        using var cancellation = new CancellationTokenSource();
        Cancellation = cancellation;
        var timer = Stopwatch.StartNew();
        try
        {
            var response = await Evaluation.EvaluateAsync(Request, cancellation.Token);
            if (!Disposed && !cancellation.IsCancellationRequested) Response = response;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!Disposed) Error = "Evaluation canceled. You can try again.";
        }
        catch (OperationCanceledException error)
        {
            Logger.LogWarning(error, "Image evaluation timed out for {Model}", ModelId);
            if (!Disposed) Error = "The model took too long to respond. Try again.";
        }
        catch (JevApiException error)
        {
            Logger.LogWarning(error, "Image evaluation failed with HTTP {Status} for {Model}", (int)error.StatusCode, ModelId);
            if (!Disposed) Error = error.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "OpenRouter rejected the API key. Check the server-side configuration.",
                HttpStatusCode.PaymentRequired => "OpenRouter reports insufficient credits.",
                HttpStatusCode.RequestEntityTooLarge => "This image is too large for the selected provider. Try a different image or model.",
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "The provider rejected this image or question type. Try another compatible model or image.",
                HttpStatusCode.TooManyRequests => "The provider's rate limit was reached. Try again shortly.",
                _ => "The provider could not evaluate this image. Try again or select a different model."
            };
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or NotSupportedException)
        {
            Logger.LogWarning(error, "Image evaluation failed for {Model}", ModelId);
            if (!Disposed) Error = "The image could not be evaluated. Try again or choose a different model.";
        }
        finally
        {
            Elapsed = timer.Elapsed;
            Busy = false;
            Cancellation = null;
        }
    }

    private async Task CancelAsync()
    {
        if (Cancellation is { } cancellation) await cancellation.CancelAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        await CancelAsync();
    }
}
