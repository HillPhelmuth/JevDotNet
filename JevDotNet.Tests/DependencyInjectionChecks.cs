using System.Net;
using DecisionsDotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class DependencyInjectionChecks
{
    public static async Task RunAsync()
    {
        foreach (var useConfiguration in new[] { false, true })
        {
            await CheckProviderAsync(useConfiguration, openRouter: false);
            await CheckProviderAsync(useConfiguration, openRouter: false,
                options: new JevClientOptions { BaseUri = new Uri("http://localhost:1234/") });
            await CheckProviderAsync(useConfiguration, openRouter: true);
        }

        await CheckProviderAsync(useConfiguration: false, openRouter: false,
            options: new JevClientOptions { BaseUri = new Uri("http://localhost:1234/") }, localRegistration: true);
        await CheckProviderAsync(useConfiguration: false, openRouter: false,
            options: new JevClientOptions { BaseUri = new Uri("http://jev:1234/") }, localRegistration: true,
            localApiKey: "custom-local-key");
        ExpectArgument(() => new ServiceCollection().AddLocalJevSystemOneClient(null!), "Local null base URI");
        ExpectArgument(() => new ServiceCollection().AddLocalJevSystemOneClient(new Uri("http://localhost:1234/"), " "),
            "Local empty key");

        ExpectArgument(() => new ServiceCollection().AddJevSystemOneClient(" "), "Direct empty key");
        ExpectArgument(() => new ServiceCollection().AddOpenRouterDecisionsClient(" "), "OpenRouter empty key");
        ExpectMissingKey(() => new ServiceCollection().AddJevSystemOneClient(new ConfigurationBuilder().Build()),
            "TypeSafe:ApiKey");
        ExpectMissingKey(() => new ServiceCollection().AddOpenRouterDecisionsClient(new ConfigurationBuilder().Build()),
            "OpenRouter:ApiKey");
    }

    private static async Task CheckProviderAsync(bool useConfiguration, bool openRouter, JevClientOptions? options = null,
        bool localRegistration = false, string? localApiKey = null)
    {
        var services = new ServiceCollection();
        var expectedKey = localRegistration ? localApiKey ?? "local-key" : openRouter ? "router-key" : "direct-key";
        var expectedUrl = openRouter
            ? "https://openrouter.ai/api/alpha/decisions"
            : new Uri(options?.BaseUri ?? new Uri("https://api.typesafe.ai/"), "v1/systemone").ToString();
        var model = openRouter ? "typesafe/jev-1.13" : "jev-latest";
        var calls = 0;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [openRouter ? "OpenRouter:ApiKey" : "TypeSafe:ApiKey"] = expectedKey
            }).Build();
        var builder = localRegistration
            ? localApiKey is null
                ? services.AddLocalJevSystemOneClient(options!.BaseUri)
                : services.AddLocalJevSystemOneClient(options!.BaseUri, localApiKey)
            : openRouter
            ? useConfiguration
                ? services.AddOpenRouterDecisionsClient(configuration)
                : services.AddOpenRouterDecisionsClient(expectedKey)
            : useConfiguration
                ? services.AddJevSystemOneClient(configuration, options)
                : services.AddJevSystemOneClient(expectedKey, options);
        if (useConfiguration)
            configuration[openRouter ? "OpenRouter:ApiKey" : "TypeSafe:ApiKey"] = "changed-after-registration";

        builder.ConfigurePrimaryHttpMessageHandler(() => new StubHandler(message =>
        {
            if (message.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"data":[{"id":"typesafe/jev-1.13","architecture":{"input_modalities":["text"]}}]}
                    """) };
            calls++;
            Check(message.Method == HttpMethod.Post, "DI evaluation method");
            Check(message.RequestUri?.ToString() == expectedUrl, "DI provider endpoint");
            Check(message.Headers.Authorization?.ToString() == $"Bearer {expectedKey}", "DI bearer key");
            var body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(body.Contains($"\"model\":\"{model}\""), "DI request model");
            Check(body.Contains("\"type\":\"choice\""), "DI typed question");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"jev-1.13.0","answers":{"move":{"type":"choice","choice":"right","probabilities":{"right":0.6,"down":0.4},"confidence":0.6}},"usage":{"input_tokens":12,"output_tokens":2}}
                    """)
            };
        }));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDecisionsClient>();
        Check(openRouter ? client is OpenRouterDecisionsClient : client is JevClient, "DI provider selection");
        var request = new DecisionsRequest("A chase", new Dictionary<string, DecisionsQuestion>
        {
            ["move"] = new ChoiceQuestion("Which move?", new Dictionary<string, DecisionsValue?>
            {
                ["right"] = "Closer", ["down"] = "Farther"
            })
        }, model);
        var response = await client.EvaluateAsync(request);
        Check(response.GetAnswer<ChoiceAnswer>("move").Choice == "right", "DI typed answer");
        Check(response.Usage.InputTokens == 12 && calls == 1, "DI response and single call");
    }

    private static void ExpectArgument(Action action, string name)
    {
        try { action(); throw new Exception($"Expected argument error: {name}"); }
        catch (ArgumentException) { }
    }

    private static void ExpectMissingKey(Action action, string setting)
    {
        try { action(); throw new Exception($"Expected missing key error: {setting}"); }
        catch (InvalidOperationException error) when (error.Message.Contains(setting)) { }
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
