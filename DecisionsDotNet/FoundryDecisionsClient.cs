using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace DecisionsDotNet;

/// <summary>The token scope and retry settings for <see cref="FoundryDecisionsClient"/>.</summary>
public sealed class FoundryDecisionsClientOptions : DecisionsClientOptions
{
    /// <summary>The scope requested from a <see cref="TokenCredential"/>. Ignored when the client uses an API key.</summary>
    public string TokenScope { get; init; } = AzureAuthentication.DefaultTokenScope;
}

/// <summary>
/// HTTP client for Microsoft decision models (such as Microsoft-Decision-1.1) deployed in Microsoft Foundry. It sends the
/// same Noul, Choice, and Score questions as the OpenRouter client to <c>{endpoint}/v1/systemone</c>.
/// </summary>
/// <remarks>
/// <para>
/// Foundry routes by deployment, so every request is sent with the deployment name this client was created with as its
/// model, and <see cref="DecisionsRequest.Model"/> is not sent.
/// </para>
/// <para>
/// Images in the state array are sent as Foundry assets and referenced from the state text as <c>image:&lt;name&gt;</c>.
/// </para>
/// </remarks>
public sealed class FoundryDecisionsClient : IDecisionsClient, IDisposable
{
    private const string Service = "Foundry";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<int> RetryableStatusCodes = new HashSet<int> { 429, 500, 502, 503, 504 };
    private readonly Uri _systemOneUri;
    private readonly AzureAuthentication _authentication;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly RetryPolicy _retry;

    /// <summary>Creates a client that authenticates with an API key.</summary>
    /// <param name="endpoint">The Foundry endpoint, such as <c>https://my-project.services.ai.azure.com/providers/microsoft</c>.
    /// A bare resource endpoint gets <c>/providers/microsoft</c> appended.</param>
    /// <param name="deploymentName">The name of the model deployment, sent as the request model.</param>
    /// <param name="apiKey">The resource's API key, sent in the <c>api-key</c> header.</param>
    /// <param name="httpClient">An optional caller-owned client. When omitted, the client creates and disposes its own.</param>
    /// <param name="options">Retry settings. Defaults apply when omitted.</param>
    public FoundryDecisionsClient(Uri endpoint, string deploymentName, string apiKey,
        HttpClient? httpClient = null, FoundryDecisionsClientOptions? options = null)
        : this(endpoint, deploymentName, AzureAuthentication.FromApiKey(apiKey, Service), httpClient, options ?? new())
    {
    }

    /// <summary>Creates a client that authenticates with Microsoft Entra ID.</summary>
    /// <param name="endpoint">The Foundry endpoint, such as <c>https://my-project.services.ai.azure.com/providers/microsoft</c>.
    /// A bare resource endpoint gets <c>/providers/microsoft</c> appended.</param>
    /// <param name="deploymentName">The name of the model deployment, sent as the request model.</param>
    /// <param name="credential">A credential such as <c>DefaultAzureCredential</c> or <c>AzureCliCredential</c>. Tokens are
    /// requested for <see cref="FoundryDecisionsClientOptions.TokenScope"/> and sent as a bearer token.</param>
    /// <param name="httpClient">An optional caller-owned client. When omitted, the client creates and disposes its own.</param>
    /// <param name="options">The token scope and retry settings. Defaults apply when omitted.</param>
    public FoundryDecisionsClient(Uri endpoint, string deploymentName, TokenCredential credential,
        HttpClient? httpClient = null, FoundryDecisionsClientOptions? options = null)
        : this(endpoint, deploymentName, Credential(credential, options ??= new()), httpClient, options)
    {
    }

    private FoundryDecisionsClient(Uri endpoint, string deploymentName, AzureAuthentication authentication,
        HttpClient? httpClient, FoundryDecisionsClientOptions options)
    {
        AzureEndpoint.Require(endpoint);
        DeploymentName = AzureEndpoint.RequireDeployment(deploymentName);
        _systemOneUri = SystemOneUri(endpoint);
        _authentication = authentication;
        _retry = RetryPolicy.From(options, RetryableStatusCodes);
        _retry.Validate(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>The deployment every request is sent to.</summary>
    public string DeploymentName { get; }

    private static AzureAuthentication Credential(TokenCredential credential, FoundryDecisionsClientOptions options)
        => AzureAuthentication.FromCredential(credential, options.TokenScope, options.Clock);

    internal static Uri SystemOneUri(Uri endpoint)
    {
        var root = endpoint.AbsolutePath.Trim('/').Length == 0 ? AzureEndpoint.Combine(endpoint, "providers/microsoft") : endpoint;
        return AzureEndpoint.Combine(root, "v1/systemone");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The request has a Box question, or images outside the top-level state array.</exception>
    /// <exception cref="DecisionsApiException">Foundry returned an HTTP error that was not retried or outlasted its retries.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response did not match the questions asked.</exception>
    public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Questions.Values.FirstOrDefault(question => question is not (NoulQuestion or ChoiceQuestion or ScoreQuestion))
            is { } unsupported)
            throw new NotSupportedException($"Foundry's Decisions API does not support {unsupported.GetType().Name}.");
        var (state, hasImages) = ImageInput.ValidateRequest(request);
        var body = new JsonObject { ["model"] = DeploymentName };
        if (hasImages)
        {
            var (text, assets) = BuildAssets(state);
            body["state"] = text;
            body["assets"] = assets;
        }
        else body["state"] = JsonNode.Parse(state.GetRawText());
        body["questions"] = JsonSerializer.SerializeToNode(request.Questions, JsonOptions);
        var json = body.ToJsonString(JsonOptions);

        var content = await DecisionsHttp.SendAsync(_httpClient, async attemptCancellation =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, _systemOneUri);
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
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
        var response = DecisionsResponseValidator.DeserializeAndValidate(content, request.Questions);
        return response.Provider is null ? response with { Provider = Service } : response;
    }

    /// <summary>Moves images into named assets and replaces each with an <c>image:&lt;name&gt;</c> reference in the state text.</summary>
    private static (string State, JsonObject Assets) BuildAssets(JsonElement state)
    {
        var lines = new List<string>();
        var assets = new JsonObject();
        foreach (var part in state.EnumerateArray())
        {
            if (!ImageInput.IsImage(part))
            {
                lines.Add(ImageInput.AsJsonText(part).ToString()!);
                continue;
            }
            var (mediaType, base64) = ImageInput.ReadImage(part);
            var format = mediaType["image/".Length..];
            var name = $"image-{assets.Count + 1}.{format}";
            assets[name] = new JsonObject { ["type"] = "image", ["format"] = format, ["data_base64"] = base64 };
            lines.Add($"image:{name}");
        }
        return (string.Join("\n", lines), assets);
    }

    /// <summary>Disposes the HTTP client when this instance created it. A caller-supplied client is left alone.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
