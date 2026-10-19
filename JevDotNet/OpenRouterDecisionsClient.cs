using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DecisionsDotNet;

public sealed class OpenRouterDecisionsClientOptions
{
    public Uri BaseUri { get; init; } = new("https://openrouter.ai/");
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Input modalities keyed by exact model ID. Overrides bypass catalog discovery for that model.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ModelInputModalitiesOverrides { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>HTTP client for OpenRouter's Decisions API.</summary>
public sealed class OpenRouterDecisionsClient : IDecisionsClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly OpenRouterDecisionsClientOptions _options;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _modalityOverrides;
    private readonly RetryPolicy _retry;
    private static readonly IReadOnlySet<int> RetryableStatusCodes = new HashSet<int> { 429, 502, 503, 529 };

    public OpenRouterDecisionsClient(
        string apiKey,
        HttpClient? httpClient = null,
        OpenRouterDecisionsClientOptions? options = null)
    {
        _apiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey : throw new ArgumentException("An OpenRouter API key is required.", nameof(apiKey));
        _options = options ?? new OpenRouterDecisionsClientOptions();
        _modalityOverrides = ModelInputModalities.Snapshot(_options.ModelInputModalitiesOverrides);
        if (_options.BaseUri is null || !_options.BaseUri.IsAbsoluteUri || _options.BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("BaseUri must be an absolute HTTPS URI.", nameof(options));
        _retry = new RetryPolicy(_options.MaxAttempts, _options.InitialRetryDelay, _options.MaxRetryDelay, RetryableStatusCodes);
        _retry.Validate(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public static OpenRouterDecisionsClient FromEnvironment(
        HttpClient? httpClient = null,
        OpenRouterDecisionsClientOptions? options = null)
        => new(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
            ?? throw new InvalidOperationException("OPENROUTER_API_KEY is not set."), httpClient, options);

    public async Task<DecisionsResponse> EvaluateAsync(
        DecisionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (state, hasImages) = ImageInput.ValidateRequest(request);
        var modalities = _modalityOverrides.TryGetValue(request.Model, out var configured)
            ? configured
            : await OpenRouterModelCatalog.Shared.ResolveAsync(_httpClient, _options.BaseUri,
                request.Model, cancellationToken).ConfigureAwait(false);
        if (hasImages) ImageInput.RequireImageSupport(modalities, request.Model);
        object wireState = ImageInput.IsMultimodal(modalities) ? state : ImageInput.AsJsonText(state);
        var body = JsonSerializer.Serialize(new
        {
            state = wireState,
            model = request.Model,
            questions = request.Questions
        }, JsonOptions);

        var content = await DecisionsHttp.SendAsync(_httpClient, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, "api/alpha/decisions"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return message;
        }, _retry, "OpenRouter", cancellationToken).ConfigureAwait(false);
        return JevResponseValidator.DeserializeAndValidate(content, request.Questions);
    }

    /// <summary>Lists OpenRouter's Decisions models with their input modalities. The catalog is public; no API key is sent.</summary>
    public async Task<IReadOnlyList<OpenRouterDecisionsModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var content = await DecisionsHttp.SendAsync(_httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri, OpenRouterModelCatalog.Path)),
            _retry, "OpenRouter", cancellationToken).ConfigureAwait(false);
        return OpenRouterModelCatalog.Parse(content);
    }

    /// <summary>Lists OpenRouter's Decisions models without constructing a client or supplying an API key.</summary>
    public static async Task<IReadOnlyList<OpenRouterDecisionsModel>> ListModelsAsync(
        HttpClient httpClient, Uri? baseUri = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        baseUri ??= new OpenRouterDecisionsClientOptions().BaseUri;
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("BaseUri must be an absolute HTTPS URI.", nameof(baseUri));
        var defaults = new OpenRouterDecisionsClientOptions();
        var content = await DecisionsHttp.SendAsync(httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, OpenRouterModelCatalog.Path)),
            new RetryPolicy(defaults.MaxAttempts, defaults.InitialRetryDelay, defaults.MaxRetryDelay, RetryableStatusCodes),
            "OpenRouter", cancellationToken).ConfigureAwait(false);
        return OpenRouterModelCatalog.Parse(content);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
