using System.Net;
using System.Text.Json;
using ExtractionsDotNet;

public sealed class ExtractionsTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExtractsScalarAndListFieldsWithMetadata()
    {
        const string state = "🧾 INV-7, due tomorrow, total $100.";
        using var http = new HttpClient(new StubHandler(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://router.example/api/alpha/extractions", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", message.Headers.Authorization.Parameter);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);

            AssertBody(message, """
                {
                  "model": "cloudflare/clairic",
                  "state": "🧾 INV-7, due tomorrow, total $100.",
                  "fields": {
                    "InvoiceID": {"type":"text"},
                    "total": {"type":"number","decimals":2,"integer":false,"unit":"USD"},
                    "due": {"type":"date","instructions":"Due date"},
                    "time": {"type":"time"},
                    "appointment": {"type":"datetime"},
                    "payment_period": {"type":"duration","unit":"days","integer":true},
                    "contacts": {"type":"list","items":"text"}
                  },
                  "reference_date": "2026-10-01",
                  "provider": {
                    "only": ["Cloudflare"],
                    "allow_fallbacks": false,
                    "options": {"cloudflare":{"return_state_text":true}},
                    "data_collection": "deny"
                  },
                  "user": "example-user",
                  "session_id": "session",
                  "trace": {"trace_id":"trace","CustomKey":0}
                }
                """);

            return JsonResponse("""
                {
                  "id": "gen-1",
                  "model": "cloudflare/clairic",
                  "provider": "Cloudflare",
                  "state_text": "🧾 INV-7, due tomorrow, total $100.",
                  "truncated": true,
                  "usage": {
                    "input_tokens":20,"output_tokens":0,"cost":0.000003,
                    "state_tokens":10,"image_tokens":0,"schema_tokens":10,"passes":2
                  },
                  "answers": {
                    "InvoiceID":{"value":"INV-7","span":[2,7],"type":"text","confidence":1},
                    "total":{"type":"number","value":null,"guess":100,"p_null":0.75},
                    "due":{"type":"date","value":"2026-10-02"},
                    "time":{"type":"time","value":"14:30"},
                    "appointment":{
                      "type":"datetime","value":"2026-10-02T14:30:00-05:00","guess":"2026-10-02T14:30:00"
                    },
                    "payment_period":{"type":"duration","value":10,"unit":"days"},
                    "contacts":{
                      "type":"list","value":["INV-7","Ada"],"spans":[[2,7],null],
                      "confidences":[1,0.9],"source":"image","spell_confidence":0.9
                    }
                  }
                }
                """);
        }));
        using var client = new OpenRouterExtractionsClient("test-key", http, new Uri("https://router.example"));
        var result = await client.ExtractAsync(new ExtractionRequest("cloudflare/clairic",
            new Dictionary<string, ExtractionField>
            {
                ["InvoiceID"] = new TextField(),
                ["total"] = new NumberField { Decimals = 2, Integer = false, Unit = "USD" },
                ["due"] = new DateField { Instructions = "Due date" },
                ["time"] = new TimeField(),
                ["appointment"] = new DateTimeField(),
                ["payment_period"] = new DurationField { Unit = DurationUnit.Days, Integer = true },
                ["contacts"] = new ListField(ListItemType.Text)
            })
        {
            State = state,
            ReferenceDate = new DateOnly(2026, 10, 1),
            Provider = new ExtractionProviderOptions
            {
                Only = ["Cloudflare"],
                AllowFallbacks = false,
                Options = new Dictionary<string, object?>
                {
                    ["cloudflare"] = new { return_state_text = true }
                },
                AdditionalProperties = new Dictionary<string, JsonElement>
                {
                    ["data_collection"] = JsonSerializer.SerializeToElement("deny")
                }
            },
            User = "example-user",
            SessionId = "session",
            Trace = new Dictionary<string, object?> { ["trace_id"] = "trace", ["CustomKey"] = 0 }
        }, TestCancellation);

        Assert.Equal("cloudflare/clairic", result.Model);
        Assert.Equal("gen-1", result.Id);
        Assert.Equal("Cloudflare", result.Provider);
        Assert.Equal(state, result.StateText);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.Usage.Passes);
        Assert.Equal(0.000003m, result.Usage.Cost);

        var invoice = Assert.IsType<ExtractionAnswer<string>>(result.Answers["InvoiceID"]);
        Assert.Equal("INV-7", invoice.Value);
        Assert.Equal(new ExtractionSpan(2, 7), invoice.Span);
        Assert.Equal(1, invoice.Confidence);

        var total = Assert.IsType<ExtractionAnswer<double?>>(result.Answers["total"]);
        Assert.Null(total.Value);
        Assert.Equal(100, total.Guess);
        Assert.Equal(0.75, total.NullProbability);
        Assert.Equal(new DateOnly(2026, 10, 2), Assert.IsType<ExtractionAnswer<DateOnly?>>(result.Answers["due"]).Value);
        Assert.Equal(new TimeOnly(14, 30), Assert.IsType<ExtractionAnswer<TimeOnly?>>(result.Answers["time"]).Value);
        var appointment = Assert.IsType<ExtractionAnswer<ExtractionDateTime?>>(result.Answers["appointment"]);
        Assert.Equal(TimeSpan.FromHours(-5), appointment.Value!.Value.Offset);
        Assert.Equal(new DateTime(2026, 10, 2, 14, 30, 0), appointment.Guess!.Value.LocalDateTime);
        Assert.Null(appointment.Guess.Value.Offset);
        var duration = Assert.IsType<ExtractionDurationAnswer>(result.Answers["payment_period"]);
        Assert.Equal(10, duration.Value);
        Assert.Equal("days", duration.Unit);
        var contacts = Assert.IsType<ExtractionListAnswer<string>>(result.Answers["contacts"]);
        Assert.Equal(["INV-7", "Ada"], contacts.Value);
        Assert.Equal(new ExtractionSpan?[] { new(2, 7), null }, contacts.Spans);
        Assert.Equal(new[] { 1d, 0.9 }, contacts.Confidences);
        Assert.Equal("image", contacts.Source);
        Assert.Equal(0.9, contacts.SpellConfidence);
    }

    [Fact]
    public async Task ExtractsNumericAndCalendarDistributionsAndLists()
    {
        var fields = new Dictionary<string, ExtractionField>
        {
            ["estimate"] = new EstimateField(0, 100) { Step = 0.5, Scale = ExtractionScale.Linear },
            ["calendar_estimate"] = new EstimateField(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1))
            {
                CalendarStep = CalendarStep.Month
            },
            ["forecast"] = new ForecastField(0, 100, 2, ForecastInterval.Week),
            ["calendar_forecast"] = new ForecastField(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), 1, ForecastInterval.Month),
            ["numbers"] = new ListField(ListItemType.Number) { Decimals = 2 },
            ["dates"] = new ListField(ListItemType.Date)
        };
        using var http = new HttpClient(new StubHandler(message =>
        {
            AssertBody(message, """
                {
                  "model": "cloudflare/clairic",
                  "state": "History",
                  "fields": {
                    "estimate": {"type":"estimate","min":0,"max":100,"step":0.5,"scale":"linear"},
                    "calendar_estimate": {"type":"estimate","min":"2026-01-01","max":"2027-01-01","step":"month"},
                    "forecast": {"type":"forecast","min":0,"max":100,"horizon":2,"interval":"week"},
                    "calendar_forecast": {"type":"forecast","min":"2026-01-01","max":"2027-01-01","horizon":1,"interval":"month"},
                    "numbers": {"type":"list","items":"number","decimals":2},
                    "dates": {"type":"list","items":"date"}
                  }
                }
                """);
            return JsonResponse("""
                {
                  "model": "cloudflare/clairic",
                  "usage": {"input_tokens":20,"output_tokens":0},
                  "answers": {
                    "estimate": {
                      "type":"estimate","value":20,"mean":20.2,"median":20,
                      "p10":10,"p90":30,"p_below":0,"p_above":0.01,"quantiles":[10,20,30]
                    },
                    "calendar_estimate":{"type":"estimate","value":"2026-06-01","median":"2026-05-01"},
                    "forecast": {
                      "type":"forecast","value":[20,23],"guess":[20,23],"p10":[15,20],"p90":[25,27],
                      "steps":[{"value":20,"median":21,"p_above":0.01},{"value":null,"mean":23}]
                    },
                    "calendar_forecast": {
                      "type":"forecast","value":["2026-06-01"],
                      "steps":[{"value":"2026-06-01","quantiles":["2026-05-01","2026-07-01"]}]
                    },
                    "numbers":{"type":"list","value":[10.25,11]},
                    "dates":{"type":"list","value":null,"guess":["2026-06-01"]}
                  }
                }
                """);
        }));
        using var client = new OpenRouterExtractionsClient("test-key", http);
        var result = await client.ExtractAsync(new ExtractionRequest("cloudflare/clairic", fields) { State = "History" }, TestCancellation);

        var estimate = Assert.IsType<ExtractionEstimateAnswer<double>>(result.Answers["estimate"]);
        Assert.Equal(20.2, estimate.Mean);
        Assert.Equal(0.01, estimate.PAbove);
        Assert.Equal(new double[] { 10, 20, 30 }, estimate.Quantiles);
        var calendarEstimate = Assert.IsType<ExtractionEstimateAnswer<DateOnly>>(result.Answers["calendar_estimate"]);
        Assert.Equal(new DateOnly(2026, 5, 1), calendarEstimate.Median);
        var forecast = Assert.IsType<ExtractionForecastAnswer<double>>(result.Answers["forecast"]);
        Assert.Equal(new double[] { 20, 23 }, forecast.Value);
        Assert.Equal(new double[] { 15, 20 }, forecast.P10);
        Assert.Equal(0.01, forecast.Steps![0].PAbove);
        Assert.Null(forecast.Steps[1].Value);
        var calendarForecast = Assert.IsType<ExtractionForecastAnswer<DateOnly>>(result.Answers["calendar_forecast"]);
        Assert.Equal(new DateOnly(2026, 6, 1), Assert.Single(calendarForecast.Value!));
        Assert.Equal(new DateOnly(2026, 7, 1), Assert.Single(calendarForecast.Steps!).Quantiles![1]);
        Assert.Equal(new[] { 10.25, 11 }, Assert.IsType<ExtractionListAnswer<double>>(result.Answers["numbers"]).Value);
        var dates = Assert.IsType<ExtractionListAnswer<DateOnly>>(result.Answers["dates"]);
        Assert.Null(dates.Value);
        Assert.Equal(new DateOnly(2026, 6, 1), Assert.Single(dates.Guess!));
    }

    [Theory]
    [InlineData(ExtractionAssetFormat.Png, "png", "image", "cloudflare/clairic")]
    [InlineData(ExtractionAssetFormat.Jpeg, "jpeg", "image", "cloudflare/clairic")]
    [InlineData(ExtractionAssetFormat.Webp, "webp", "image", "cloudflare/clairic")]
    [InlineData(ExtractionAssetFormat.Pdf, "pdf", "document", "scaledown/extract")]
    public async Task EncodesAssetOnlyRequests(ExtractionAssetFormat format, string wireFormat, string type, string model)
    {
        using var http = new HttpClient(new StubHandler(message =>
        {
            AssertBody(message, $$"""
                {
                  "model": "{{model}}",
                  "state": "asset-1",
                  "fields": {"value":{"type":"text"} },
                  "assets": {"asset-1":{"format":"{{wireFormat}}","type":"{{type}}","data_base64":"+/8="} }
                }
                """);
            return JsonResponse($$"""
                {
                  "model":"{{model}}","usage":{"input_tokens":3,"output_tokens":0},
                  "answers":{"value":{"type":"text","value":"Invoice","source":"{{type}}" } }
                }
                """);
        }));
        using var client = new OpenRouterExtractionsClient("test-key", http);
        var result = await client.ExtractAsync(TextRequest(model) with
        {
            State = "",
            Assets = new Dictionary<string, ExtractionAsset> { ["asset-1"] = new(format, [0xFB, 0xFF]) }
        }, TestCancellation);
        Assert.Equal(type, result.Answers["value"].Source);
    }

    [Fact]
    public async Task ListsModelsWithoutAddingAuthAndLeavesSuppliedHttpClientUsable()
    {
        using var http = new HttpClient(new StubHandler(message =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Equal("https://openrouter.ai/api/v1/models?endpoints=/api/alpha/extractions", message.RequestUri!.AbsoluteUri);
            Assert.Null(message.Headers.Authorization);
            return JsonResponse("""
                {
                  "data": [
                    {"id":"cloudflare/clairic","architecture":{"input_modalities":["text","image"]}},
                    {"id":"scaledown/extract","architecture":{"input_modalities":["text","file"]}}
                  ]
                }
                """);
        }));
        using (var client = new OpenRouterExtractionsClient("test-key", http))
        {
            var models = await client.ListModelsAsync(TestCancellation);
            Assert.Equal("cloudflare/clairic", models[0].Id);
            Assert.Equal(["text", "image"], models[0].InputModalities);
        }

        var discovered = await OpenRouterExtractionsClient.ListModelsAsync(http, cancellationToken: TestCancellation);
        Assert.Equal("scaledown/extract", discovered[1].Id);
        Assert.Equal(["text", "file"], discovered[1].InputModalities);
    }

    [Fact]
    public async Task PreservesProviderErrorWithoutRetrying()
    {
        const string error = """{"error":{"code":503,"message":"Unavailable"}}""";
        var attempts = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            attempts++;
            return JsonResponse(error, HttpStatusCode.ServiceUnavailable);
        }));
        using var client = new OpenRouterExtractionsClient("test-key", http);
        var exception = await Assert.ThrowsAsync<ExtractionsApiException>(() => client.ExtractAsync(TextRequest(), TestCancellation));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(error, exception.ResponseBody);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"value":{"type":"number","value":4}}""")]
    [InlineData("""{"value":{"type":"text"}}""")]
    [InlineData("""{"value":{"type":"text","value":"x","span":[3,2]}}""")]
    [InlineData("""{"other":{"type":"text","value":"x"}}""")]
    public async Task RejectsMalformedSuccessfulExtractionResponses(string answers)
    {
        using var http = new HttpClient(new StubHandler(_ => JsonResponse($$"""
            {"model":"cloudflare/clairic","usage":{"input_tokens":1,"output_tokens":0},"answers":{{answers}} }
            """)));
        using var client = new OpenRouterExtractionsClient("test-key", http);
        await Assert.ThrowsAsync<JsonException>(() => client.ExtractAsync(TextRequest(), TestCancellation));
    }

    [Fact]
    public async Task RejectsMalformedModelCatalog()
    {
        using var http = new HttpClient(new StubHandler(_ => JsonResponse("""{"data":[{"architecture":{}}]}""")));
        await Assert.ThrowsAsync<JsonException>(() => OpenRouterExtractionsClient.ListModelsAsync(http, cancellationToken: TestCancellation));
    }

    [Fact]
    public async Task ValidatesRequestAndCancellationBeforeSending()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Unexpected HTTP call.")));
        using var client = new OpenRouterExtractionsClient("test-key", http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExtractAsync(TextRequest() with { State = "" }, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExtractAsync(TextRequest() with
        {
            State = "",
            Assets = new Dictionary<string, ExtractionAsset>
            {
                ["asset-1"] = new(ExtractionAssetFormat.Png, [1]),
                ["asset-2"] = new(ExtractionAssetFormat.Png, [2])
            }
        }, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExtractAsync(new ExtractionRequest("model", new Dictionary<string, ExtractionField>()) { State = "text" }, TestCancellation));

        var cancelled = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExtractAsync(TextRequest(), cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListModelsAsync(cancelled));
    }

    [Fact]
    public async Task CancelsAnInFlightExtraction()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        using var http = new HttpClient(new WaitingHandler(entered));
        using var client = new OpenRouterExtractionsClient("test-key", http);

        var extraction = client.ExtractAsync(TextRequest(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellation);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extraction.WaitAsync(TimeSpan.FromSeconds(5), TestCancellation));
    }

    private sealed class WaitingHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected cancellation.");
        }
    }

    private static ExtractionRequest TextRequest(string model = "cloudflare/clairic") =>
        new(model, new Dictionary<string, ExtractionField> { ["value"] = new TextField() }) { State = "Invoice" };

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static void AssertBody(HttpRequestMessage message, string expectedJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(message.Content!.ReadAsStringAsync(TestCancellation).GetAwaiter().GetResult());
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), actual.RootElement.GetRawText());
    }
}
