using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using JevDotNet;

internal static class OpenRouterChecks
{
    public static async Task RunAsync()
    {
        var calls = 0;
        var questions = new Dictionary<string, JevQuestion>
        {
            ["urgent"] = new NoulQuestion("Is this urgent?"),
            ["team"] = new ChoiceQuestion("Which team?", new Dictionary<string, JevValue?>
            {
                ["billing"] = "Payments", ["technical"] = "Bugs"
            }),
            ["severity"] = new ScoreQuestion("How severe?", new JevValue[] { "Low", "High" })
        };
        var request = new DecisionsRequest(JevValue.FromObject(new { message = "Help" }), questions, "typesafe/jev-1.13");
        using (var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(message =>
        {
            calls++;
            Check(message.Method == HttpMethod.Post, "OpenRouter method");
            Check(message.RequestUri?.ToString() == "https://openrouter.ai/api/alpha/decisions", "OpenRouter URL");
            Check(message.Headers.Authorization?.ToString() == "Bearer router-key", "OpenRouter bearer key");
            Check(message.Content?.Headers.ContentType?.MediaType == "application/json", "OpenRouter content type");
            using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = body.RootElement;
            Check(root.GetProperty("model").GetString() == "typesafe/jev-1.13", "OpenRouter model");
            Check(root.GetProperty("state").GetProperty("message").GetString() == "Help", "OpenRouter state");
            Check(root.GetProperty("questions").GetProperty("urgent").GetProperty("type").GetString() == "noul", "OpenRouter noul");
            Check(root.GetProperty("questions").GetProperty("team").GetProperty("type").GetString() == "choice", "OpenRouter choice");
            Check(root.GetProperty("questions").GetProperty("severity").GetProperty("type").GetString() == "score", "OpenRouter score");
            return Success();
        }))))
        {
            var result = await client.EvaluateAsync(request);
            Check(result.Model == "typesafe/jev-1.13-20260917", "Resolved OpenRouter model");
            Check(result.Id == "gen-dec-test" && result.Provider == "TypeSafe", "OpenRouter response metadata");
            Check(result.Usage.Cost == 0.000019992m && result.Usage.InputTokens == 476, "OpenRouter usage and cost");
            Check(result.GetAnswer<NoulAnswer>("urgent").Noul == 0.96, "OpenRouter typed noul");
            Check(result.GetAnswer<ChoiceAnswer>("team").Choice == "billing", "OpenRouter typed choice");
            Check(result.GetAnswer<ScoreAnswer>("severity").Score == 1.5, "OpenRouter typed score");
        }
        Check(calls == 1, "One OpenRouter call");

        var retries = 0;
        var statuses = new[] { 429, 502, 503, 529 };
        using (var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(_ =>
        {
            var status = retries++;
            if (status >= statuses.Length) return Success();
            var response = new HttpResponseMessage((HttpStatusCode)statuses[status])
            { Content = new StringContent("temporary") };
            if (status == 0) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        })), new OpenRouterDecisionsClientOptions
        {
            MaxAttempts = 5, InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero
        }))
        {
            await client.EvaluateAsync(request);
            Check(retries == 5, "OpenRouter transient retries");
        }

        foreach (var status in new[] { 402, 524 })
        {
            var attempts = 0;
            using var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(_ =>
            {
                attempts++;
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("error detail") };
            })), new OpenRouterDecisionsClientOptions
            {
                InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero
            });
            try
            {
                await client.EvaluateAsync(request);
                throw new Exception("Expected OpenRouter API exception");
            }
            catch (JevApiException error)
            {
                Check((int)error.StatusCode == status && error.ResponseBody == "error detail" &&
                    error.Service == "OpenRouter" && error.Message.Contains("OpenRouter"),
                    $"OpenRouter HTTP {status} error detail");
                Check(attempts == 1, $"OpenRouter HTTP {status} is not retried");
            }
        }

        var exhaustedAttempts = 0;
        using (var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(_ =>
        {
            exhaustedAttempts++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("still rate limited") };
        })), new OpenRouterDecisionsClientOptions
        {
            MaxAttempts = 3, InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero
        }))
        {
            try
            {
                await client.EvaluateAsync(request);
                throw new Exception("Expected exhausted OpenRouter retry error");
            }
            catch (JevApiException error)
            {
                Check(exhaustedAttempts == 3 && error.StatusCode == HttpStatusCode.TooManyRequests &&
                    error.ResponseBody == "still rate limited" && error.Service == "OpenRouter",
                    "OpenRouter exhausted retries preserve error detail");
            }
        }

        using (var client = new OpenRouterDecisionsClient("router-key", new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"model\":\"typesafe/jev-1.13\",\"answers\":{\"urgent\":{\"type\":\"choice\",\"choice\":\"billing\",\"probabilities\":{\"billing\":1},\"confidence\":1}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}")
            }))))
        {
            try
            {
                await client.EvaluateAsync(new DecisionsRequest("test", new Dictionary<string, JevQuestion>
                {
                    ["urgent"] = new NoulQuestion("Urgent?")
                }, "typesafe/jev-1.13"));
                throw new Exception("Expected OpenRouter protocol error");
            }
            catch (JsonException) { }
        }
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"answers":{"urgent":{"type":"noul","noul":0.96},"team":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"technical":0.2},"confidence":0.75},"severity":{"type":"score","score":1.5,"legend":{"0":"Low","1":"High"},"probabilities":{"0":0.25,"1":0.75},"confidence":0.8}},"id":"gen-dec-test","model":"typesafe/jev-1.13-20260917","provider":"TypeSafe","usage":{"cost":0.000019992,"input_tokens":476,"output_tokens":70}}
            """)
    };

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
