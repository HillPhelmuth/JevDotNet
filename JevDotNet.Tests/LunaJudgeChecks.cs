using System.Net;
using System.Text.Json;
using JevDotNet;
using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

internal static class LunaJudgeChecks
{
    public static async Task RunAsync()
    {
        var examples = JudgeCatalog.Load(Path.Combine("JevDotNet.Demo", "Data", "judge-data.json"));
        foreach (var example in examples)
        {
            var request = LunaJudgeAgent.BuildRequest(example.Type, example.Input);
            var evaluator = JudgeCatalog.Find(example.Type);
            using var document = JsonDocument.Parse(request.Prompt);
            Check(document.RootElement.GetProperty("rubric").GetArrayLength() == 5, "Same five rubric levels");
            var inputs = document.RootElement.GetProperty("inputs");
            Check(inputs.EnumerateObject().Count() == (evaluator.NeedsReference || evaluator.NeedsContext ? 3 : 2), "Same minimal judge inputs");
            Check(!request.Prompt.Contains(example.Explanation) && !request.Prompt.Contains(example.Id) &&
                !request.Prompt.Contains("expectedScore"), "Luna cannot see reference labels");
        }
        var original = LunaJudgeAgent.BuildRequest(examples[0].Type, examples[0].Input);
        Check(original.CacheKey.Length == 64 && original.CacheKey == LunaJudgeAgent.BuildRequest(examples[0].Type, examples[0].Input).CacheKey, "Stable request fingerprint");
        Check(original.CacheKey != LunaJudgeAgent.BuildRequest(examples[0].Type, examples[0].Input with { Response = "changed" }).CacheKey, "Changed response invalidates cache");
        Check(original.CacheKey != (original with { Model = "other" }).CacheKey &&
            original.CacheKey != (original with { Instructions = "changed" }).CacheKey &&
            original.CacheKey != (original with { Schema = "changed" }).CacheKey &&
            original.CacheKey != (original with { Prompt = "changed rubric" }).CacheKey &&
            original.CacheKey != (original with { MaxOutputTokens = 256 }).CacheKey, "All request settings affect key");
        foreach (var invalid in new[] { "{}", "{\"rating\":0,\"rationale\":\"text\"}", "{\"rating\":6,\"rationale\":\"text\"}",
            "{\"rating\":2.5,\"rationale\":\"text\"}", "{\"rating\":\"3\",\"rationale\":\"text\"}", "{\"rating\":3,\"rationale\":\"\"}",
            "{\"rating\":3,\"rationale\":\"text\",\"extra\":0}" }) Expect<JsonException>(() => LunaJudgeAgent.Parse(invalid));
        await WireAsync(original);

        var directory = Path.Combine(Path.GetTempPath(), "jev-luna-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LunaJudgeCache(directory);
            var client = new FakeLuna();
            var first = await cache.GetOrEvaluateAsync(original, client);
            Check(!first.FromCache && client.Calls == 1, "First judgment sent once");
            var restarted = new LunaJudgeCache(directory);
            var second = await restarted.GetOrEvaluateAsync(original, null);
            Check(second.FromCache && second.Result == first.Result && client.Calls == 1, "Persistent reuse without client or API key");
            var edited = original with { Prompt = original.Prompt + " " };
            await cache.GetOrEvaluateAsync(edited, client);
            Check(client.Calls == 2, "Changed request makes fresh judgment");

            var concurrentRequest = original with { Prompt = "concurrent" };
            var concurrent = await Task.WhenAll(cache.GetOrEvaluateAsync(concurrentRequest, client), restarted.GetOrEvaluateAsync(concurrentRequest, client));
            Check(client.Calls == 3 && concurrent.Count(item => item.FromCache) == 1, "Concurrent viewers make one LLM call");

            var failed = original with { Prompt = "failed" };
            var failingClient = new FakeLuna { Fail = true };
            await ExpectAsync<HttpRequestException>(() => cache.GetOrEvaluateAsync(failed, failingClient));
            await ExpectAsync<InvalidOperationException>(() => restarted.GetOrEvaluateAsync(failed, failingClient));
            Check(failingClient.Calls == 1, "Failed attempts are not automatically repeated");

            var interrupted = original with { Prompt = "interrupted" };
            await File.WriteAllTextAsync(Path.Combine(directory, interrupted.CacheKey + ".json"), JsonSerializer.Serialize(
                new LunaJudgeCacheEntry(interrupted.CacheKey, interrupted, DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await ExpectAsync<InvalidOperationException>(() => cache.GetOrEvaluateAsync(interrupted, client));
            Check(client.Calls == 3, "Interrupted requests cannot be resubmitted");

            var corrupt = original with { Prompt = "corrupt" };
            await File.WriteAllTextAsync(Path.Combine(directory, corrupt.CacheKey + ".json"), "not JSON");
            await ExpectAsync<JsonException>(() => cache.GetOrEvaluateAsync(corrupt, client));
            Check(client.Calls == 3, "Corrupt storage does not trigger a network retry");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => cache.GetOrEvaluateAsync(original with { Prompt = "cancelled" }, client, cancellation.Token));
            Check(client.Calls == 3, "Cancellation before send makes no LLM request");

            using var afterResponseCancellation = new CancellationTokenSource();
            var lateRequest = original with { Prompt = "cancel after response" };
            var lateClient = new DelegateLuna((request, _) =>
            {
                afterResponseCancellation.Cancel();
                return Task.FromResult(new LunaJudgeResult(5, "Supported.", "{\"rating\":5,\"rationale\":\"Supported.\"}",
                    request.Model, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow, 20, 10));
            });
            await cache.GetOrEvaluateAsync(lateRequest, lateClient, afterResponseCancellation.Token);
            Check((await restarted.GetOrEvaluateAsync(lateRequest, null)).FromCache, "Completed response persists despite cancellation");

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "test" }).Build();
            var judge = new JudgeService(new DemoEvaluationService(config, new FakeDecisions()), new TestEnvironment());
            var comparisonClient = new FakeLuna();
            var comparison = new JudgeComparisonService(judge, cache, comparisonClient);
            var group = examples.Where(item => item.Type == "completeness").ToArray();
            var run = new JudgeComparisonRun(group, DemoDecisionModels.DefaultModelId);
            var updates = 0;
            await comparison.RunAsync(run, () => { updates++; return Task.CompletedTask; });
            Check(run.Complete && run.Luna.Count == 20 && updates == 40 && comparisonClient.Calls == 20, "Full original dataset comparison");
            var summary = JudgeComparisonSummary.Calculate(run);
            Check(summary.Paired == 20 && summary.Agreed == 20 && summary.MeanGap == 0 &&
                summary.LunaReferenceAgreement == 4 && summary.LunaReferenceError == 2 && summary.Live == 20, "Paired and reference metrics");
            var warmRun = new JudgeComparisonRun(group, "~typesafe/jev-latest");
            await new JudgeComparisonService(judge, new LunaJudgeCache(directory), null).RunAsync(warmRun);
            Check(JudgeComparisonSummary.Calculate(warmRun).Stored == 20 && comparisonClient.Calls == 20, "Switching decision model reuses Luna baseline");
            Check(JudgeComparisonSummary.Calculate(warmRun).LunaOriginalTime == summary.LunaOriginalTime, "Stored time retains original LLM latency");

            var failureClient = new FakeLuna { FailCall = 2 };
            var failureGroup = examples.Where(item => item.Type == "groundedness").ToArray();
            var failureRun = new JudgeComparisonRun(failureGroup, DemoDecisionModels.DefaultModelId);
            await new JudgeComparisonService(judge, cache, failureClient).RunAsync(failureRun);
            Check(failureRun.Complete && failureRun.Luna.Count == 20 && failureRun.Luna[1].Error is not null &&
                JudgeComparisonSummary.Calculate(failureRun).Paired == 19, "Comparison continues after individual failure");
            var failureWarm = new JudgeComparisonRun(failureGroup, DemoDecisionModels.DefaultModelId);
            await new JudgeComparisonService(judge, restarted, failureClient).RunAsync(failureWarm);
            Check(failureClient.Calls == 20 && failureWarm.Luna[1].Error is not null &&
                JudgeComparisonSummary.Calculate(failureWarm).Stored == 19, "Warm comparison reuses successes and does not retry failed item");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task WireAsync(LunaJudgeRequest request)
    {
        var bodies = new List<JsonElement>();
        using var http = new HttpClient(new Handler(message =>
        {
            bodies.Add(JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "judge-test", @object = "chat.completion", created = 1, model = LunaJudgeAgent.ModelId,
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "{\"rating\":5,\"rationale\":\"All required facts are supported.\"}" } } },
                usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 }
            })) };
        }));
        var agent = new LunaJudgeAgent("test-key", http);
        var result = await agent.EvaluateAsync(request);
        await agent.EvaluateAsync(request);
        Check(result.Rating == 5 && result.InputTokens == 20 && result.OutputTokens == 10 && bodies.Count == 2, "Luna typed response and usage");
        Check(bodies.All(body => body.GetProperty("messages").GetArrayLength() == 2), "No prior example history");
        var format = bodies[0].GetProperty("response_format");
        Check(format.GetProperty("type").GetString() == "json_schema" &&
            format.GetProperty("json_schema").GetProperty("strict").GetBoolean(), "Strict Luna JSON schema");
        var schema = format.GetProperty("json_schema").GetProperty("schema");
        Check(schema.GetProperty("properties").GetProperty("rating").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetInt32()).SequenceEqual(Enumerable.Range(1, 5)), "Integer Likert bounds on wire");

        var calls = 0;
        using var errorHttp = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }; }));
        await ExpectAsync<System.ClientModel.ClientResultException>(() => new LunaJudgeAgent("test", errorHttp).EvaluateAsync(request));
        Check(calls == 1, "SDK automatic HTTP retries disabled");
    }

    private static void Check(bool condition, string name) { if (!condition) throw new Exception("Failed: " + name); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class FakeLuna : ILunaJudgeClient
    {
        public int Calls;
        public bool Fail;
        public int FailCall;
        public async Task<LunaJudgeResult> EvaluateAsync(LunaJudgeRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(20, cancellationToken);
            if (Fail || Calls == FailCall) throw new HttpRequestException("test failure");
            return new(5, "Supported.", "{\"rating\":5,\"rationale\":\"Supported.\"}", request.Model,
                TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow, 20, 10);
        }
    }
    private sealed class DelegateLuna(Func<LunaJudgeRequest, CancellationToken, Task<LunaJudgeResult>> evaluate) : ILunaJudgeClient
    {
        public Task<LunaJudgeResult> EvaluateAsync(LunaJudgeRequest request, CancellationToken cancellationToken = default) => evaluate(request, cancellationToken);
    }
    private sealed class FakeDecisions : IDecisionsClient
    {
        public Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new JevResponse
        {
            Model = request.Model, Usage = new JevUsage { InputTokens = 10, OutputTokens = 5 },
            Answers = new Dictionary<string, JevAnswer> { [JudgeCatalog.QuestionId] = new ScoreAnswer
            {
                Score = 4, Confidence = 1, Legend = new Dictionary<string, string>(),
                Probabilities = new Dictionary<string, double> { ["0"] = 0, ["1"] = 0, ["2"] = 0, ["3"] = 0, ["4"] = 1 }
            } }
        });
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "JevDotNet.Demo";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetFullPath("JevDotNet.Demo");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
