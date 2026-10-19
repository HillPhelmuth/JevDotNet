using DecisionsDotNet;
using System.Text.Json;

namespace JevDotNet.Demo.Services;

public sealed class DecisionModelsRefreshService(
    IHttpClientFactory clients,
    ILogger<DecisionModelsRefreshService> logger) : BackgroundService
{
    public const string HttpClientName = "DecisionModels";

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Model selectors and synchronous request validation need the catalog before serving pages.
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await RefreshAsync(stoppingToken); }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested &&
                    error is JevApiException or HttpRequestException or JsonException or OperationCanceledException)
                {
                    logger.LogWarning(error, "Could not refresh Decisions models; retaining the previous catalog.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient(HttpClientName);
        await DemoDecisionModels.RefreshAsync(client, cancellationToken);
    }
}
