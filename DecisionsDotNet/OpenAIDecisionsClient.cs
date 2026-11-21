using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;

namespace DecisionsDotNet;

/// <summary>The base URI, organization and project headers, and retry settings for <see cref="OpenAIDecisionsClient"/>.</summary>
public sealed class OpenAIDecisionsClientOptions : DecisionsClientOptions
{
    /// <summary>Absolute HTTPS service root. Evaluations go to v1/decisions under it.</summary>
    public Uri BaseUri { get; init; } = new("https://api.openai.com/");
    /// <summary>Sent as OpenAI-Organization when set.</summary>
    public string? Organization { get; init; }
    /// <summary>Sent as OpenAI-Project when set.</summary>
    public string? Project { get; init; }
}

/// <summary>
/// HTTP client for OpenAI's Decisions API (<c>POST /v1/decisions</c>). It accepts the same <see cref="DecisionsRequest"/>
/// as the other providers and maps Noul questions to predicates, Choice to choices, and Score to levels.
/// </summary>
public sealed class OpenAIDecisionsClient : IDecisionsClient, IDisposable
{
    /// <summary>The model OpenAI's Decisions API supports. Pass it as the request model.</summary>
    public static string DefaultModel => "gpt-6-luna";

    private const string Service = "OpenAI";
    private static readonly IReadOnlySet<int> RetryableStatusCodes = new HashSet<int> { 429, 500, 502, 503, 504 };
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly OpenAIDecisionsClientOptions _options;
    private readonly RetryPolicy _retry;

    /// <summary>Creates a client.</summary>
    /// <param name="apiKey">An OpenAI API key.</param>
    /// <param name="httpClient">An optional caller-owned client. When omitted, the client creates and disposes its own.</param>
    /// <param name="options">The base URI, headers, and retry settings. Defaults apply when omitted.</param>
    public OpenAIDecisionsClient(string apiKey, HttpClient? httpClient = null, OpenAIDecisionsClientOptions? options = null)
    {
        _apiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey : throw new ArgumentException("An OpenAI API key is required.", nameof(apiKey));
        _options = options ?? new OpenAIDecisionsClientOptions();
        if (_options.BaseUri is null || !_options.BaseUri.IsAbsoluteUri || _options.BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("BaseUri must be an absolute HTTPS URI.", nameof(options));
        _retry = RetryPolicy.From(_options, RetryableStatusCodes);
        _retry.Validate(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>Creates a client using the <c>OPENAI_API_KEY</c> environment variable.</summary>
    public static OpenAIDecisionsClient FromEnvironment(HttpClient? httpClient = null, OpenAIDecisionsClientOptions? options = null)
        => new(Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY is not set."), httpClient, options);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The request has a Box question, or images outside the top-level state array.</exception>
    /// <exception cref="DecisionsApiException">OpenAI returned an HTTP error that was not retried or outlasted its retries.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response did not match the questions asked.</exception>
    public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (state, hasImages) = ImageInput.ValidateRequest(request);
        var body = OpenAIDecisionsWire.BuildBody(request.Model, request.Questions, state, hasImages, Service);

        var content = await DecisionsHttp.SendAsync(_httpClient, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, "v1/decisions"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (!string.IsNullOrWhiteSpace(_options.Organization)) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (!string.IsNullOrWhiteSpace(_options.Project)) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return message;
        }, _retry, Service, cancellationToken).ConfigureAwait(false);
        return OpenAIDecisionsWire.ReadResponse(content, request.Questions, Service);
    }

    /// <summary>Disposes the HTTP client when this instance created it. A caller-supplied client is left alone.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
