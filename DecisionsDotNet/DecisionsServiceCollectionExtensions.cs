using System.Diagnostics.CodeAnalysis;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DecisionsDotNet.Pipeline;

namespace DecisionsDotNet;

/// <summary>Registers Decisions providers behind <see cref="IDecisionsClient"/>.</summary>
/// <remarks>
/// The unkeyed <c>Add*DecisionsClient</c> methods all register <see cref="IDecisionsClient"/>, so when more than one is
/// called the last registration is the one injected. Use the <c>AddKeyed*</c> methods to run several providers side by side.
/// </remarks>
public static class DecisionsServiceCollectionExtensions
{
    /// <summary>Registers a Jev client using an HTTP or HTTPS service root, such as http://localhost:1234/.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="baseUri">The service root. The client appends v1/systemone and v1/models.</param>
    /// <param name="apiKey">The bearer API key, or a placeholder for servers without authentication.</param>
    public static IHttpClientBuilder AddLocalTypeSafeDecisionsClient(
        this IServiceCollection services,
        Uri baseUri,
        string apiKey = "local-key")
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return services.AddTypeSafeDecisionsClient(apiKey, new TypeSafeDecisionsClientOptions { BaseUri = baseUri });
    }

    /// <summary>Registers the direct TypeSafe client as <see cref="IDecisionsClient"/> with the given API key.</summary>
    public static IHttpClientBuilder AddTypeSafeDecisionsClient(
        this IServiceCollection services,
        string apiKey,
        TypeSafeDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A TypeSafe API key is required.", nameof(apiKey));

        return services.AddHttpClient("DecisionsDotNet.TypeSafe")
            .AddTypedClient<IDecisionsClient>((httpClient, _) => new TypeSafeDecisionsClient(apiKey, httpClient, options));
    }

    /// <summary>Registers the direct TypeSafe client as <see cref="IDecisionsClient"/> using the <c>TypeSafe:ApiKey</c> configuration value.</summary>
    public static IHttpClientBuilder AddTypeSafeDecisionsClient(
        this IServiceCollection services,
        IConfiguration configuration,
        TypeSafeDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["TypeSafe:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure TypeSafe:ApiKey before registering the TypeSafe Decisions client.");

        return services.AddTypeSafeDecisionsClient(apiKey, options);
    }

    /// <summary>Registers the OpenRouter client as <see cref="IDecisionsClient"/> with the given API key.</summary>
    public static IHttpClientBuilder AddOpenRouterDecisionsClient(
        this IServiceCollection services,
        string apiKey,
        OpenRouterDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An OpenRouter API key is required.", nameof(apiKey));

        return services.AddHttpClient("DecisionsDotNet.OpenRouter")
            .AddTypedClient<IDecisionsClient>((httpClient, _) => new OpenRouterDecisionsClient(apiKey, httpClient, options));
    }

    /// <summary>Registers the OpenRouter client as <see cref="IDecisionsClient"/> using the <c>OpenRouter:ApiKey</c> configuration value.</summary>
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
    public static IHttpClientBuilder AddKeyedTypeSafeDecisionsClient(
        this IServiceCollection services,
        object serviceKey,
        string apiKey,
        TypeSafeDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A TypeSafe API key is required.", nameof(apiKey));

        // A unique name per registration keeps keys with equal ToString() values on separate HTTP pipelines.
        var name = $"DecisionsDotNet.TypeSafe.{Guid.NewGuid():N}";
        services.AddKeyedTransient<IDecisionsClient>(serviceKey, (provider, _) =>
            new TypeSafeDecisionsClient(apiKey, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name), options));
        return services.AddHttpClient(name);
    }

    /// <summary>Registers a keyed direct TypeSafe client using the <c>TypeSafe:ApiKey</c> configuration value.</summary>
    public static IHttpClientBuilder AddKeyedTypeSafeDecisionsClient(
        this IServiceCollection services,
        object serviceKey,
        IConfiguration configuration,
        TypeSafeDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["TypeSafe:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure TypeSafe:ApiKey before registering the TypeSafe Decisions client.");

        return services.AddKeyedTypeSafeDecisionsClient(serviceKey, apiKey, options);
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

        // A unique name per registration keeps keys with equal ToString() values on separate HTTP pipelines.
        var name = $"DecisionsDotNet.OpenRouter.{Guid.NewGuid():N}";
        services.AddKeyedTransient<IDecisionsClient>(serviceKey, (provider, _) =>
            new OpenRouterDecisionsClient(apiKey, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name), options));
        return services.AddHttpClient(name);
    }

    /// <summary>Registers a keyed OpenRouter client using the <c>OpenRouter:ApiKey</c> configuration value.</summary>
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

    /// <summary>Registers the OpenAI client as <see cref="IDecisionsClient"/> with the given API key.</summary>
    public static IHttpClientBuilder AddOpenAIDecisionsClient(
        this IServiceCollection services,
        string apiKey,
        OpenAIDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An OpenAI API key is required.", nameof(apiKey));

        return services.AddHttpClient("DecisionsDotNet.OpenAI")
            .AddTypedClient<IDecisionsClient>((httpClient, _) => new OpenAIDecisionsClient(apiKey, httpClient, options));
    }

    /// <summary>Registers the OpenAI client as <see cref="IDecisionsClient"/> using the <c>OpenAI:ApiKey</c> configuration value.</summary>
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

        // A unique name per registration keeps keys with equal ToString() values on separate HTTP pipelines.
        var name = $"DecisionsDotNet.OpenAI.{Guid.NewGuid():N}";
        services.AddKeyedTransient<IDecisionsClient>(serviceKey, (provider, _) =>
            new OpenAIDecisionsClient(apiKey, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name), options));
        return services.AddHttpClient(name);
    }

    /// <summary>Registers a keyed OpenAI client using the <c>OpenAI:ApiKey</c> configuration value.</summary>
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

    /// <summary>Registers the Microsoft Foundry client as <see cref="IDecisionsClient"/>, authenticating with an API key.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The Foundry endpoint, such as <c>https://my-project.services.ai.azure.com/providers/microsoft</c>.</param>
    /// <param name="deploymentName">The model deployment every request is sent to.</param>
    /// <param name="apiKey">The resource's API key.</param>
    /// <param name="options">The token scope and retry settings.</param>
    public static IHttpClientBuilder AddFoundryDecisionsClient(this IServiceCollection services,
        Uri endpoint, string deploymentName, string apiKey, FoundryDecisionsClientOptions? options = null)
    {
        RequireAzure(endpoint, deploymentName);
        RequireKey(apiKey, "Foundry");
        return Register(services, null, "Foundry",
            httpClient => new FoundryDecisionsClient(endpoint, deploymentName, apiKey, httpClient, options));
    }

    /// <summary>Registers the Microsoft Foundry client as <see cref="IDecisionsClient"/>, authenticating with Microsoft Entra ID.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The Foundry endpoint, such as <c>https://my-project.services.ai.azure.com/providers/microsoft</c>.</param>
    /// <param name="deploymentName">The model deployment every request is sent to.</param>
    /// <param name="credential">A credential such as <c>DefaultAzureCredential</c>. It is shared by every client the registration creates.</param>
    /// <param name="options">The token scope and retry settings.</param>
    public static IHttpClientBuilder AddFoundryDecisionsClient(this IServiceCollection services,
        Uri endpoint, string deploymentName, TokenCredential credential, FoundryDecisionsClientOptions? options = null)
    {
        RequireAzure(endpoint, deploymentName);
        ArgumentNullException.ThrowIfNull(credential);
        return Register(services, null, "Foundry",
            httpClient => new FoundryDecisionsClient(endpoint, deploymentName, credential, httpClient, options));
    }

    /// <summary>
    /// Registers the Microsoft Foundry client as <see cref="IDecisionsClient"/> from the <c>Foundry:Endpoint</c>,
    /// <c>Foundry:DeploymentName</c>, and <c>Foundry:ApiKey</c> configuration values. Without an API key, <paramref name="credential"/> is used.
    /// </summary>
    public static IHttpClientBuilder AddFoundryDecisionsClient(this IServiceCollection services,
        IConfiguration configuration, TokenCredential? credential = null, FoundryDecisionsClientOptions? options = null)
    {
        var (endpoint, deploymentName, apiKey) = AzureSettings(configuration, "Foundry", credential);
        return apiKey is not null
            ? services.AddFoundryDecisionsClient(endpoint, deploymentName, apiKey, options)
            : services.AddFoundryDecisionsClient(endpoint, deploymentName, credential!, options);
    }

    /// <summary>Registers a Microsoft Foundry client as a keyed <see cref="IDecisionsClient"/>, authenticating with an API key.</summary>
    public static IHttpClientBuilder AddKeyedFoundryDecisionsClient(this IServiceCollection services, object serviceKey,
        Uri endpoint, string deploymentName, string apiKey, FoundryDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);
        RequireAzure(endpoint, deploymentName);
        RequireKey(apiKey, "Foundry");
        return Register(services, serviceKey, "Foundry",
            httpClient => new FoundryDecisionsClient(endpoint, deploymentName, apiKey, httpClient, options));
    }

    /// <summary>Registers a Microsoft Foundry client as a keyed <see cref="IDecisionsClient"/>, authenticating with Microsoft Entra ID.</summary>
    public static IHttpClientBuilder AddKeyedFoundryDecisionsClient(this IServiceCollection services, object serviceKey,
        Uri endpoint, string deploymentName, TokenCredential credential, FoundryDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);
        RequireAzure(endpoint, deploymentName);
        ArgumentNullException.ThrowIfNull(credential);
        return Register(services, serviceKey, "Foundry",
            httpClient => new FoundryDecisionsClient(endpoint, deploymentName, credential, httpClient, options));
    }

    /// <summary>Registers a keyed Microsoft Foundry client from the <c>Foundry</c> configuration section, using <paramref name="credential"/> when no API key is configured.</summary>
    public static IHttpClientBuilder AddKeyedFoundryDecisionsClient(this IServiceCollection services, object serviceKey,
        IConfiguration configuration, TokenCredential? credential = null, FoundryDecisionsClientOptions? options = null)
    {
        var (endpoint, deploymentName, apiKey) = AzureSettings(configuration, "Foundry", credential);
        return apiKey is not null
            ? services.AddKeyedFoundryDecisionsClient(serviceKey, endpoint, deploymentName, apiKey, options)
            : services.AddKeyedFoundryDecisionsClient(serviceKey, endpoint, deploymentName, credential!, options);
    }

    /// <summary>Registers the Azure OpenAI client as <see cref="IDecisionsClient"/>, authenticating with an API key.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The resource endpoint, such as <c>https://my-resource.openai.azure.com/</c>.</param>
    /// <param name="deploymentName">The model deployment every request is sent to.</param>
    /// <param name="apiKey">The resource's API key.</param>
    /// <param name="options">The token scope and retry settings.</param>
    public static IHttpClientBuilder AddAzureOpenAIDecisionsClient(this IServiceCollection services,
        Uri endpoint, string deploymentName, string apiKey, AzureOpenAIDecisionsClientOptions? options = null)
    {
        RequireAzure(endpoint, deploymentName);
        RequireKey(apiKey, "Azure OpenAI");
        return Register(services, null, "AzureOpenAI",
            httpClient => new AzureOpenAIDecisionsClient(endpoint, deploymentName, apiKey, httpClient, options));
    }

    /// <summary>Registers the Azure OpenAI client as <see cref="IDecisionsClient"/>, authenticating with Microsoft Entra ID.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The resource endpoint, such as <c>https://my-resource.openai.azure.com/</c>.</param>
    /// <param name="deploymentName">The model deployment every request is sent to.</param>
    /// <param name="credential">A credential such as <c>DefaultAzureCredential</c>. It is shared by every client the registration creates.</param>
    /// <param name="options">The token scope and retry settings.</param>
    public static IHttpClientBuilder AddAzureOpenAIDecisionsClient(this IServiceCollection services,
        Uri endpoint, string deploymentName, TokenCredential credential, AzureOpenAIDecisionsClientOptions? options = null)
    {
        RequireAzure(endpoint, deploymentName);
        ArgumentNullException.ThrowIfNull(credential);
        return Register(services, null, "AzureOpenAI",
            httpClient => new AzureOpenAIDecisionsClient(endpoint, deploymentName, credential, httpClient, options));
    }

    /// <summary>
    /// Registers the Azure OpenAI client as <see cref="IDecisionsClient"/> from the <c>AzureOpenAI:Endpoint</c>,
    /// <c>AzureOpenAI:DeploymentName</c>, and <c>AzureOpenAI:ApiKey</c> configuration values. Without an API key, <paramref name="credential"/> is used.
    /// </summary>
    public static IHttpClientBuilder AddAzureOpenAIDecisionsClient(this IServiceCollection services,
        IConfiguration configuration, TokenCredential? credential = null, AzureOpenAIDecisionsClientOptions? options = null)
    {
        var (endpoint, deploymentName, apiKey) = AzureSettings(configuration, "AzureOpenAI", credential);
        return apiKey is not null
            ? services.AddAzureOpenAIDecisionsClient(endpoint, deploymentName, apiKey, options)
            : services.AddAzureOpenAIDecisionsClient(endpoint, deploymentName, credential!, options);
    }

    /// <summary>Registers an Azure OpenAI client as a keyed <see cref="IDecisionsClient"/>, authenticating with an API key.</summary>
    public static IHttpClientBuilder AddKeyedAzureOpenAIDecisionsClient(this IServiceCollection services, object serviceKey,
        Uri endpoint, string deploymentName, string apiKey, AzureOpenAIDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);
        RequireAzure(endpoint, deploymentName);
        RequireKey(apiKey, "Azure OpenAI");
        return Register(services, serviceKey, "AzureOpenAI",
            httpClient => new AzureOpenAIDecisionsClient(endpoint, deploymentName, apiKey, httpClient, options));
    }

    /// <summary>Registers an Azure OpenAI client as a keyed <see cref="IDecisionsClient"/>, authenticating with Microsoft Entra ID.</summary>
    public static IHttpClientBuilder AddKeyedAzureOpenAIDecisionsClient(this IServiceCollection services, object serviceKey,
        Uri endpoint, string deploymentName, TokenCredential credential, AzureOpenAIDecisionsClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);
        RequireAzure(endpoint, deploymentName);
        ArgumentNullException.ThrowIfNull(credential);
        return Register(services, serviceKey, "AzureOpenAI",
            httpClient => new AzureOpenAIDecisionsClient(endpoint, deploymentName, credential, httpClient, options));
    }

    /// <summary>Registers a keyed Azure OpenAI client from the <c>AzureOpenAI</c> configuration section, using <paramref name="credential"/> when no API key is configured.</summary>
    public static IHttpClientBuilder AddKeyedAzureOpenAIDecisionsClient(this IServiceCollection services, object serviceKey,
        IConfiguration configuration, TokenCredential? credential = null, AzureOpenAIDecisionsClientOptions? options = null)
    {
        var (endpoint, deploymentName, apiKey) = AzureSettings(configuration, "AzureOpenAI", credential);
        return apiKey is not null
            ? services.AddKeyedAzureOpenAIDecisionsClient(serviceKey, endpoint, deploymentName, apiKey, options)
            : services.AddKeyedAzureOpenAIDecisionsClient(serviceKey, endpoint, deploymentName, credential!, options);
    }

    private static void RequireAzure(Uri endpoint, string deploymentName)
    {
        AzureEndpoint.Require(endpoint);
        AzureEndpoint.RequireDeployment(deploymentName);
    }

    private static void RequireKey(string apiKey, string service)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException($"A {service} API key is required.", nameof(apiKey));
    }

    private static (Uri Endpoint, string DeploymentName, string? ApiKey) AzureSettings(
        IConfiguration configuration, string section, TokenCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = configuration[$"{section}:Endpoint"];
        var deploymentName = configuration[$"{section}:DeploymentName"];
        var apiKey = configuration[$"{section}:ApiKey"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Configure {section}:Endpoint as an absolute HTTPS URI.");
        if (string.IsNullOrWhiteSpace(deploymentName))
            throw new InvalidOperationException($"Configure {section}:DeploymentName.");
        if (string.IsNullOrWhiteSpace(apiKey) && credential is null)
            throw new InvalidOperationException($"Configure {section}:ApiKey or pass a TokenCredential.");
        return (uri, deploymentName, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
    }

    private static IHttpClientBuilder Register(IServiceCollection services, object? serviceKey, string provider,
        Func<HttpClient, IDecisionsClient> create)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (serviceKey is null)
            return services.AddHttpClient($"DecisionsDotNet.{provider}").AddTypedClient<IDecisionsClient>((httpClient, _) => create(httpClient));

        // A unique name per registration keeps keys with equal ToString() values on separate HTTP pipelines.
        var name = $"DecisionsDotNet.{provider}.{Guid.NewGuid():N}";
        services.AddKeyedTransient<IDecisionsClient>(serviceKey, (serviceProvider, _) =>
            create(serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(name)));
        return services.AddHttpClient(name);
    }

    /// <summary>
    /// Registers the pipeline as the unkeyed <see cref="IDecisionsClient"/>. Resolve the inner client by key, such as from
    /// <c>AddKeyedOpenRouterDecisionsClient</c>; resolving the unkeyed client from inside the factory would recurse.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="innerClientFactory">Creates the provider client the pipeline wraps.</param>
    /// <param name="lifetime">The registration lifetime. Transient by default, matching the provider clients, whose HTTP handlers rotate.</param>
    [Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
    public static DecisionsClientBuilder AddDecisionsClient(this IServiceCollection services,
        Func<IServiceProvider, IDecisionsClient> innerClientFactory, ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = new DecisionsClientBuilder(innerClientFactory);
        services.Add(new ServiceDescriptor(typeof(IDecisionsClient), provider => builder.Build(provider), lifetime));
        return builder;
    }

    /// <summary>Registers the pipeline as a keyed <see cref="IDecisionsClient"/>.</summary>
    [Experimental(DecisionsDiagnostics.Conditions, UrlFormat = DecisionsDiagnostics.UrlFormat)]
    public static DecisionsClientBuilder AddKeyedDecisionsClient(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, IDecisionsClient> innerClientFactory, ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        var builder = new DecisionsClientBuilder(innerClientFactory);
        services.Add(new ServiceDescriptor(typeof(IDecisionsClient), serviceKey, (provider, _) => builder.Build(provider), lifetime));
        return builder;
    }
}
