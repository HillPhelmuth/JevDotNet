using System.Net;
using System.Text.Json;
using DecisionsDotNet;

internal static class BoxChecks
{
    private const string Answer = """
        {"type":"box","box":{"top":0.25,"bottom":0.82,"left":0.44,"right":0.84},"confidence":0.92}
        """;

    internal static async Task RunAsync()
    {
        try { _ = new BoxQuestion(null!); throw new Exception("Expected null instructions error"); }
        catch (ArgumentNullException) { }

        foreach (var direct in new[] { false, true })
        {
            var request = new DecisionsRequest(
                DecisionsValue.FromParts("Kitchen photo", DecisionsValue.ImageFromDataUrl("data:image/png;base64,AQID")),
                new Dictionary<string, DecisionsQuestion>
                {
                    ["mug"] = new BoxQuestion("Find the coffee mug in the photo."),
                    ["visible"] = new NoulQuestion("Is a coffee mug visible?")
                }, direct ? "jev-1.14.0" : "typesafe/jev-1.14");
            var calls = 0;
            string? firstBody = null;
            using var http = new HttpClient(new Handler(message =>
            {
                Check(message.Method == HttpMethod.Post, "Box evaluation method");
                var text = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                firstBody ??= text;
                Check(text == firstBody, "Box retry body is unchanged");
                using var body = JsonDocument.Parse(text);
                var question = body.RootElement.GetProperty("questions").GetProperty("mug");
                Check(question.GetProperty("type").GetString() == "box" &&
                    question.GetProperty("instructions").GetString() == "Find the coffee mug in the photo." &&
                    !question.TryGetProperty("criteria", out _), "Box question has instructions and no criteria");
                if (direct)
                {
                    Check(message.Headers.GetValues("TypeSafe-Danger-Zone-Images").Single() == "true", "Direct Box header");
                    Check(body.RootElement.GetProperty("assets").EnumerateObject().Count() == 1, "Box image asset");
                }
                else
                {
                    Check(!message.Headers.Contains("TypeSafe-Danger-Zone-Images"), "OpenRouter excludes direct header");
                    Check(body.RootElement.GetProperty("state")[1].GetProperty("type").GetString() == "image_url", "Box image part");
                }
                if (++calls == 1) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"model":"jev-1.14.0","answers":{"mug":
                    """ + Answer + """
                    ,"visible":{"type":"noul","noul":0.98}},"usage":{"input_tokens":290,"output_tokens":21}}
                    """) };
            }));
            using var jev = new JevClient("key", http, new() { InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero });
            using var router = new OpenRouterDecisionsClient("key", http, new()
            {
                InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero,
                ModelInputModalitiesOverrides = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["typesafe/jev-1.14"] = new[] { "text", "image" }
                }
            });
            IDecisionsClient client = direct ? jev : router;
            var response = await client.EvaluateAsync(request);
            var answer = response.GetAnswer<BoxAnswer>("mug");
            Check(answer.Box.Top == 0.25 && answer.Box.Bottom == 0.82 && answer.Box.Left == 0.44 &&
                answer.Box.Right == 0.84 && answer.Confidence == 0.92, "Typed Box coordinates and confidence");
            Check(response.GetAnswer<NoulAnswer>("visible").Noul == 0.98 && calls == 2, "Mixed Box and Noul request");
            var roundTrip = JsonSerializer.Deserialize<JevAnswer>(JsonSerializer.Serialize<JevAnswer>(answer));
            Check(roundTrip is BoxAnswer box && box == answer, "Box answer round trip");
        }

        var questions = new Dictionary<string, DecisionsQuestion> { ["mug"] = new BoxQuestion("Find the mug.") };
        foreach (var invalid in new[]
        {
            "{\"type\":\"box\",\"confidence\":0.9}",
            Answer.Replace("\"box\":{\"top\":0.25,\"bottom\":0.82,\"left\":0.44,\"right\":0.84}", "\"box\":null"),
            Answer.Replace(",\"confidence\":0.92", ""),
            Answer.Replace("\"top\":0.25,", ""),
            Answer.Replace("\"bottom\":0.82,", ""),
            Answer.Replace("\"left\":0.44,", ""),
            Answer.Replace(",\"right\":0.84", ""),
            Answer.Replace("0.25", "-0.1"), Answer.Replace("0.82", "1.1"),
            Answer.Replace("0.44", "0.9"), Answer.Replace("0.84", "0.44"),
            Answer.Replace("0.25", "0.82"), Answer.Replace("0.92", "1.01"),
            Answer.Replace("0.92", "-0.01"), Answer.Replace("0.25", "1e400"),
            "{\"type\":\"noul\",\"noul\":0.9}"
        })
        {
            ExpectProtocolError(Response(invalid), questions);
        }
        ExpectProtocolError(Response(Answer), new Dictionary<string, DecisionsQuestion> { ["mug"] = new NoulQuestion("Visible?") });
        foreach (var confidence in new[] { "0", "1" })
        {
            var fullImage = Answer.Replace("0.25", "0").Replace("0.82", "1").Replace("0.44", "0")
                .Replace("0.84", "1").Replace("0.92", confidence);
            var answer = JevResponseValidator.DeserializeAndValidate(Response(fullImage), questions).GetAnswer<BoxAnswer>("mug");
            Check(answer.Box.Left == 0 && answer.Box.Right == 1, "Box accepts inclusive interval boundaries");
        }

        // Box itself requires the direct header even when the server will need to reject missing image input.
        using var headerHttp = new HttpClient(new Handler(message =>
        {
            Check(message.Headers.Contains("TypeSafe-Danger-Zone-Images"), "Box without image still enables required header");
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Box requires an image") };
        }));
        using var headerClient = new JevClient("key", headerHttp);
        try { await headerClient.EvaluateAsync(new DecisionsRequest("no image", questions, "jev-preview")); }
        catch (JevApiException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.ResponseBody == "Box requires an image")
        { return; }
        throw new Exception("Expected provider Box validation error");
    }

    private static string Response(string answer) => """
        {"model":"jev-1.14.0","answers":{"mug":
        """ + answer + """
        },"usage":{"input_tokens":290,"output_tokens":21}}
        """;

    private static void ExpectProtocolError(string json, IReadOnlyDictionary<string, DecisionsQuestion> questions)
    {
        try { JevResponseValidator.DeserializeAndValidate(json, questions); }
        catch (JsonException) { return; }
        throw new Exception("Expected invalid Box response to fail validation");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception($"Failed: {message}");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
