using System.Globalization;
using System.Net;
using System.Buffers.Binary;
using System.Text.Json;
using JevDotNet.Demo.Components.Images;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DecisionsDotNet;

internal static class ImageDemoChecks
{
    internal static async Task RunAsync()
    {
        using var http = new HttpClient(new CatalogHandler());
        await DemoDecisionModels.RefreshAsync(http);
        Check(ImageDemoCatalog.SupportedModels(false).Select(model => model.Id).SequenceEqual(new[] { "vision/model", "typesafe/jev-1.14" }),
            "Image demos exclude text-only models");
        Check(ImageDemoCatalog.SupportedModels(true).Select(model => model.Id).SequenceEqual(new[] { "typesafe/jev-1.14" }),
            "Box demo excludes image models without known Box support");
        foreach (var image in ImageDemoCatalog.All)
        {
            Check(image.Bytes.Span.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Embedded demo image bytes");
            var original = FileHelper.ExtractFileBytesFromAssembly(image.Id == "breakfast"
                ? "Rustic Half-Eaten Breakfast Table.png" : "New York - Alien Invasion.png");
            var offset = 8;
            while (!original.AsSpan(offset + 4, 4).SequenceEqual("IEND"u8))
                offset += (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(offset, 4)) + 12;
            var pngEnd = offset + 12;
            Check(image.Bytes.Length == pngEnd && image.Bytes.Span.SequenceEqual(original.AsSpan(0, pngEnd)),
                "Demo removes trailing PNG data without changing any PNG chunks or pixels");
            var analysis = ImageDemoCatalog.BuildRequest(image.Id, "vision/model", image.Question, false);
            Check(analysis.Questions["question"] is NoulQuestion && analysis.Questions["scene"] is ChoiceQuestion &&
                analysis.Questions["activity"] is ScoreQuestion, "Image analysis question types");
            Check(analysis.State.ToJsonElement()[1].GetProperty("image_url").GetProperty("url").GetString()!
                .StartsWith("data:image/png;base64,", StringComparison.Ordinal), "Demo sends inline image parts");
            Check(analysis.State.ToJsonElement()[0].GetString() == image.Question, "Image question is plain text state context");
            var box = ImageDemoCatalog.BuildRequest(image.Id, "typesafe/jev-1.14", image.Target, true);
            Check(box.Questions["target"] is BoxQuestion && box.Questions["visible"] is NoulQuestion,
                "Localization asks Box and presence questions together");
            await CheckOutgoingRequestAsync(analysis, image);
            await CheckOutgoingRequestAsync(box, image);
            ExpectArgument(() => ImageDemoCatalog.BuildRequest(image.Id, "text/model", image.Question, false));
            ExpectArgument(() => ImageDemoCatalog.BuildRequest(image.Id, "vision/model", image.Target, true));
            ExpectArgument(() => ImageDemoCatalog.BuildRequest(image.Id, "vision/model", " ", false));
            ExpectArgument(() => ImageDemoCatalog.BuildRequest(image.Id, "vision/model", new string('x', 1001), false));
        }
        ExpectArgument(() => ImageDemoCatalog.BuildRequest("missing", "vision/model", "Question?", false));
        try { FileHelper.ExtractFileBytesFromAssembly("missing.png"); throw new Exception("Expected missing resource error"); }
        catch (FileNotFoundException) { }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<DemoEvaluationService>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var imageAsset = ImageDemoCatalog.All[0];
        var bounds = new BoundingBox { Left = 0.1, Top = 0.2, Right = 0.7, Bottom = 0.8 };
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var overlay = await RenderAsync<ImagePreview>(renderer, new() { ["Image"] = imageAsset, ["Box"] = bounds });
            Check(overlay.Contains("x=\"100\"") && overlay.Contains("y=\"200\"") && overlay.Contains("width=\"600\"") &&
                overlay.Contains("height=\"600\"") && overlay.Contains("preserveAspectRatio=\"none\""), "Overlay maps normalized coordinates independently of culture");
            var preview = await RenderAsync<ImagePreview>(renderer, new() { ["Image"] = imageAsset });
            Check(!preview.Contains("<svg") && preview.Contains(imageAsset.PreviewUrl), "Preview has no box before evaluation");
        }
        finally { CultureInfo.CurrentCulture = culture; }

        foreach (var locate in new[] { false, true })
        {
            var html = await RenderAsync<ImageDemoWorkspace>(renderer, new() { ["Locate"] = locate });
            Check(html.Contains("OpenRouter API key needed") && html.Contains("disabled"), "Image pages show missing-key setup and disable evaluation");
            Check(html.Contains(locate ? "Find target" : "Evaluate image") && html.Contains("typesafe/jev-1.14") &&
                !html.Contains("Text only"), "Image page uses compatible model list");
            Check(!locate || !html.Contains("Other vision model"), "Box page only offers known Box models");
            Check(html.Contains(locate ? imageAsset.Target : imageAsset.Question) && !html.Contains("field-error"),
                "Demo controls receive the default prompt and no initial error");
        }

        var response = new DecisionsResponse
        {
            Model = "typesafe/jev-1.14", Answers = new Dictionary<string, JevAnswer>
            {
                ["target"] = new BoxAnswer { Box = bounds, Confidence = 0.9 },
                ["visible"] = new NoulAnswer { Noul = 0.95 }
            }, Usage = new DecisionsUsage { InputTokens = 100, OutputTokens = 0, Cost = 0.0001m }
        };
        var result = await RenderAsync<ImageDecisionResults>(renderer, new()
        {
            ["Response"] = response,
            ["Questions"] = ImageDemoCatalog.BuildRequest(imageAsset.Id, "typesafe/jev-1.14", imageAsset.Target, true).Questions,
            ["Elapsed"] = TimeSpan.FromSeconds(1)
        });
        Check(result.Contains("90.0") && result.Contains("10.0") && result.Contains("View response JSON"), "Box result shows confidence, coordinates, and JSON");

        using var textOnly = new HttpClient(new CatalogHandler(textOnly: true));
        await DemoDecisionModels.RefreshAsync(textOnly);
        var empty = await RenderAsync<ImageDemoWorkspace>(renderer, new() { ["Locate"] = true });
        Check(empty.Contains("No compatible models available") && empty.Contains("disabled"), "Empty image-model catalog disables evaluation");
    }

