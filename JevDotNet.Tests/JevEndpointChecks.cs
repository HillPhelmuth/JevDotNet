using System.Net;
using System.Text.Json;
using DecisionsDotNet;

public sealed class JevEndpointChecks
{
    [Fact]
    public async Task CustomEndpointsAndEnvironmentConfigurationAreHonoredAsync()
    {
        foreach (var root in new[]
        {
            "http://localhost:1234/", "http://127.0.0.1:1234/", "http://[::1]:1234/",
            "http://192.168.1.10:1234/", "http://jev:1234/", "https://custom.example/",
            "http://localhost:1234/proxy/"
        })
        {
            var baseUri = new Uri(root);
            var evaluationCalls = 0;
            var modelCalls = 0;
            using var http = new HttpClient(new StubHandler(message =>
            {
                Assert.True(message.Headers.Authorization?.ToString() == "Bearer local-key", "Custom endpoint authorization");
                if (message.Method == HttpMethod.Get)
                {
                    modelCalls++;
                    Assert.True(message.RequestUri == new Uri(baseUri, "v1/models"), "Custom model-list URL");
                    return Ok("""
                        {"models":[{"name":"jev-latest","description":"Local model","release_date":"2026-09-15"}]}
                        """);
                }

                evaluationCalls++;
                Assert.True(message.Method == HttpMethod.Post, "Custom evaluation method");
                Assert.True(message.RequestUri == new Uri(baseUri, "v1/systemone"), "Custom evaluation URL");
                Assert.True(message.Content?.Headers.ContentType?.MediaType == "application/json", "Custom endpoint content type");
                using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var payload = body.RootElement;
                Assert.True(payload.GetProperty("model").GetString() == "jev-latest", "Custom endpoint default model");
                Assert.True(payload.GetProperty("state").GetProperty("message").GetString() == "Help", "Custom endpoint structured state");
                var question = payload.GetProperty("questions").GetProperty("urgent");
                Assert.True(question.GetProperty("type").GetString() == "noul", "Custom endpoint question type");
                Assert.True(question.GetProperty("instructions").GetString() == "Is this urgent?", "Custom endpoint instructions");
                return Ok("""
                    {"model":"jev-latest","answers":{"urgent":{"type":"noul","noul":0.9}},"usage":{"input_tokens":5,"output_tokens":1}}
                    """);
            }));
            using var client = new JevClient("local-key", http, new JevClientOptions { BaseUri = baseUri });
            var response = await client.EvaluateAsync(new DecisionsRequest(
                DecisionsValue.FromObject(new { message = "Help" }),
                new Dictionary<string, DecisionsQuestion> { ["urgent"] = new NoulQuestion("Is this urgent?") }));
            Assert.True(response.GetAnswer<NoulAnswer>("urgent").Noul == 0.9, "Custom endpoint typed answer");
            Assert.True(response.Usage.InputTokens == 5, "Custom endpoint usage");
            var models = await client.ListModelsAsync();
            Assert.True(models.Models.Single().Name == "jev-latest", "Custom endpoint model response");
            Assert.True(evaluationCalls == 1 && modelCalls == 1, "Custom endpoint request counts");
        }

        foreach (var invalid in new Uri?[] { null, new("relative/", UriKind.Relative), new("ftp://localhost/"), new("file:///tmp/jev/") })
        {
            try
            {
                using var client = new JevClient("local-key", options: new JevClientOptions { BaseUri = invalid! });
                throw new Exception("Expected invalid base URI rejection");
            }
            catch (ArgumentException error)
            {
                Assert.True(error.ParamName == "options" && error.Message.Contains("HTTP or HTTPS"), "Base URI validation error");
            }
        }
    }

    private static HttpResponseMessage Ok(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

}
