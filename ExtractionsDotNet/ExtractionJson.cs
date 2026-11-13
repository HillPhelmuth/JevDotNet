using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExtractionsDotNet;

internal static class ExtractionJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
            new SpanConverter(),
            new DateTimeConverter()
        }
    };

    internal static ExtractionResponse ReadResponse(string json, IReadOnlyDictionary<string, ExtractionField> fields)
    {
        var response = JsonSerializer.Deserialize<ResponseEnvelope>(json, Options)
            ?? throw new JsonException("Expected an extraction response object.");
        if (string.IsNullOrWhiteSpace(response.Model) || response.Answers.Count != fields.Count)
            throw new JsonException("Expected a model ID and one answer per requested field.");

        var answers = new Dictionary<string, ExtractionAnswer>(fields.Count, StringComparer.Ordinal);
        foreach (var (name, field) in fields)
        {
            if (!response.Answers.TryGetValue(name, out var element))
                throw new JsonException($"The extraction response is missing the answer for '{name}'.");

            answers.Add(name, ReadAnswer(element, field));
        }

        return new ExtractionResponse(response.Model, answers, response.Usage)
        {
            Id = response.Id,
            Provider = response.Provider,
            StateText = response.StateText,
            Truncated = response.Truncated
        };
    }

    private static ExtractionAnswer ReadAnswer(JsonElement json, ExtractionField field) => field switch
    {
        TextField => Read<ExtractionAnswer<string?>>(json, "text"),
        NumberField => Read<ExtractionAnswer<double?>>(json, "number"),
        DateField => Read<ExtractionAnswer<DateOnly?>>(json, "date"),
        TimeField => Read<ExtractionAnswer<TimeOnly?>>(json, "time"),
        DateTimeField => Read<ExtractionAnswer<ExtractionDateTime?>>(json, "datetime"),
        DurationField => Read<ExtractionDurationAnswer>(json, "duration"),
        ListField { Items: ListItemType.Text } => Read<ExtractionListAnswer<string>>(json, "list"),
        ListField { Items: ListItemType.Number } => Read<ExtractionListAnswer<double>>(json, "list"),
        ListField { Items: ListItemType.Date } => Read<ExtractionListAnswer<DateOnly>>(json, "list"),
        EstimateField { Min: DateOnly } => Read<ExtractionEstimateAnswer<DateOnly>>(json, "estimate"),
        EstimateField => Read<ExtractionEstimateAnswer<double>>(json, "estimate"),
        ForecastField { Min: DateOnly } => Read<ExtractionForecastAnswer<DateOnly>>(json, "forecast"),
        ForecastField => Read<ExtractionForecastAnswer<double>>(json, "forecast"),
        _ => throw new JsonException("Unsupported extraction field.")
    };

    private static T Read<T>(JsonElement json, string expectedType) where T : ExtractionAnswer
    {
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !type.ValueEquals(expectedType))
        {
            throw new JsonException($"Expected an extraction answer of type '{expectedType}'.");
        }

        return json.Deserialize<T>(Options)!;
    }

    internal static IReadOnlyList<ExtractionModel> ReadModels(string json)
    {
        var catalog = JsonSerializer.Deserialize<ModelCatalog>(json, Options)
            ?? throw new JsonException("Expected an extraction model catalog.");
        var models = new List<ExtractionModel>(catalog.Data.Count);
        foreach (var entry in catalog.Data)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id))
                throw new JsonException("An extraction model is missing its ID.");

            models.Add(new ExtractionModel(entry.Id, entry.Architecture?.InputModalities ?? []));
        }
        return models;
    }

    private sealed record ResponseEnvelope
    {
        public required string Model { get; init; }
        public required Dictionary<string, JsonElement> Answers { get; init; }
        public required ExtractionUsage Usage { get; init; }
        public string? Id { get; init; }
        public string? Provider { get; init; }
        public string? StateText { get; init; }
        public bool? Truncated { get; init; }
    }

    private sealed record ModelCatalog
    {
        public required List<ModelEntry> Data { get; init; }
    }

    private sealed record ModelEntry(string Id, ModelArchitecture? Architecture);
    private sealed record ModelArchitecture(IReadOnlyList<string>? InputModalities);

    private sealed class SpanConverter : JsonConverter<ExtractionSpan>
    {
        public override ExtractionSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (JsonSerializer.Deserialize<int[]>(ref reader, options) is not [var start, var end]
                || start < 0 || end < start)
            {
                throw new JsonException("Expected a span with two ordered Unicode code point offsets.");
            }

            return new ExtractionSpan(start, end);
        }

        public override void Write(Utf8JsonWriter writer, ExtractionSpan value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.Start);
            writer.WriteNumberValue(value.End);
            writer.WriteEndArray();
        }
    }

    private sealed class DateTimeConverter : JsonConverter<ExtractionDateTime>
    {
        public override ExtractionDateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 10 } text)
            {
                bool hasOffset = text.EndsWith('Z') || text.AsSpan(10).IndexOfAny('+', '-') >= 0;
                if (hasOffset && reader.TryGetDateTimeOffset(out var offsetDateTime))
                    return new ExtractionDateTime(offsetDateTime.DateTime, offsetDateTime.Offset);
                if (!hasOffset && reader.TryGetDateTime(out var localDateTime))
                    return new ExtractionDateTime(localDateTime, null);
            }

            throw new JsonException("Expected an ISO date and time with an optional offset.");
        }

        public override void Write(Utf8JsonWriter writer, ExtractionDateTime value, JsonSerializerOptions options)
        {
            var localDateTime = DateTime.SpecifyKind(value.LocalDateTime, DateTimeKind.Unspecified);
            if (value.Offset is { } offset)
                writer.WriteStringValue(new DateTimeOffset(localDateTime, offset));
            else
                writer.WriteStringValue(localDateTime);
        }
    }
}
