using System.Net;
using System.Text.Json;
using JevDotNet;

var calls = 0;
var handler = new StubHandler(request =>
{
    calls++;
    Check(request.Method == HttpMethod.Post, "Evaluation method");
    Check(request.RequestUri?.ToString() == "https://api.typesafe.ai/v1/systemone", "Evaluation URL");
    Check(request.Headers.Authorization?.ToString() == "Bearer test-key", "Authorization header");
    using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
    var root = body.RootElement;
    Check(root.GetProperty("model").GetString() == "jev-latest", "Default model");
    Check(root.GetProperty("state").GetProperty("message").GetString() == "Help", "Structured state");
    var questions = root.GetProperty("questions");
    Check(questions.GetProperty("urgent").GetProperty("type").GetString() == "noul", "Noul discriminator");
    Check(questions.GetProperty("urgent").GetProperty("criteria").GetProperty("true").GetString() == "Time-sensitive", "Noul criteria");
    Check(questions.GetProperty("route").GetProperty("type").GetString() == "choice", "Choice discriminator");
    Check(questions.GetProperty("route").GetProperty("criteria").GetProperty("other").ValueKind == JsonValueKind.Null, "Null choice criterion");
    Check(questions.GetProperty("severity").GetProperty("type").GetString() == "score", "Score discriminator");
    Check(questions.GetProperty("severity").GetProperty("criteria").GetArrayLength() == 2, "Score criteria");
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"model":"jev-1.13.0","answers":{"urgent":{"type":"noul","noul":0.95},"route":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"other":0.2},"confidence":0.7},"severity":{"type":"score","score":0.25,"legend":{"0":"Calm","1":"Angry"},"probabilities":{"0":0.75,"1":0.25},"confidence":0.6}},"usage":{"input_tokens":20,"output_tokens":3}}
            """)
    };
});

using (var client = new JevClient("test-key", new HttpClient(handler)))
{
    var request = new DecisionsRequest(
        JevValue.FromObject(new { message = "Help" }),
        new Dictionary<string, JevQuestion>
        {
            ["urgent"] = new NoulQuestion("Is this urgent?", new NoulCriteria("Time-sensitive", "Not urgent")),
            ["route"] = new ChoiceQuestion("Which team?", new Dictionary<string, JevValue?>
            {
                ["billing"] = "Payment questions", ["other"] = null
            }),
            ["severity"] = new ScoreQuestion("How severe?", new JevValue[] { "Calm", "Angry" })
        });
    var result = await client.EvaluateAsync(request);
    Check(result.Model == "jev-1.13.0", "Resolved model");
    Check(result.GetAnswer<NoulAnswer>("urgent").Noul == 0.95, "Typed Noul answer");
    Check(result.GetAnswer<ChoiceAnswer>("route").Choice == "billing", "Typed Choice answer");
    Check(result.GetAnswer<ScoreAnswer>("severity").Legend["1"] == "Angry", "Typed Score answer");
    Check(result.Usage.InputTokens == 20, "Usage");
}
Check(calls == 1, "One evaluation call");

var retryCalls = 0;
using (var client = new JevClient("test-key", new HttpClient(new StubHandler(_ =>
{
    retryCalls++;
    return retryCalls == 1
        ? new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("rate limited") }
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"name\":\"jev-latest\",\"description\":\"Stable\",\"release_date\":\"2026-09-15\"}]}") };
})), new JevClientOptions { InitialRetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero }))
{
    var models = await client.ListModelsAsync();
    Check(retryCalls == 2 && models.Models[0].Name == "jev-latest", "Retry and model listing");
}

using (var client = new JevClient("test-key", new HttpClient(new StubHandler(_ =>
    new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("bad criteria") }))))
{
    try
    {
        await client.ListModelsAsync();
        throw new Exception("Expected JevApiException");
    }
    catch (JevApiException ex)
    {
        Check(ex.StatusCode == HttpStatusCode.UnprocessableEntity && ex.ResponseBody == "bad criteria", "API error details");
    }
}

using (var client = new JevClient("test-key", new HttpClient(new StubHandler(_ =>
    new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"model\":\"jev-1.13.0\",\"answers\":{\"urgent\":{\"type\":\"choice\",\"choice\":\"yes\",\"probabilities\":{\"yes\":1},\"confidence\":1}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}")
    }))))
{
    try
    {
        await client.EvaluateAsync(new DecisionsRequest("test", new Dictionary<string, JevQuestion>
        {
            ["urgent"] = new NoulQuestion("Urgent?")
        }));
        throw new Exception("Expected a protocol error");
    }
    catch (JsonException) { }
}

await OpenRouterChecks.RunAsync();
await DependencyInjectionChecks.RunAsync();
await DemoChecks.RunAsync();
await BulkTriageChecks.RunAsync();
await JudgeChecks.RunAsync();
await LunaJudgeChecks.RunAsync();
ChaseChecks.Run();
Console.WriteLine("All SDK and demo contract checks passed.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"Failed: {name}");
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
