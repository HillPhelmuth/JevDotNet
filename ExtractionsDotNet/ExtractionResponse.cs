using System.Text.Json.Serialization;

namespace ExtractionsDotNet;

public sealed record ExtractionResponse(
    string Model,
    IReadOnlyDictionary<string, ExtractionAnswer> Answers,
    ExtractionUsage Usage)
{
    public string? Id { get; init; }
    public string? Provider { get; init; }
    public string? StateText { get; init; }
    public bool? Truncated { get; init; }
}

public sealed record ExtractionUsage
{
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public decimal? Cost { get; init; }
    public int? StateTokens { get; init; }
    public int? ImageTokens { get; init; }
    public int? SchemaTokens { get; init; }
    public int? Passes { get; init; }
}

public abstract record ExtractionAnswer
{
    public required string Type { get; init; }
    [JsonPropertyName("p_null")]
    public double? NullProbability { get; init; }
    public double? Confidence { get; init; }
    public string? Source { get; init; }
    public double? SpellConfidence { get; init; }
    public ExtractionSpan? Span { get; init; }
}

public record ExtractionAnswer<T> : ExtractionAnswer
{
    public required T Value { get; init; }
    public T? Guess { get; init; }
}

/// <summary>Zero-based, end-exclusive Unicode code point offsets into the state text.</summary>
public readonly record struct ExtractionSpan(int Start, int End);

/// <summary>A wall-clock date and time with an offset only when the answer includes one.</summary>
public readonly record struct ExtractionDateTime(DateTime LocalDateTime, TimeSpan? Offset);

public sealed record ExtractionDurationAnswer : ExtractionAnswer<double?>
{
    public string? Unit { get; init; }
}

public sealed record ExtractionListAnswer<T> : ExtractionAnswer<IReadOnlyList<T>?>
{
    public IReadOnlyList<ExtractionSpan?>? Spans { get; init; }
    public IReadOnlyList<double>? Confidences { get; init; }
}

public sealed record ExtractionEstimateAnswer<T> : ExtractionAnswer<T?> where T : struct
{
    public T? Mean { get; init; }
    public T? Median { get; init; }
    public T? P10 { get; init; }
    public T? P90 { get; init; }
    public double? PBelow { get; init; }
    public double? PAbove { get; init; }
    public IReadOnlyList<T>? Quantiles { get; init; }
}

public sealed record ExtractionForecastAnswer<T> : ExtractionAnswer<IReadOnlyList<T>?> where T : struct
{
    public IReadOnlyList<T>? Mean { get; init; }
    public IReadOnlyList<T>? Median { get; init; }
    public IReadOnlyList<T>? P10 { get; init; }
    public IReadOnlyList<T>? P90 { get; init; }
    public IReadOnlyList<ExtractionForecastStep<T>>? Steps { get; init; }
}

public sealed record ExtractionForecastStep<T> where T : struct
{
    public required T? Value { get; init; }
    public T? Mean { get; init; }
    public T? Median { get; init; }
    public T? P10 { get; init; }
    public T? P90 { get; init; }
    public double? PBelow { get; init; }
    public double? PAbove { get; init; }
    public IReadOnlyList<T>? Quantiles { get; init; }
}

public sealed record ExtractionModel(string Id, IReadOnlyList<string> InputModalities);
