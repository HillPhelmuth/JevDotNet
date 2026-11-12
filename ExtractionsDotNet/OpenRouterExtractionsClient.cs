using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ExtractionsDotNet;

/// <summary>Client for OpenRouter's alpha Extractions API.</summary>
public sealed class OpenRouterExtractionsClient : IDisposable
{
    private const string ExtractionsPath = "/api/alpha/extractions";
    private const string ModelsPath = "/api/v1/models?endpoints=/api/alpha/extractions";
    private static readonly Uri DefaultBaseUri = new("https://openrouter.ai/");

    private readonly AuthenticationHeaderValue _authorization;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <param name="apiKey">An OpenRouter API key.</param>
    /// <param name="httpClient">An optional caller-owned client.</param>
    /// <param name="baseUri">The HTTPS origin serving OpenRouter's API routes.</param>
    public OpenRouterExtractionsClient(string apiKey, HttpClient? httpClient = null, Uri? baseUri = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _baseUri = ValidateBaseUri(baseUri);
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public static OpenRouterExtractionsClient FromEnvironment(HttpClient? httpClient = null, Uri? baseUri = null)
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OPENROUTER_API_KEY is not set.");

        return new OpenRouterExtractionsClient(key, httpClient, baseUri);
    }

    public async Task<ExtractionResponse> ExtractAsync(
        ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.State);
        ArgumentNullException.ThrowIfNull(request.Fields);

        // Use the same field definitions for serialization and decoding after the HTTP call.
        var fields = new Dictionary<string, ExtractionField>(request.Fields, StringComparer.Ordinal);
        ValidateFields(fields);
        if (request.State.Length == 0 && request.Assets is not { Count: > 0 })
            throw new ArgumentException("Supply state text or at least one asset.", nameof(request));

        if (request.Assets is { } assets)
        {
            foreach (var (name, asset) in assets)
            {
                if (string.IsNullOrWhiteSpace(name) || asset is null)
                    throw new ArgumentException("Each asset must have a nonempty name and a value.", nameof(request));
            }
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, ExtractionsPath));
        message.Headers.Authorization = _authorization;
        message.Content = JsonContent.Create(request with { Fields = fields }, options: ExtractionJson.Options);
        var content = await SendAsync(_httpClient, message, cancellationToken).ConfigureAwait(false);
        return ExtractionJson.ReadResponse(content, fields);
    }

    public Task<IReadOnlyList<ExtractionModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ListModelsAsync(_httpClient, _baseUri, cancellationToken);
    }

    /// <summary>Lists models serving the Extractions route. The SDK adds no authorization header.</summary>
    public static async Task<IReadOnlyList<ExtractionModel>> ListModelsAsync(
        HttpClient httpClient, Uri? baseUri = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        cancellationToken.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(ValidateBaseUri(baseUri), ModelsPath));
        var content = await SendAsync(httpClient, message, cancellationToken).ConfigureAwait(false);
        return ExtractionJson.ReadModels(content);
    }

    public void Dispose()
    {
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static Uri ValidateBaseUri(Uri? baseUri)
    {
        baseUri ??= DefaultBaseUri;
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The base URI must be an absolute HTTPS URI.", nameof(baseUri));

        return baseUri;
    }

    private static void ValidateFields(IReadOnlyDictionary<string, ExtractionField> fields)
    {
        if (fields.Count == 0)
            throw new ArgumentException("Supply at least one extraction field.", nameof(fields));

        foreach (var (name, field) in fields)
        {
            if (string.IsNullOrWhiteSpace(name) || field is null)
                throw new ArgumentException("Each field must have a nonempty name and a specification.", nameof(fields));

            if (field is RangeField { Step: not null, CalendarStep: not null })
                throw new ArgumentException($"Field '{name}' must use either a numeric step or a calendar step.", nameof(fields));
        }
    }

    private static async Task<string> SendAsync(
        HttpClient client, HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ExtractionsApiException(response.StatusCode, content);

        return content;
    }
}

public sealed class ExtractionsApiException(HttpStatusCode statusCode, string responseBody)
    : HttpRequestException($"OpenRouter Extractions returned HTTP {(int)statusCode} ({statusCode}).", null, statusCode)
{
    public string ResponseBody { get; } = responseBody;
}
