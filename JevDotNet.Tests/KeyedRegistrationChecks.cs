using System.Net;
using DecisionsDotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class KeyedRegistrationChecks
{
    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        var seen = new List<string>();
        HttpMessageHandler Stub() => new StubHandler(message =>
        {
            if (message.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"data":[{"id":"typesafe/jev-1.13","architecture":{"input_modalities":["text"]}}]}
                    """) };
            seen.Add($"{message.RequestUri} {message.Headers.Authorization}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"model":"jev-1.13.0","answers":{"ok":{"type":"noul","noul":0.9}},"usage":{"input_tokens":1,"output_tokens":1}}
                """) };
        });

        services.AddKeyedJevSystemOneClient("direct", "direct-key").ConfigurePrimaryHttpMessageHandler(Stub);
        services.AddKeyedOpenRouterDecisionsClient("router", new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "router-key" }).Build())
            .ConfigurePrimaryHttpMessageHandler(Stub);
        services.AddKeyedJevSystemOneClient("local", "local-key", new JevClientOptions { BaseUri = new Uri("http://jev:1234/") })
            .ConfigurePrimaryHttpMessageHandler(Stub);

        using var provider = services.BuildServiceProvider();
        Check(provider.GetService<IDecisionsClient>() is null, "Keyed registrations add no default client");
        var direct = provider.GetRequiredKeyedService<IDecisionsClient>("direct");
        var router = provider.GetRequiredKeyedService<IDecisionsClient>("router");
        var local = provider.GetRequiredKeyedService<IDecisionsClient>("local");
        Check(direct is JevClient && router is OpenRouterDecisionsClient && local is JevClient, "Keyed provider types");

        var questions = new Dictionary<string, DecisionsQuestion> { ["ok"] = new NoulQuestion("OK?") };
        await direct.EvaluateAsync(new DecisionsRequest("x", questions));
        await router.EvaluateAsync(new DecisionsRequest("x", questions, "typesafe/jev-1.13"));
        await local.EvaluateAsync(new DecisionsRequest("x", questions));
        Check(seen.SequenceEqual(new[]
        {
            "https://api.typesafe.ai/v1/systemone Bearer direct-key",
            "https://openrouter.ai/api/alpha/decisions Bearer router-key",
            "http://jev:1234/v1/systemone Bearer local-key"
        }), "Keyed clients keep separate keys and endpoints");

        ExpectArgument(() => new ServiceCollection().AddKeyedOpenRouterDecisionsClient("k", " "), "Keyed empty key");
        ExpectArgument(() => new ServiceCollection().AddKeyedJevSystemOneClient(null!, "key"), "Keyed null service key");
    }

    private static void ExpectArgument(Action action, string name)
    {
        try { action(); throw new Exception($"Expected argument error: {name}"); }
        catch (ArgumentException) { }
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
