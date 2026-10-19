using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DecisionsDotNet;

/// <summary>Registers one Decisions provider behind <see cref="IDecisionsClient"/>.</summary>
public static class ServiceCollectionsExtensions
{
    /// <summary>Registers a Jev client using an HTTP or HTTPS service root, such as http://localhost:1234/.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="baseUri">The service root. The client appends v1/systemone and v1/models.</param>
    /// <param name="apiKey">The bearer API key, or a placeholder for servers without authentication.</param>
    public static IHttpClientBuilder AddLocalJevSystemOneClient(
        this IServiceCollection services,
        Uri baseUri,
        string apiKey = "local-key")
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return services.AddJevSystemOneClient(apiKey, new JevClientOptions { BaseUri = baseUri });
    }

    public static IHttpClientBuilder AddJevSystemOneClient(
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

    public static IHttpClientBuilder AddJevSystemOneClient(
        this IServiceCollection services,
        IConfiguration configuration,
        JevClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["TypeSafe:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure TypeSafe:ApiKey before registering the TypeSafe Decisions client.");

        return services.AddJevSystemOneClient(apiKey, options);
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

    /// <summary>Registers a direct TypeSafe client as a keyed <see cref="IDecisionsClient"/>, so it can sit beside other providers.</summary>
    public static IHttpClientBuilder AddKeyedJevSystemOneClient(
        this IServiceCollection services,
        object serviceKey,
        string apiKey,
        JevClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A TypeSafe API key is required.", nameof(apiKey));

        var name = $"JevDotNet.TypeSafe.{serviceKey}";
        services.AddKeyedTransient<IDecisionsClient>(serviceKey, (provider, _) =>
            new JevClient(apiKey, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name), options));
        return services.AddHttpClient(name);
    }

    public static IHttpClientBuilder AddKeyedJevSystemOneClient(
        this IServiceCollection services,
        object serviceKey,
        IConfiguration configuration,
        JevClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["TypeSafe:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure TypeSafe:ApiKey before registering the TypeSafe Decisions client.");

        return services.AddKeyedJevSystemOneClient(serviceKey, apiKey, options);
    }

    /// <summary>Registers an OpenRouter client as a keyed <see cref="IDecisionsClient"/>, so it can sit beside other providers.</summary>
    public static IHttpClientBuilder AddKeyedOpenRouterDecisionsClient(
        this IServiceCollection services,
        object serviceKey,
        string apiKey,
        OpenRouterDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An OpenRouter API key is required.", nameof(apiKey));

        var name = $"JevDotNet.OpenRouter.{serviceKey}";
        services.AddKeyedTransient<IDecisionsClient>(serviceKey, (provider, _) =>
            new OpenRouterDecisionsClient(apiKey, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name), options));
        return services.AddHttpClient(name);
    }

    public static IHttpClientBuilder AddKeyedOpenRouterDecisionsClient(
        this IServiceCollection services,
        object serviceKey,
        IConfiguration configuration,
        OpenRouterDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["OpenRouter:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure OpenRouter:ApiKey before registering the OpenRouter Decisions client.");

        return services.AddKeyedOpenRouterDecisionsClient(serviceKey, apiKey, options);
    }

    public static IHttpClientBuilder AddOpenAIDecisionsClient(
        this IServiceCollection services,
        string apiKey,
        OpenAIDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An OpenAI API key is required.", nameof(apiKey));

        return services.AddHttpClient("JevDotNet.OpenAI")
            .AddTypedClient<IDecisionsClient>((httpClient, _) => new OpenAIDecisionsClient(apiKey, httpClient, options));
    }

    public static IHttpClientBuilder AddOpenAIDecisionsClient(
        this IServiceCollection services,
        IConfiguration configuration,
        OpenAIDecisionsClientOptions? options = null)
        => services.AddOpenAIDecisionsClient(OpenAIKey(configuration), options);

    /// <summary>Registers an OpenAI client as a keyed <see cref="IDecisionsClient"/>, so it can sit beside other providers.</summary>
    public static IHttpClientBuilder AddKeyedOpenAIDecisionsClient(
        this IServiceCollection services,
        object serviceKey,
        string apiKey,
        OpenAIDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An OpenAI API key is required.", nameof(apiKey));

        var name = $"JevDotNet.OpenAI.{serviceKey}";
        services.AddKeyedTransient<IDecisionsClient>(serviceKey, (provider, _) =>
            new OpenAIDecisionsClient(apiKey, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name), options));
        return services.AddHttpClient(name);
    }

    public static IHttpClientBuilder AddKeyedOpenAIDecisionsClient(
        this IServiceCollection services,
        object serviceKey,
        IConfiguration configuration,
        OpenAIDecisionsClientOptions? options = null)
        => services.AddKeyedOpenAIDecisionsClient(serviceKey, OpenAIKey(configuration), options);

    private static string OpenAIKey(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["OpenAI:ApiKey"];
        return !string.IsNullOrWhiteSpace(apiKey) ? apiKey
            : throw new InvalidOperationException("Configure OpenAI:ApiKey before registering the OpenAI Decisions client.");
    }
}
