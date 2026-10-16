using System.Net;
using System.Text.Json;
using JevDotNet.Demo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static class DecisionModelsChecks
{
    public static async Task RunAsync()
    {
        var body = Catalog(new(DemoDecisionModels.DefaultModelId, "Default from API"),
            new("test/retired-model", "Old model"));
        var status = HttpStatusCode.OK;
        var calls = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(DecisionModelsRefreshService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
            {
                calls++;
                Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri ==
                    "https://openrouter.ai/api/v1/models?output_modalities=decisions", "Filtered catalog GET");
                Check(request.Headers.Authorization is null, "Public catalog needs no API key");
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            }));
        services.AddHostedService<DecisionModelsRefreshService>();
        using var provider = services.BuildServiceProvider();
        var updater = provider.GetRequiredService<IEnumerable<IHostedService>>().Single();
        await updater.StartAsync(CancellationToken.None);
        Check(calls == 1 && DemoDecisionModels.All.Count == 2 &&
            DemoDecisionModels.IsSupported("test/retired-model"), "Catalog ready when hosted service starts");
        await updater.StopAsync(CancellationToken.None);

        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(DecisionModelsRefreshService.HttpClientName);
        body = Catalog(new("test/new-model", "New release"),
            new(DemoDecisionModels.DefaultModelId, "Updated API name"),
            new("test/new-model:free", "New release (free)"),
            new("~test/latest", "Latest alias"));
        await DemoDecisionModels.RefreshAsync(client);
        var snapshot = DemoDecisionModels.All;
        Check(snapshot.Count == 4 && snapshot[0].Id == "test/new-model" && snapshot[0].Name == "New release" &&
            snapshot[1].Name == "Updated API name", "API names, IDs and ordering preserved");
        Check(!DemoDecisionModels.IsSupported("test/retired-model") &&
            DemoDecisionModels.RequireSupported("test/new-model:free") == "test/new-model:free" &&
            DemoDecisionModels.IsSupported("~test/latest"), "Validation follows refreshed catalog, including aliases");

        status = HttpStatusCode.ServiceUnavailable;
        await ExpectAsync<HttpRequestException>(() => DemoDecisionModels.RefreshAsync(client));
        Check(ReferenceEquals(snapshot, DemoDecisionModels.All), "HTTP failure retains catalog");
        status = HttpStatusCode.OK;
        foreach (var invalid in new[]
        {
            "not json", "null", "{}", "{\"data\":null}", "{\"data\":[]}", "{\"data\":[null]}",
            "{\"data\":[{\"id\":\"missing/name\"}]}",
            Catalog(new DemoDecisionModel(" ", "Invalid ID")), Catalog(new DemoDecisionModel("test/invalid", " ")),
            Catalog(new("test/duplicate", "First"), new("test/duplicate", "Second"))
        })
        {
            body = invalid;
            await ExpectAsync<JsonException>(() => DemoDecisionModels.RefreshAsync(client));
            Check(ReferenceEquals(snapshot, DemoDecisionModels.All), "Invalid response retains catalog");
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => DemoDecisionModels.RefreshAsync(client, cancellation.Token));
        Check(ReferenceEquals(snapshot, DemoDecisionModels.All), "Cancelled refresh retains catalog");
    }

    private static string Catalog(params DemoDecisionModel[] models)
        => JsonSerializer.Serialize(new { data = models, total_count = models.Length, links = new { next = (string?)null } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }
}
