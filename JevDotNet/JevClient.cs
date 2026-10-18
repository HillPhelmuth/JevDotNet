using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DecisionsDotNet;

public sealed class JevClientOptions
{
    /// <summary>Absolute HTTP or HTTPS service root. Endpoint paths such as v1/systemone are appended to this URI.</summary>
    public Uri BaseUri { get; init; } = new("https://api.typesafe.ai/");
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Input modalities keyed by exact model ID. Overrides take precedence over known Jev capabilities.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ModelInputModalitiesOverrides { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
}

public sealed class JevApiException : Exception
{
    public JevApiException(HttpStatusCode statusCode, string responseBody, string service = "TypeSafe")
        : base($"{service} API returned HTTP {(int)statusCode} ({statusCode}).")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        Service = service;
    }

    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }
    public string Service { get; }
}

/// <summary>HTTP client for TypeSafe AI's System One and model-list endpoints.</summary>
public sealed class JevClient : IDecisionsClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly JevClientOptions _options;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _modalityOverrides;

    public JevClient(string apiKey, HttpClient? httpClient = null, JevClientOptions? options = null)
    {
        _apiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : throw new ArgumentException("An API key is required.", nameof(apiKey));
        _options = options ?? new JevClientOptions();
        _modalityOverrides = ModelInputModalities.Snapshot(_options.ModelInputModalitiesOverrides);
        if (_options.BaseUri is null || !_options.BaseUri.IsAbsoluteUri ||
            (_options.BaseUri.Scheme != Uri.UriSchemeHttps && _options.BaseUri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("BaseUri must be an absolute HTTP or HTTPS URI.", nameof(options));
        if (_options.MaxAttempts < 1 || _options.InitialRetryDelay < TimeSpan.Zero ||
            _options.MaxRetryDelay < _options.InitialRetryDelay)
            throw new ArgumentException("Invalid retry settings.", nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public static JevClient FromEnvironment(HttpClient? httpClient = null, JevClientOptions? options = null)
        => new(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")
            ?? throw new InvalidOperationException("TYPESAFE_API_KEY is not set."), httpClient, options);

    public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (state, hasImages) = ImageInput.ValidateRequest(request);
        var modalities = _modalityOverrides.TryGetValue(request.Model, out var configured)
            ? configured
            : request.Model is "jev-1.13.0" or "jev-1.14.0" or "jev-latest" or "jev-preview"
                ? new[] { "text", "image" } : null;
        string body;
        if (hasImages)
        {
            ImageInput.RequireImageSupport(modalities, request.Model);
            var assets = new Dictionary<string, object>(StringComparer.Ordinal);
            var wireParts = new List<object>();
            var original = JsonSerializer.Serialize(request, JsonOptions);
            foreach (var part in state.EnumerateArray())
            {
                if (!ImageInput.IsImage(part)) { wireParts.Add(part); continue; }
                var (mediaType, base64) = ImageInput.ReadImage(part);
                if (mediaType == "image/webp")
                    throw new NotSupportedException("Direct TypeSafe image requests support PNG and JPEG only. Convert WebP before sending.");
                string reference;
                do { reference = Guid.NewGuid().ToString(); }
                while (original.Contains(reference, StringComparison.Ordinal) || assets.ContainsKey(reference));
                assets.Add(reference, new { type = "image", format = mediaType == "image/png" ? "png" : "jpg", data_base64 = base64 });
                wireParts.Add(reference);
            }
            body = JsonSerializer.Serialize(new { state = wireParts, model = request.Model, questions = request.Questions, assets }, JsonOptions);
        }
        else
        {
            object wireState = modalities is null || ImageInput.IsMultimodal(modalities)
                ? state : ImageInput.AsJsonText(state);
            body = JsonSerializer.Serialize(new { state = wireState, model = request.Model, questions = request.Questions }, JsonOptions);
        }
        var enableImages = hasImages || request.Questions.Values.Any(question => question is BoxQuestion);
        var response = await SendAsync(HttpMethod.Post, "v1/systemone", body, cancellationToken, enableImages).ConfigureAwait(false);
        return JevResponseValidator.DeserializeAndValidate(response, request.Questions);
    }

    public async Task<JevModelList> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var result = Deserialize<JevModelList>(
            await SendAsync(HttpMethod.Get, "v1/models", null, cancellationToken).ConfigureAwait(false));
        if (result.Models is null || result.Models.Any(model => model is null ||
            string.IsNullOrWhiteSpace(model.Name) || model.Description is null || model.ReleaseDate is null))
            throw new JsonException("TypeSafe returned an incomplete model list.");
        return result;
    }

    private async Task<string> SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken,
        bool enableImages = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(method, new Uri(_options.BaseUri, path));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (enableImages) message.Headers.Add("TypeSafe-Danger-Zone-Images", "true");
            if (body is not null)
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return content;

            if (attempt >= _options.MaxAttempts || (int)response.StatusCode is not (429 or 529))
                throw new JevApiException(response.StatusCode, content);

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

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, JsonOptions)
           ?? throw new JsonException("TypeSafe returned an empty JSON response.");

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
