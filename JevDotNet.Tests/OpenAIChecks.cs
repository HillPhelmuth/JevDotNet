using System.Net;
using System.Text.Json;
using DecisionsDotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class OpenAIChecks
{
    private const string Png = "iVBORw0KGgo=";

    public static async Task RunAsync()
    {
        JsonElement sent = default;
        var calls = 0;
        var handler = new StubHandler(message =>
        {
            calls++;
            Check(message.Method == HttpMethod.Post && message.RequestUri?.ToString() == "https://api.openai.com/v1/decisions", "OpenAI URL");
            Check(message.Headers.Authorization?.ToString() == "Bearer openai-key", "OpenAI bearer key");
            Check(message.Headers.GetValues("OpenAI-Project").Single() == "proj_1", "OpenAI project header");
            if (calls == 1) return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("oops") };
            sent = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return Ok("""
                {"id":"dec_1","model":"gpt-6-luna-2026-09-01","answers":[
                  {"type":"predicate","name":"urgent","probability":0.92},
                  {"type":"choice","name":"team","choice":"billing","probabilities":[{"value":"billing","probability":0.7},{"value":"other","probability":0.3}],"confidence":0.6},
                  {"type":"score","name":"anger","score":1.4,"probabilities":[{"value":0,"label":"Calm","probability":0.1},{"value":1,"label":"Upset","probability":0.4},{"value":2,"label":"Furious","probability":0.5}],"confidence":0.5}],
                 "usage":{"input_tokens":40,"input_tokens_details":{"cached_tokens":0},"output_tokens":0,"total_tokens":40}}
                """);
        });

        using var client = new OpenAIDecisionsClient("openai-key", new HttpClient(handler), new OpenAIDecisionsClientOptions
        {
            Project = "proj_1", InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero
        });
        var response = await client.EvaluateAsync(DecisionsRequest.Create(DecisionsValue.FromObject(new { ticket = "Charged twice" }))
            .Noul("urgent", "Is this urgent?", out var urgent, "Blocking the customer", "Can wait")
            .Choice("team", "Which team?", out var team, ("billing", "Payments"), ("other", null))
            .Score("anger", "How angry?", out var anger, "Calm", "Upset", "Furious")
            .Build(OpenAIDecisionsClient.DefaultModel));

        Check(calls == 2, "OpenAI retries 500");
        Check(sent.GetProperty("model").GetString() == "gpt-6-luna", "OpenAI model");
        Check(sent.GetProperty("input").GetString() == "{\"ticket\":\"Charged twice\"}", "Structured state becomes JSON text input");
        var questions = sent.GetProperty("questions").EnumerateArray().ToArray();
        Check(questions[0].GetProperty("type").GetString() == "predicate" && questions[0].GetProperty("name").GetString() == "urgent" &&
            questions[0].GetProperty("instructions").GetString() == "Is this urgent?\nTrue when: Blocking the customer\nFalse when: Can wait",
            "Noul maps to predicate with criteria in instructions");
        var choices = questions[1].GetProperty("choices").EnumerateArray().ToArray();
        Check(questions[1].GetProperty("type").GetString() == "choice" && choices[0].GetProperty("value").GetString() == "billing" &&
            choices[0].GetProperty("description").GetString() == "Payments" && choices[1].GetProperty("description").GetString() == "other",
            "Choice maps to choices");
        var levels = questions[2].GetProperty("levels").EnumerateArray().ToArray();
        Check(questions[2].GetProperty("type").GetString() == "score" && levels.Length == 3 &&
            levels[2].GetProperty("label").GetString() == "Furious", "Score maps to levels");

        Check(response.Provider == "OpenAI" && response.Id == "dec_1" && response.Model == "gpt-6-luna-2026-09-01", "OpenAI metadata");
        Check(response.Get(urgent).Noul == 0.92, "Predicate answer");
        Check(response.Get(team).Choice == "billing" && response.Get(team).Probabilities["other"] == 0.3, "Choice answer");
        Check(response.Get(anger).Score == 1.4 && response.Get(anger).Probabilities["2"] == 0.5 &&
            response.Get(anger).Legend["1"] == "Upset", "Score answer");
        Check(response.Usage.InputTokens == 40 && response.Usage.OutputTokens == 0, "OpenAI usage");

        // Images become a user message with ordered parts.
        using var imageClient = new OpenAIDecisionsClient("openai-key", new HttpClient(new StubHandler(message =>
        {
            sent = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return Ok("""{"model":"gpt-6-luna","answers":[{"type":"refusal","name":"ok"}],"usage":{"input_tokens":5,"output_tokens":0}}""");
        })));
        var refused = await imageClient.EvaluateAsync(DecisionsRequest.Create(
                DecisionsValue.FromParts("Listing photo", DecisionsValue.ImageFromDataUrl($"data:image/png;base64,{Png}")))
            .Noul("ok", "Does the photo match?").Build(OpenAIDecisionsClient.DefaultModel));
        var message = sent.GetProperty("input")[0];
        var parts = message.GetProperty("content").EnumerateArray().ToArray();
        Check(message.GetProperty("role").GetString() == "user" && parts[0].GetProperty("type").GetString() == "input_text" &&
            parts[1].GetProperty("type").GetString() == "input_image" &&
            parts[1].GetProperty("image_url").GetString() == $"data:image/png;base64,{Png}", "Image input parts");
        Check(refused.Answers["ok"] is RefusalAnswer, "Refusal answer");
        try { refused.GetAnswer<NoulAnswer>("ok"); throw new Exception("Expected refusal to fail typed read"); }
        catch (InvalidOperationException) { }

        await ExpectAsync<NotSupportedException>(() => imageClient.EvaluateAsync(
            DecisionsRequest.Create("x").Box("b", "Find it").Build(OpenAIDecisionsClient.DefaultModel)), "Box unsupported");

        using var mismatched = new OpenAIDecisionsClient("k", new HttpClient(new StubHandler(_ => Ok(
            """{"model":"gpt-6-luna","answers":[{"type":"predicate","name":"other","probability":0.5}],"usage":{"input_tokens":1,"output_tokens":0}}"""))));
        await ExpectAsync<JsonException>(() => mismatched.EvaluateAsync(
            DecisionsRequest.Create("x").Noul("ok", "OK?").Build(OpenAIDecisionsClient.DefaultModel)), "Unexpected answer name");

        using var failing = new OpenAIDecisionsClient("k", new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad model") })));
        try
        {
            await failing.EvaluateAsync(DecisionsRequest.Create("x").Noul("ok", "OK?").Build("jev-latest"));
            throw new Exception("Expected JevApiException");
        }
        catch (JevApiException error)
        {
            Check(error.Service == "OpenAI" && error.ResponseBody == "bad model", "OpenAI error details");
        }

        var services = new ServiceCollection();
        services.AddOpenAIDecisionsClient(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "cfg-key" }).Build());
        services.AddKeyedOpenAIDecisionsClient("luna", "keyed-key");
        using var provider = services.BuildServiceProvider();
        Check(provider.GetRequiredService<IDecisionsClient>() is OpenAIDecisionsClient &&
            provider.GetRequiredKeyedService<IDecisionsClient>("luna") is OpenAIDecisionsClient, "OpenAI DI registration");
        try { new ServiceCollection().AddOpenAIDecisionsClient(new ConfigurationBuilder().Build()); throw new Exception("Expected missing key"); }
        catch (InvalidOperationException error) when (error.Message.Contains("OpenAI:ApiKey")) { }
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static async Task ExpectAsync<TException>(Func<Task> action, string name) where TException : Exception
    {
        try { await action(); throw new Exception($"Expected {typeof(TException).Name}: {name}"); }
        catch (TException) { }
    }

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
