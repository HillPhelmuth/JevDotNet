using JevDotNet;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers one Decisions provider behind <see cref="IDecisionsClient"/>.</summary>
public static class ServiceCollectionsExtensions
{
    public static IHttpClientBuilder AddJevDecisionsClient(
        this IServiceCollection services,
        string apiKey,
        JevClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A TypeSafe API key is required.", nameof(apiKey));

        return services.AddHttpClient("JevDotNet.TypeSafe")
            .AddTypedClient<IDecisionsClient>((httpClient, _) => new JevClient(apiKey, httpClient, options));
    }

    public static IHttpClientBuilder AddJevDecisionsClient(
        this IServiceCollection services,
        IConfiguration configuration,
        JevClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["TypeSafe:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure TypeSafe:ApiKey before registering the TypeSafe Decisions client.");

        return services.AddJevDecisionsClient(apiKey, options);
    }

    public static IHttpClientBuilder AddOpenRouterDecisionsClient(
        this IServiceCollection services,
        string apiKey,
        OpenRouterDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An OpenRouter API key is required.", nameof(apiKey));

        return services.AddHttpClient("JevDotNet.OpenRouter")
            .AddTypedClient<IDecisionsClient>((httpClient, _) => new OpenRouterDecisionsClient(apiKey, httpClient, options));
    }

    public static IHttpClientBuilder AddOpenRouterDecisionsClient(
        this IServiceCollection services,
        IConfiguration configuration,
        OpenRouterDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["OpenRouter:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure OpenRouter:ApiKey before registering the OpenRouter Decisions client.");

        return services.AddOpenRouterDecisionsClient(apiKey, options);
    }
}
