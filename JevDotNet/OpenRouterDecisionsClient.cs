using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JevDotNet;

public sealed class OpenRouterDecisionsClientOptions
{
    public Uri BaseUri { get; init; } = new("https://openrouter.ai/");
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>HTTP client for OpenRouter's Decisions API.</summary>
public sealed class OpenRouterDecisionsClient : IDecisionsClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly OpenRouterDecisionsClientOptions _options;

    public OpenRouterDecisionsClient(
        string apiKey,
        HttpClient? httpClient = null,
        OpenRouterDecisionsClientOptions? options = null)
    {
        _apiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey : throw new ArgumentException("An OpenRouter API key is required.", nameof(apiKey));
        _options = options ?? new OpenRouterDecisionsClientOptions();
        if (_options.BaseUri is null || !_options.BaseUri.IsAbsoluteUri || _options.BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("BaseUri must be an absolute HTTPS URI.", nameof(options));
        if (_options.MaxAttempts < 1 || _options.InitialRetryDelay < TimeSpan.Zero ||
            _options.MaxRetryDelay < _options.InitialRetryDelay)
            throw new ArgumentException("Invalid retry settings.", nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public static OpenRouterDecisionsClient FromEnvironment(
        HttpClient? httpClient = null,
        OpenRouterDecisionsClientOptions? options = null)
        => new(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
            ?? throw new InvalidOperationException("OPENROUTER_API_KEY is not set."), httpClient, options);

    public async Task<JevResponse> EvaluateAsync(
        DecisionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = JsonSerializer.Serialize(request, JsonOptions);

        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_options.BaseUri, "api/alpha/decisions"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return JevResponseValidator.DeserializeAndValidate(content, request.Questions);

            if (attempt >= _options.MaxAttempts || (int)response.StatusCode is not (429 or 502 or 503 or 529))
                throw new JevApiException(response.StatusCode, content, "OpenRouter");

            var backoff = TimeSpan.FromMilliseconds(Math.Min(
                _options.MaxRetryDelay.TotalMilliseconds,
                _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ??
                (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : backoff);
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > _options.MaxRetryDelay) delay = _options.MaxRetryDelay;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
