using System.Net;
using System.Text.Json;
using DecisionsDotNet;

internal static class JevEndpointChecks
{
    public static async Task RunAsync()
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
                Check(message.Headers.Authorization?.ToString() == "Bearer local-key", "Custom endpoint authorization");
                if (message.Method == HttpMethod.Get)
                {
                    modelCalls++;
                    Check(message.RequestUri == new Uri(baseUri, "v1/models"), "Custom model-list URL");
                    return Ok("""
                        {"models":[{"name":"jev-latest","description":"Local model","release_date":"2026-09-15"}]}
                        """);
                }

                evaluationCalls++;
                Check(message.Method == HttpMethod.Post, "Custom evaluation method");
                Check(message.RequestUri == new Uri(baseUri, "v1/systemone"), "Custom evaluation URL");
                Check(message.Content?.Headers.ContentType?.MediaType == "application/json", "Custom endpoint content type");
                using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var payload = body.RootElement;
                Check(payload.GetProperty("model").GetString() == "jev-latest", "Custom endpoint default model");
                Check(payload.GetProperty("state").GetProperty("message").GetString() == "Help", "Custom endpoint structured state");
                var question = payload.GetProperty("questions").GetProperty("urgent");
                Check(question.GetProperty("type").GetString() == "noul", "Custom endpoint question type");
                Check(question.GetProperty("instructions").GetString() == "Is this urgent?", "Custom endpoint instructions");
                return Ok("""
                    {"model":"jev-latest","answers":{"urgent":{"type":"noul","noul":0.9}},"usage":{"input_tokens":5,"output_tokens":1}}
                    """);
            }));
            using var client = new JevClient("local-key", http, new JevClientOptions { BaseUri = baseUri });
            var response = await client.EvaluateAsync(new DecisionsRequest(
                DecisionsValue.FromObject(new { message = "Help" }),
                new Dictionary<string, DecisionsQuestion> { ["urgent"] = new NoulQuestion("Is this urgent?") }));
            Check(response.GetAnswer<NoulAnswer>("urgent").Noul == 0.9, "Custom endpoint typed answer");
            Check(response.Usage.InputTokens == 5, "Custom endpoint usage");
            var models = await client.ListModelsAsync();
            Check(models.Models.Single().Name == "jev-latest", "Custom endpoint model response");
            Check(evaluationCalls == 1 && modelCalls == 1, "Custom endpoint request counts");
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
                Check(error.ParamName == "options" && error.Message.Contains("HTTP or HTTPS"), "Base URI validation error");
            }
        }
    }

    private static HttpResponseMessage Ok(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }
}
