using System.Net;
using System.Text.Json;
using DecisionsDotNet;

public sealed class JevClientTests
{
    [Fact]
    public async Task EvaluateAsyncSerializesQuestionsAndReadsTypedAnswers()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            Assert.True(request.Method == HttpMethod.Post, "Evaluation method");
            Assert.True(request.RequestUri?.ToString() == "https://api.typesafe.ai/v1/systemone", "Evaluation URL");
            Assert.True(request.Headers.Authorization?.ToString() == "Bearer test-key", "Authorization header");
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = body.RootElement;
            Assert.True(root.GetProperty("model").GetString() == "jev-latest", "Default model");
            Assert.True(root.GetProperty("state").GetProperty("message").GetString() == "Help", "Structured state");
            var questions = root.GetProperty("questions");
            Assert.True(questions.GetProperty("urgent").GetProperty("type").GetString() == "noul", "Noul discriminator");
            Assert.True(questions.GetProperty("urgent").GetProperty("criteria").GetProperty("true").GetString() == "Time-sensitive", "Noul criteria");
            Assert.True(questions.GetProperty("route").GetProperty("type").GetString() == "choice", "Choice discriminator");
            Assert.True(questions.GetProperty("route").GetProperty("criteria").GetProperty("other").ValueKind == JsonValueKind.Null, "Null choice criterion");
            Assert.True(questions.GetProperty("severity").GetProperty("type").GetString() == "score", "Score discriminator");
            Assert.True(questions.GetProperty("severity").GetProperty("criteria").GetArrayLength() == 2, "Score criteria");
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
                DecisionsValue.FromObject(new { message = "Help" }),
                new Dictionary<string, DecisionsQuestion>
                {
                    ["urgent"] = new NoulQuestion("Is this urgent?", new NoulCriteria("Time-sensitive", "Not urgent")),
                    ["route"] = new ChoiceQuestion("Which team?", new Dictionary<string, DecisionsValue?>
                    {
                        ["billing"] = "Payment questions", ["other"] = null
                    }),
                    ["severity"] = new ScoreQuestion("How severe?", new DecisionsValue[] { "Calm", "Angry" })
                });
            var result = await client.EvaluateAsync(request);
            Assert.True(result.Model == "jev-1.13.0", "Resolved model");
            Assert.True(result.GetAnswer<NoulAnswer>("urgent").Noul == 0.95, "Typed Noul answer");
            Assert.True(result.GetAnswer<ChoiceAnswer>("route").Choice == "billing", "Typed Choice answer");
            Assert.True(result.GetAnswer<ScoreAnswer>("severity").Legend["1"] == "Angry", "Typed Score answer");
            Assert.True(result.Usage.InputTokens == 20, "Usage");
        }
        Assert.True(calls == 1, "One evaluation call");
    }

    [Fact]
    public async Task ListModelsAsyncRetriesRateLimitedRequests()
    {
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
            Assert.True(retryCalls == 2 && models.Models[0].Name == "jev-latest", "Retry and model listing");
        }
    }

    [Fact]
    public async Task ListModelsAsyncPreservesApiErrorDetails()
    {
        using (var client = new JevClient("test-key", new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("bad criteria") }))))
        {
            var error = await Assert.ThrowsAsync<JevApiException>(() => client.ListModelsAsync());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, error.StatusCode);
            Assert.Equal("bad criteria", error.ResponseBody);
        }
    }

    [Fact]
    public async Task EvaluateAsyncRejectsMismatchedAnswerTypes()
    {
        using (var client = new JevClient("test-key", new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"model\":\"jev-1.13.0\",\"answers\":{\"urgent\":{\"type\":\"choice\",\"choice\":\"yes\",\"probabilities\":{\"yes\":1},\"confidence\":1}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}")
            }))))
        {
            await Assert.ThrowsAsync<JsonException>(() => client.EvaluateAsync(
                new DecisionsRequest("test", new Dictionary<string, DecisionsQuestion>
                {
                    ["urgent"] = new NoulQuestion("Urgent?")
                })));
        }
    }
}
