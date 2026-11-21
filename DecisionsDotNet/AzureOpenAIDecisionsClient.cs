using System.Diagnostics.CodeAnalysis;
using System.Text;
using Azure.Core;

namespace DecisionsDotNet;

/// <summary>The token scope and retry settings for <see cref="AzureOpenAIDecisionsClient"/>.</summary>
public sealed class AzureOpenAIDecisionsClientOptions : DecisionsClientOptions
{
    /// <summary>The scope requested from a <see cref="TokenCredential"/>. Ignored when the client uses an API key.</summary>
    public string TokenScope { get; init; } = AzureAuthentication.DefaultTokenScope;
}

/// <summary>
/// HTTP client for the Azure OpenAI Decisions API (<c>POST {endpoint}/openai/v1/decisions</c>). It uses the same wire format
/// as <see cref="OpenAIDecisionsClient"/>: Noul questions become predicates, Choice becomes choices, and Score becomes levels.
/// </summary>
/// <remarks>
/// <para>
/// Azure OpenAI routes by deployment, so every request is sent with the deployment name this client was created with as its
/// model, and <see cref="DecisionsRequest.Model"/> is not sent.
/// </para>
/// </remarks>
public sealed class AzureOpenAIDecisionsClient : IDecisionsClient, IDisposable
{
    private const string Service = "Azure OpenAI";
    private static readonly IReadOnlySet<int> RetryableStatusCodes = new HashSet<int> { 429, 500, 502, 503, 504 };
    private readonly Uri _decisionsUri;
    private readonly AzureAuthentication _authentication;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly RetryPolicy _retry;

    /// <summary>Creates a client that authenticates with an API key.</summary>
    /// <param name="endpoint">The Azure OpenAI resource endpoint, such as <c>https://my-resource.openai.azure.com/</c>.</param>
    /// <param name="deploymentName">The name of the model deployment (for example a <c>gpt-6-luna</c> deployment), sent as the request model.</param>
    /// <param name="apiKey">The resource's API key, sent in the <c>api-key</c> header.</param>
    /// <param name="httpClient">An optional caller-owned client. When omitted, the client creates and disposes its own.</param>
    /// <param name="options">Retry settings. Defaults apply when omitted.</param>
    public AzureOpenAIDecisionsClient(Uri endpoint, string deploymentName, string apiKey,
        HttpClient? httpClient = null, AzureOpenAIDecisionsClientOptions? options = null)
        : this(endpoint, deploymentName, AzureAuthentication.FromApiKey(apiKey, Service), httpClient, options ?? new())
    {
    }

    /// <summary>Creates a client that authenticates with Microsoft Entra ID.</summary>
    /// <param name="endpoint">The Azure OpenAI resource endpoint, such as <c>https://my-resource.openai.azure.com/</c>.</param>
    /// <param name="deploymentName">The name of the model deployment (for example a <c>gpt-6-luna</c> deployment), sent as the request model.</param>
    /// <param name="credential">A credential such as <c>DefaultAzureCredential</c> or <c>AzureCliCredential</c>. Tokens are
    /// requested for <see cref="AzureOpenAIDecisionsClientOptions.TokenScope"/> and sent as a bearer token.</param>
    /// <param name="httpClient">An optional caller-owned client. When omitted, the client creates and disposes its own.</param>
    /// <param name="options">The token scope and retry settings. Defaults apply when omitted.</param>
    public AzureOpenAIDecisionsClient(Uri endpoint, string deploymentName, TokenCredential credential,
        HttpClient? httpClient = null, AzureOpenAIDecisionsClientOptions? options = null)
        : this(endpoint, deploymentName, Credential(credential, options ??= new()), httpClient, options)
    {
    }

    private AzureOpenAIDecisionsClient(Uri endpoint, string deploymentName, AzureAuthentication authentication,
        HttpClient? httpClient, AzureOpenAIDecisionsClientOptions options)
    {
        AzureEndpoint.Require(endpoint);
        DeploymentName = AzureEndpoint.RequireDeployment(deploymentName);
        _decisionsUri = DecisionsUri(endpoint);
        _authentication = authentication;
        _retry = RetryPolicy.From(options, RetryableStatusCodes);
        _retry.Validate(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>The deployment every request is sent to.</summary>
    public string DeploymentName { get; }

    private static AzureAuthentication Credential(TokenCredential credential, AzureOpenAIDecisionsClientOptions options)
        => AzureAuthentication.FromCredential(credential, options.TokenScope, options.Clock);

    /// <summary>Accepts the resource endpoint with or without the <c>openai/v1</c> suffix the OpenAI SDKs use as their base URL.</summary>
    internal static Uri DecisionsUri(Uri endpoint)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        var root = path.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase) ? endpoint
            : path.EndsWith("/openai", StringComparison.OrdinalIgnoreCase) ? AzureEndpoint.Combine(endpoint, "v1")
            : AzureEndpoint.Combine(endpoint, "openai/v1");
        return AzureEndpoint.Combine(root, "decisions");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The request has a Box question, or images outside the top-level state array.</exception>
    /// <exception cref="DecisionsApiException">Azure OpenAI returned an HTTP error that was not retried or outlasted its retries.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response did not match the questions asked.</exception>
    public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (state, hasImages) = ImageInput.ValidateRequest(request);
        var body = OpenAIDecisionsWire.BuildBody(DeploymentName, request.Questions, state, hasImages, Service);

        var content = await DecisionsHttp.SendAsync(_httpClient, async attemptCancellation =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, _decisionsUri);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                await _authentication.ApplyAsync(message, attemptCancellation).ConfigureAwait(false);
                return message;
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }, _retry, Service, cancellationToken).ConfigureAwait(false);
        return OpenAIDecisionsWire.ReadResponse(content, request.Questions, Service);
    }

    /// <summary>Disposes the HTTP client when this instance created it. A caller-supplied client is left alone.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