    private static async Task CheckOutgoingRequestAsync(DecisionsRequest request, ImageDemoAsset image)
    {
        using var http = new HttpClient(new OutgoingImageHandler(request, image));
        using var client = new OpenRouterDecisionsClient("test-key", http, new()
        {
            ModelInputModalitiesOverrides = new Dictionary<string, IReadOnlyList<string>>
            {
                [request.Model] = new[] { "text", "image" }
            }
        });
        try { await client.EvaluateAsync(request); }
        catch (JevApiException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.ResponseBody == "fixture") { return; }
        throw new Exception("Expected fixture response");
    }

    private static Task<string> RenderAsync<T>(HtmlRenderer renderer, Dictionary<string, object?> parameters) where T : IComponent
        => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });

    private static void ExpectArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Expected invalid demo input error");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }

    private sealed class OutgoingImageHandler(DecisionsRequest expected, ImageDemoAsset image) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/alpha/decisions", "Image demo API endpoint");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            Check(root.GetProperty("model").GetString() == expected.Model, "Image demo wire model");
            var state = root.GetProperty("state");
            Check(state.ValueKind == JsonValueKind.Array && state.GetArrayLength() == 2 && state[0].ValueKind == JsonValueKind.String,
                "Image demo wire state matches text-plus-image example");
            var part = state[1];
            Check(part.GetProperty("type").GetString() == "image_url" && part.EnumerateObject().Count() == 2,
                "Image demo wire image_url part");
            var url = part.GetProperty("image_url").GetProperty("url").GetString()!;
            Check(url.StartsWith("data:image/png;base64,", StringComparison.Ordinal) &&
                Convert.FromBase64String(url[22..]).AsSpan().SequenceEqual(image.Bytes.Span), "Wire base64 is the complete cleaned PNG");
            Check(!root.TryGetProperty("images", out _) && !root.TryGetProperty("assets", out _), "No unsupported top-level media fields");
            Check(JsonElement.DeepEquals(root.GetProperty("questions"), JsonSerializer.SerializeToElement(expected.Questions)),
                "Image demo wire question discriminators and instructions");
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("fixture") };
        }
    }

    private sealed class CatalogHandler(bool textOnly = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(textOnly ? """
                {"data":[{"id":"text/model","name":"Text only","architecture":{"input_modalities":["text"]}}]}
                """ : """
                {"data":[{"id":"text/model","name":"Text only","architecture":{"input_modalities":["text"]}},
                {"id":"vision/model","name":"Other vision model","architecture":{"input_modalities":["text","image"]}},
                {"id":"typesafe/jev-1.14","name":"Jev 1.14","architecture":{"input_modalities":["text","image"]}}]}
                """) });
    }
}
