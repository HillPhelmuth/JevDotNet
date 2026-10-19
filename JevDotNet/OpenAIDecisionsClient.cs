using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DecisionsDotNet;

public sealed class OpenAIDecisionsClientOptions
{
    /// <summary>Absolute HTTPS service root. Evaluations go to v1/decisions under it.</summary>
    public Uri BaseUri { get; init; } = new("https://api.openai.com/");
    /// <summary>Sent as OpenAI-Organization when set.</summary>
    public string? Organization { get; init; }
    /// <summary>Sent as OpenAI-Project when set.</summary>
    public string? Project { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// HTTP client for OpenAI's Decisions API (<c>POST /v1/decisions</c>). It accepts the same <see cref="DecisionsRequest"/>
/// as the other providers and maps Noul questions to predicates, Choice to choices, and Score to levels.
/// </summary>
public sealed class OpenAIDecisionsClient : IDecisionsClient, IDisposable
{
    /// <summary>The model OpenAI's Decisions API supports. Pass it as the request model.</summary>
    public const string DefaultModel = "gpt-6-luna";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<int> RetryableStatusCodes = new HashSet<int> { 429, 500, 502, 503, 504 };
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly OpenAIDecisionsClientOptions _options;
    private readonly RetryPolicy _retry;

    public OpenAIDecisionsClient(string apiKey, HttpClient? httpClient = null, OpenAIDecisionsClientOptions? options = null)
    {
        _apiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey : throw new ArgumentException("An OpenAI API key is required.", nameof(apiKey));
        _options = options ?? new OpenAIDecisionsClientOptions();
        if (_options.BaseUri is null || !_options.BaseUri.IsAbsoluteUri || _options.BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("BaseUri must be an absolute HTTPS URI.", nameof(options));
        _retry = new RetryPolicy(_options.MaxAttempts, _options.InitialRetryDelay, _options.MaxRetryDelay, RetryableStatusCodes);
        _retry.Validate(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public static OpenAIDecisionsClient FromEnvironment(HttpClient? httpClient = null, OpenAIDecisionsClientOptions? options = null)
        => new(Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY is not set."), httpClient, options);

    public async Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (state, hasImages) = ImageInput.ValidateRequest(request);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = BuildInput(state, hasImages),
            ["questions"] = new JsonArray(request.Questions.Select(pair => (JsonNode)BuildQuestion(pair.Key, pair.Value)).ToArray())
        }.ToJsonString(JsonOptions);

        var content = await DecisionsHttp.SendAsync(_httpClient, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, "v1/decisions"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (!string.IsNullOrWhiteSpace(_options.Organization)) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (!string.IsNullOrWhiteSpace(_options.Project)) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return message;
        }, _retry, "OpenAI", cancellationToken).ConfigureAwait(false);
        return ReadResponse(content, request.Questions);
    }

    private static JsonNode BuildInput(JsonElement state, bool hasImages)
    {
        if (!hasImages)
            return JsonValue.Create(ImageInput.AsJsonText(state).ToString())!;

        // Images require a user message whose parts keep the state array's order.
        var parts = new JsonArray();
        foreach (var part in state.EnumerateArray())
        {
            if (ImageInput.IsImage(part))
            {
                ImageInput.ReadImage(part);
                var image = part.GetProperty("image_url");
                var node = new JsonObject { ["type"] = "input_image", ["image_url"] = image.GetProperty("url").GetString() };
                if (image.TryGetProperty("detail", out var detail)) node["detail"] = detail.GetString();
                parts.Add(node);
            }
            else parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = ImageInput.AsJsonText(part).ToString() });
        }
        return new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = parts });
    }

    private static JsonObject BuildQuestion(string name, DecisionsQuestion question)
    {
        var instructions = Text(question.Instructions);
        switch (question)
        {
            case NoulQuestion noul:
                // Predicates take instructions only, so Noul criteria become part of them.
                if (noul.Criteria?.True is { } whenTrue) instructions += $"\nTrue when: {Text(whenTrue)}";
                if (noul.Criteria?.False is { } whenFalse) instructions += $"\nFalse when: {Text(whenFalse)}";
                return new JsonObject { ["type"] = "predicate", ["name"] = name, ["instructions"] = instructions };
            case ChoiceQuestion choice:
                return new JsonObject
                {
                    ["type"] = "choice", ["name"] = name, ["instructions"] = instructions,
                    ["choices"] = new JsonArray(choice.Criteria.Select(option => (JsonNode)new JsonObject
                    {
                        ["value"] = option.Key,
                        ["description"] = option.Value is null ? option.Key : Text(option.Value)
                    }).ToArray())
                };
            case ScoreQuestion score:
                return new JsonObject
                {
                    ["type"] = "score", ["name"] = name, ["instructions"] = instructions,
                    ["levels"] = new JsonArray(score.Criteria.Select((level, index) =>
                    {
                        var description = Text(level);
                        return (JsonNode)new JsonObject
                        {
                            ["label"] = description.Length <= 80 ? description : $"Level {index}",
                            ["description"] = description
                        };
                    }).ToArray())
                };
            case BoxQuestion:
                throw new NotSupportedException("OpenAI's Decisions API does not support Box questions.");
            default:
                throw new NotSupportedException($"{question.GetType().Name} is not supported by OpenAI's Decisions API.");
        }
    }

    private static DecisionsResponse ReadResponse(string json, IReadOnlyDictionary<string, DecisionsQuestion> questions)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Array || !root.TryGetProperty("model", out var model) ||
            model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString()))
            throw new JsonException("The OpenAI Decisions API returned an incomplete evaluation response.");

        var mapped = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
        foreach (var answer in answers.EnumerateArray())
        {
            var name = answer.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()! : throw new JsonException("The OpenAI Decisions API returned an answer without a name.");
            if (!questions.TryGetValue(name, out var question) || mapped.ContainsKey(name))
                throw new JsonException($"The OpenAI Decisions API returned an unexpected answer '{name}'.");
            mapped[name] = ReadAnswer(answer, question, name);
        }
        if (mapped.Count != questions.Count)
            throw new JsonException("The OpenAI Decisions API did not answer every question.");

        var usage = root.TryGetProperty("usage", out var usageValue) && usageValue.ValueKind == JsonValueKind.Object
            ? usageValue : throw new JsonException("The OpenAI Decisions API returned no usage.");
        return new DecisionsResponse
        {
            Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            Provider = "OpenAI",
            Model = model.GetString()!,
            Answers = mapped,
            Usage = new DecisionsUsage
            {
                InputTokens = usage.GetProperty("input_tokens").GetInt32(),
                OutputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : 0
            }
        };
    }

    private static JevAnswer ReadAnswer(JsonElement answer, DecisionsQuestion question, string name)
    {
        var type = answer.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        switch (type, question)
        {
            case ("refusal", _):
                return new RefusalAnswer();
            case ("predicate", NoulQuestion):
                return new NoulAnswer { Noul = UnitInterval(answer, "probability", name) };
            case ("choice", ChoiceQuestion):
                return new ChoiceAnswer
                {
                    Choice = ChoiceValue(answer.GetProperty("choice")),
                    Probabilities = answer.GetProperty("probabilities").EnumerateArray().ToDictionary(
                        entry => ChoiceValue(entry.GetProperty("value")), entry => UnitInterval(entry, "probability", name)),
                    Confidence = UnitInterval(answer, "confidence", name)
                };
            case ("score", ScoreQuestion score):
                var levels = answer.GetProperty("probabilities").EnumerateArray().ToArray();
                return new ScoreAnswer
                {
                    Score = answer.GetProperty("score").GetDouble(),
                    Legend = Enumerable.Range(0, score.Criteria.Count).ToDictionary(
                        index => index.ToString(CultureInfo.InvariantCulture), index => Text(score.Criteria[index])),
                    Probabilities = levels.ToDictionary(
                        entry => entry.GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture),
                        entry => UnitInterval(entry, "probability", name)),
                    Confidence = UnitInterval(answer, "confidence", name)
                };
            default:
                throw new JsonException($"The OpenAI Decisions API returned a missing or mismatched answer for '{name}'.");
        }
    }

    private static string ChoiceValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new JsonException("The OpenAI Decisions API returned an invalid choice value.")
    };

    private static double UnitInterval(JsonElement element, string property, string name)
        => element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) &&
           double.IsFinite(number) && number is >= 0 and <= 1
            ? number : throw new JsonException($"The OpenAI Decisions API returned an invalid {property} for '{name}'.");

    private static string Text(DecisionsValue value) => ImageInput.AsJsonText(value.ToJsonElement()).ToString()!;

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
