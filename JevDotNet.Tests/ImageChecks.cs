using System.Net;
using System.Text.Json;
using DecisionsDotNet;

internal static class ImageChecks
{
    private const string PngUrl = "data:image/png;base64,AQID";
    private const string JpegUrl = "data:image/jpeg;base64,BAUG";
    private static readonly IReadOnlyDictionary<string, DecisionsQuestion> Questions =
        new Dictionary<string, DecisionsQuestion> { ["visible"] = new NoulQuestion("Is the item visible?") };

    internal static async Task RunAsync()
    {
        CheckFactories();
        await CheckWireFormatsAsync();
        await CheckValidationAsync();
        await CheckModelSerializationAsync();
        await CheckCatalogAsync();
        await CheckDiscoveryIntegrationAsync();
        await CheckRetriesAndErrorsAsync();
    }

    private static void CheckFactories()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var image = DecisionsValue.ImageFromBytes(bytes, "image/png");
        bytes[0] = 9;
        Check(image.ToJsonElement().GetProperty("image_url").GetProperty("url").GetString() == PngUrl,
            "Byte helper snapshots the data");
        Check(JsonElement.DeepEquals(image.ToJsonElement(), DecisionsValue.ImageFromDataUrl(PngUrl).ToJsonElement()),
            "Data URL and byte helpers agree");
        var parts = new DecisionsValue[] { "Title", image };
        var state = DecisionsValue.FromParts(parts);
        parts[0] = "Changed";
        Check(state.ToJsonElement()[0].GetString() == "Title", "Parts snapshot caller array");
        foreach (var url in new[] { "", "https://example.com/a.png", "AQID", "data:image/gif;base64,AQID",
                     "data:image/png;base64,", "data:image/png;base64,!!!!", "data:image/png;base64,AQI",
                     "data:image/png;base64,AQ ID", "data:image/png;charset=utf8;base64,AQID" })
            Expect<ArgumentException>(() => DecisionsValue.ImageFromDataUrl(url));
        Expect<ArgumentNullException>(() => DecisionsValue.ImageFromDataUrl(null!));
        Expect<ArgumentException>(() => DecisionsValue.ImageFromBytes(ReadOnlyMemory<byte>.Empty, "image/png"));
        Expect<ArgumentException>(() => DecisionsValue.ImageFromBytes(new byte[] { 1 }, "image/gif"));
        Expect<ArgumentException>(() => DecisionsValue.FromParts(new DecisionsValue[] { null! }));
        Expect<ArgumentNullException>(() => DecisionsValue.FromParts(null!));
    }

    private static async Task CheckWireFormatsAsync()
    {
        // Small synthetic payloads exercise the wire contract; no image decoding occurs in the SDK.
        var png = DecisionsValue.ImageFromDataUrl(PngUrl);
        var jpeg = DecisionsValue.FromObject(new { type = "image_url", image_url = new { url = JpegUrl, detail = "high" } });
        var state = DecisionsValue.FromParts("Listing", png, DecisionsValue.FromObject(new { item = "sticker" }), jpeg);
        var original = state.ToJsonElement();
        using (var client = Router(message =>
        {
            using var body = Body(message);
            Check(JsonElement.DeepEquals(body.RootElement.GetProperty("state"), original), "OpenRouter preserves ordered image state");
            Check(!body.RootElement.TryGetProperty("images", out _) && !body.RootElement.TryGetProperty("assets", out _),
                "OpenRouter uses state image parts");
            Check(!message.Headers.Contains("TypeSafe-Danger-Zone-Images"), "TypeSafe header stays off OpenRouter");
            return Success();
        }, "image")) await client.EvaluateAsync(Request(state));

        using (var client = new JevClient("key", new HttpClient(new Handler((message, _) =>
        {
            using var body = Body(message);
            var root = body.RootElement;
            var sent = root.GetProperty("state");
            var assets = root.GetProperty("assets");
            Check(sent[0].GetString() == "Listing" && sent[2].GetProperty("item").GetString() == "sticker",
                "Direct context order preserved");
            Check(assets.EnumerateObject().Count() == 2, "Direct asset count");
            foreach (var index in new[] { 1, 3 })
            {
                var reference = sent[index].GetString()!;
                Check(Guid.TryParse(reference, out var parsedReference), "Direct references are UUIDs");
                var asset = assets.GetProperty(reference);
                Check(asset.GetProperty("type").GetString() == "image" &&
                    asset.GetProperty("format").GetString() == (index == 1 ? "png" : "jpg") &&
                    asset.GetProperty("data_base64").GetString() == (index == 1 ? "AQID" : "BAUG"), "Direct asset encoding");
            }
            Check(message.Headers.GetValues("TypeSafe-Danger-Zone-Images").Single() == "true", "Direct image header");
            return Task.FromResult(Success());
        })))) await client.EvaluateAsync(Request(state, "jev-latest"));
        Check(JsonElement.DeepEquals(state.ToJsonElement(), original), "Both clients preserve caller state");

        using var single = Router(message =>
        {
            using var body = Body(message);
            Check(body.RootElement.GetProperty("state").GetArrayLength() == 1, "Standalone image normalized");
            return Success();
        }, "image");
        await single.EvaluateAsync(Request(png));
        await single.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl("data:image/webp;base64,AQID")));
        await single.EvaluateAsync(Request(DecisionsValue.FromJson(JsonSerializer.SerializeToElement(new[] { png }))));
        await single.EvaluateAsync(Request(DecisionsValue.FromArray(new[] { png })));

        foreach (var model in new[] { "jev-1.13.0", "jev-1.14.0", "jev-latest", "jev-preview" })
        {
            using var direct = new JevClient("key", new HttpClient(new Handler((message, token) =>
            {
                using var body = Body(message);
                var sent = body.RootElement.GetProperty("state");
                Check(sent.GetArrayLength() == 1 && sent[0].ValueKind == JsonValueKind.String &&
                    body.RootElement.GetProperty("assets").TryGetProperty(sent[0].GetString()!, out var asset),
                    "Known direct model accepts standalone image");
                return Task.FromResult(Success());
            })));
            await direct.EvaluateAsync(Request(png, model));
        }
    }

    private static async Task CheckValidationAsync()
    {
        var image = DecisionsValue.ImageFromDataUrl(PngUrl);
        var nestedStates = new[]
        {
            DecisionsValue.FromObject(new { photo = image }),
            DecisionsValue.FromParts(DecisionsValue.FromParts(image)),
            DecisionsValue.FromParts(DecisionsValue.FromObject(new { photo = image }))
        };
        var imageQuestions = new DecisionsQuestion[]
        {
            new NoulQuestion(image),
            new NoulQuestion("Visible?", new NoulCriteria(True: image)),
            new ChoiceQuestion("Which?", new Dictionary<string, DecisionsValue?> { ["photo"] = image }),
            new ScoreQuestion("How close?", new DecisionsValue[] { "Low", image })
        };
        foreach (var direct in new[] { false, true })
        {
            using var http = new HttpClient(new Handler((_, _) => throw new Exception("Invalid input must not reach HTTP")));
            using var router = new OpenRouterDecisionsClient("key", http, RouterOptions("image"));
            using var jev = new JevClient("key", http);
            IDecisionsClient client = direct ? jev : router;
            foreach (var state in nestedStates)
                await ExpectAsync<NotSupportedException>(() => client.EvaluateAsync(Request(state)));
            foreach (var question in imageQuestions)
                await ExpectAsync<NotSupportedException>(() => client.EvaluateAsync(new DecisionsRequest("text",
                    new Dictionary<string, DecisionsQuestion> { ["visible"] = question })));
            foreach (var badImage in new[]
            {
                DecisionsValue.FromObject(new { type = "image_url", image_url = new { url = "https://example.com/a.png" } }),
                DecisionsValue.FromObject(new { type = "image_url", image_url = "broken" }),
                DecisionsValue.FromObject(new { type = "image_url", image_url = new { url = PngUrl, detail = "invalid" } })
            }) await ExpectAsync<ArgumentException>(() => client.EvaluateAsync(Request(badImage)));
        }

        using (var client = Router(_ => throw new Exception("Image must be rejected before evaluation"), "text"))
            await ExpectAsync<NotSupportedException>(() => client.EvaluateAsync(Request(image)));
        using (var client = new JevClient("key", new HttpClient(new Handler((_, _) => throw new Exception("No HTTP expected")))))
        {
            await ExpectAsync<NotSupportedException>(() => client.EvaluateAsync(Request(image, "unknown")));
            await ExpectAsync<NotSupportedException>(() => client.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl("data:image/webp;base64,AQID"), "jev-latest")));
        }
        var options = new JevClientOptions { ModelInputModalitiesOverrides = Overrides("jev-latest", "text") };
        using var textOnly = new JevClient("key", new HttpClient(new Handler((_, _) => throw new Exception("No HTTP expected"))), options);
        await ExpectAsync<NotSupportedException>(() => textOnly.EvaluateAsync(Request(image, "jev-latest")));
    }

    private static async Task CheckModelSerializationAsync()
    {
        var states = new[] { DecisionsValue.FromObject(new { text = "Help" }), DecisionsValue.FromParts("Help", DecisionsValue.FromObject(new { count = 2 })),
            DecisionsValue.Text(""), DecisionsValue.Text("Quotes: \"help\"\nUnicode: café 日本語 😀"), DecisionsValue.Text("{\"x\":1}") };
        foreach (var mode in new[] { "text", "image", "audio", "file" })
        foreach (var state in states)
        {
            using var client = Router(message =>
            {
                using var body = Body(message);
                var sent = body.RootElement.GetProperty("state");
                var original = state.ToJsonElement();
                if (mode == "text" && original.ValueKind != JsonValueKind.String)
                {
                    Check(sent.ValueKind == JsonValueKind.String, "Text-only structured state becomes text");
                    using var parsed = JsonDocument.Parse(sent.GetString()!);
                    Check(JsonElement.DeepEquals(parsed.RootElement, original), "JSON text preserves structure");
                }
                else Check(JsonElement.DeepEquals(sent, original), "Multimodal structure and plain strings preserved");
                return Success();
            }, mode);
            await client.EvaluateAsync(Request(state));
        }

        foreach (var model in new[] { "jev-latest", "unknown-direct", "configured-text" })
        {
            using var client = new JevClient("key", new HttpClient(new Handler((message, _) =>
            {
                using var body = Body(message);
                Check(body.RootElement.GetProperty("state").ValueKind ==
                    (model == "configured-text" ? JsonValueKind.String : JsonValueKind.Object), "Direct state capability selection");
                Check(!message.Headers.Contains("TypeSafe-Danger-Zone-Images") && !body.RootElement.TryGetProperty("assets", out var unusedAssets),
                    "Text request has no image header or assets");
                return Task.FromResult(Success());
            })), new JevClientOptions { ModelInputModalitiesOverrides = Overrides("configured-text", "text") });
            await client.EvaluateAsync(Request(DecisionsValue.FromObject(new { x = 1 }), model));
        }

        var configuredInputs = new[] { "text", "image" };
        var overrides = new Dictionary<string, IReadOnlyList<string>> { ["test/model"] = configuredInputs };
        using var configured = new OpenRouterDecisionsClient("key", new HttpClient(new Handler((message, _) =>
        {
            Check(message.Method == HttpMethod.Post, "Overrides bypass catalog discovery");
            return Task.FromResult(Success());
        })), new OpenRouterDecisionsClientOptions { ModelInputModalitiesOverrides = overrides });
        configuredInputs[1] = "text";
        overrides.Clear();
        await configured.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl(PngUrl)));

        using var directCustom = new JevClient("key", new HttpClient(new Handler((_, _) => Task.FromResult(Success()))),
            new JevClientOptions { ModelInputModalitiesOverrides = Overrides("custom", "image") });
        await directCustom.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl(PngUrl), "custom"));
    }

    private static async Task CheckCatalogAsync()
    {
        var clock = new TestClock();
        var catalog = new OpenRouterModelCatalog(clock);
        var baseUri = UniqueUri();
        var calls = 0;
        var fail = false;
        using var http = new HttpClient(new Handler((message, _) =>
        {
            calls++;
            Check(message.Method == HttpMethod.Get && message.RequestUri == new Uri(baseUri, "api/v1/models?output_modalities=decisions"),
                "Catalog endpoint");
            Check(message.Headers.Authorization is null, "Public catalog sends no credentials");
            return Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Catalog());
        }));
        Check((await catalog.ResolveAsync(http, baseUri, "test/model", default))!.Contains("image"), "Discover image support");
        Check((await catalog.ResolveAsync(http, baseUri, "test/model-pinned", default))!.Contains("image"), "Canonical slug lookup");
        Check(await catalog.ResolveAsync(http, baseUri, "missing", default) is null, "Unknown model unresolved");
        Check(await catalog.ResolveAsync(http, baseUri, "missing/modalities", default) is null, "Missing modalities unresolved");
        Check(calls == 1, "Cache reused across models");
        clock.Advance(TimeSpan.FromMinutes(14));
        await catalog.ResolveAsync(http, baseUri, "test/model", default);
        Check(calls == 1, "Cache lives for 15 minutes");
        fail = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        Check((await catalog.ResolveAsync(http, baseUri, "test/model", default))!.Contains("image"), "Failed refresh retains snapshot");
        await catalog.ResolveAsync(http, baseUri, "test/model", default);
        Check(calls == 2, "Failed refresh observes retry interval");
        clock.Advance(TimeSpan.FromMinutes(15));
        fail = false;
        await catalog.ResolveAsync(http, baseUri, "test/model", default);
        Check(calls == 3, "Refresh recovers after failure");

        clock.Advance(TimeSpan.FromMinutes(15));
        using var malformed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"data":[{"id":"test/model","architecture":{"input_modalities":["text"]}},{"id":null}]}
                """)
        })));
        Check((await catalog.ResolveAsync(malformed, baseUri, "test/model", default))!.Contains("image"),
            "Invalid refresh never publishes a partial snapshot");
    }

    private static async Task CheckDiscoveryIntegrationAsync()
    {
        var gets = 0;
        var posts = 0;
        var baseUri = UniqueUri();
        using var http = new HttpClient(new Handler((message, _) =>
        {
            if (message.Method == HttpMethod.Get) { gets++; return Task.FromResult(Catalog()); }
            posts++;
            using var body = Body(message);
            Check(body.RootElement.GetProperty("state").ValueKind == JsonValueKind.Array, "Discovered image model uses native state");
            return Task.FromResult(Success());
        }));
        using (var first = new OpenRouterDecisionsClient("key", http, new() { BaseUri = baseUri }))
            await first.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl(PngUrl)));
        using (var second = new OpenRouterDecisionsClient("other-key", http, new() { BaseUri = baseUri }))
            await second.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl(PngUrl), "test/model-pinned"));
        Check(gets == 1 && posts == 2, "Catalog shared across client instances and credentials");

        foreach (var catalogBody in new[] { "unparseable", "{}", "{\"data\":[]}", "{\"data\":[{\"id\":\"unknown\"}]}" })
        {
            var lookups = 0;
            var evaluations = 0;
            using var fallbackHttp = new HttpClient(new Handler((message, _) =>
            {
                if (message.Method == HttpMethod.Get)
                {
                    lookups++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(catalogBody) });
                }
                evaluations++;
                using var body = Body(message);
                Check(body.RootElement.GetProperty("state").ValueKind == JsonValueKind.String, "Unresolved model text fallback");
                return Task.FromResult(Success());
            }));
            using var fallback = new OpenRouterDecisionsClient("key", fallbackHttp, new() { BaseUri = UniqueUri() });
            await fallback.EvaluateAsync(Request(DecisionsValue.FromObject(new { x = 1 })));
            await ExpectAsync<NotSupportedException>(() => fallback.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl(PngUrl))));
            Check(lookups == 1 && evaluations == 1, "Unknown images rejected and failed lookup cached");
        }

        foreach (var failure in new[] { "http", "network", "timeout" })
        {
            var lookups = 0;
            using var fallbackHttp = new HttpClient(new Handler((message, token) =>
            {
                if (message.Method == HttpMethod.Post) return Task.FromResult(Success());
                lookups++;
                if (failure == "network") throw new HttpRequestException("offline");
                if (failure == "timeout") throw new TaskCanceledException("request timed out");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }));
            using var client = new OpenRouterDecisionsClient("key", fallbackHttp, new() { BaseUri = UniqueUri() });
            await client.EvaluateAsync(Request("text"));
            await ExpectAsync<NotSupportedException>(() => client.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl(PngUrl))));
            Check(lookups == 1, "Failed catalog lookup cooldown");
        }

        using var cancellation = new CancellationTokenSource();
        using var cancelHttp = new HttpClient(new Handler((message, token) =>
        {
            Check(message.Method == HttpMethod.Get, "Canceled discovery never evaluates");
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new Exception("Expected cancellation");
        }));
        using var cancelClient = new OpenRouterDecisionsClient("key", cancelHttp, new() { BaseUri = UniqueUri() });
        await ExpectAsync<OperationCanceledException>(() => cancelClient.EvaluateAsync(Request("text"), cancellation.Token));

        await CheckConcurrentCatalogAsync();
    }

    private static async Task CheckConcurrentCatalogAsync()
    {
        var clock = new TestClock();
        var cache = new OpenRouterModelCatalog(clock);
        var uri = UniqueUri();
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Catalog();
        }));
        var first = cache.ResolveAsync(http, uri, "test/model", default);
        await started.Task;
        var others = Enumerable.Range(0, 8).Select(_ => cache.ResolveAsync(http, uri, "test/model", default)).ToArray();
        using var waitingCancellation = new CancellationTokenSource();
        var waiting = cache.ResolveAsync(http, uri, "test/model", waitingCancellation.Token);
        waitingCancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(async () => await waiting);
        release.TrySetResult();
        await first;
        await Task.WhenAll(others);
        Check(calls == 1, "Concurrent lookups coalesce");
        await cache.ResolveAsync(http, UniqueUri(), "test/model", default);
        Check(calls == 2, "Catalog caches isolated by base URI");

        var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelHttp = new HttpClient(new Handler(async (_, token) =>
        {
            cancellationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Catalog();
        }));
        using var cancellation = new CancellationTokenSource();
        var canceledUri = UniqueUri();
        var fetch = cache.ResolveAsync(cancelHttp, canceledUri, "test/model", cancellation.Token);
        await cancellationStarted.Task;
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(async () => await fetch);
        Check((await cache.ResolveAsync(http, canceledUri, "test/model", default))!.Contains("image"),
            "Canceled fetch does not poison cache or impose cooldown");
    }

    private static async Task CheckRetriesAndErrorsAsync()
    {
        foreach (var direct in new[] { false, true })
        {
            var calls = 0;
            string? firstBody = null;
            using var http = new HttpClient(new Handler((message, _) =>
            {
                var body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                firstBody ??= body;
                Check(body == firstBody, "Image retry bodies and references are identical");
                return Task.FromResult(++calls == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Success());
            }));
            using var jev = new JevClient("key", http, new() { InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero });
            using var router = new OpenRouterDecisionsClient("key", http, RouterOptions("image"));
            IDecisionsClient client = direct ? jev : router;
            await client.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl(PngUrl), direct ? "jev-latest" : "test/model"));
            Check(calls == 2, "Image retries succeed");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => client.EvaluateAsync(Request("text"), canceled.Token));
        }
        foreach (var direct in new[] { false, true })
        foreach (var status in new[] { 400, 413, 422 })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StringContent("image rejected") })));
            using var jev = new JevClient("key", http);
            using var router = new OpenRouterDecisionsClient("key", http, RouterOptions("image"));
            IDecisionsClient client = direct ? jev : router;
            try
            {
                await client.EvaluateAsync(Request(DecisionsValue.ImageFromDataUrl(PngUrl), direct ? "jev-latest" : "test/model"));
                throw new Exception("Expected provider error");
            }
            catch (JevApiException error)
            {
                Check((int)error.StatusCode == status && error.ResponseBody == "image rejected" &&
                    error.Service == (direct ? "TypeSafe" : "OpenRouter"), "Image provider error retained");
            }
        }
    }

    private static DecisionsRequest Request(DecisionsValue state, string model = "test/model") => new(state, Questions, model);
    private static Uri UniqueUri() => new($"https://{Guid.NewGuid():N}.example/");
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Overrides(string model, params string[] inputs)
        => new Dictionary<string, IReadOnlyList<string>> { [model] = inputs };
    private static OpenRouterDecisionsClientOptions RouterOptions(string modality) => new()
    {
        BaseUri = UniqueUri(), ModelInputModalitiesOverrides = Overrides("test/model", "text", modality),
        InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero
    };
    private static OpenRouterDecisionsClient Router(Func<HttpRequestMessage, HttpResponseMessage> respond, string modality)
        => new("key", new HttpClient(new Handler((message, _) => Task.FromResult(respond(message)))), RouterOptions(modality));
    private static JsonDocument Body(HttpRequestMessage message)
        => JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
    private static HttpResponseMessage Catalog() => new(HttpStatusCode.OK) { Content = new StringContent("""
        {"data":[{"id":"test/model","canonical_slug":"test/model-pinned","architecture":{"input_modalities":["text","image"]}},
        {"id":"text/model","architecture":{"input_modalities":["text"]}},{"id":"missing/modalities"}]}
        """) };
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent("""
        {"model":"test/model","answers":{"visible":{"type":"noul","noul":0.9}},"usage":{"input_tokens":100,"output_tokens":0}}
        """) };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception($"Failed: {message}");
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
