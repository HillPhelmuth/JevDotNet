using System.Text.Json.Serialization;

namespace ExtractionsDotNet;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextField), "text")]
[JsonDerivedType(typeof(NumberField), "number")]
[JsonDerivedType(typeof(DateField), "date")]
[JsonDerivedType(typeof(TimeField), "time")]
[JsonDerivedType(typeof(DateTimeField), "datetime")]
[JsonDerivedType(typeof(DurationField), "duration")]
[JsonDerivedType(typeof(ListField), "list")]
[JsonDerivedType(typeof(EstimateField), "estimate")]
[JsonDerivedType(typeof(ForecastField), "forecast")]
public abstract record ExtractionField
{
    public string? Instructions { get; init; }
    public double? NullThreshold { get; init; }
}

public sealed record TextField : ExtractionField
{
    public string? Unit { get; init; }
}

public sealed record NumberField : ExtractionField
{
    public string? Unit { get; init; }
    public bool? Integer { get; init; }
    public int? Decimals { get; init; }
}

public sealed record DateField : ExtractionField
{
    public string? Unit { get; init; }
}

public sealed record TimeField : ExtractionField
{
    public string? Unit { get; init; }
}

public sealed record DateTimeField : ExtractionField
{
    public string? Unit { get; init; }
}

public enum DurationUnit { Seconds, Minutes, Hours, Days, Weeks, Months, Years }

public sealed record DurationField : ExtractionField
{
    public DurationUnit? Unit { get; init; }
    public bool? Integer { get; init; }
    public int? Decimals { get; init; }
}

public enum ListItemType { Text, Number, Date }

public sealed record ListField(ListItemType Items) : ExtractionField
{
    public string? Unit { get; init; }
    public bool? Integer { get; init; }
    public int? Decimals { get; init; }
}

public enum ExtractionScale { Linear, Log }
public enum CalendarStep { Month, Year }
public enum ForecastInterval { Step, Minute, Hour, Day, Week, Month, Quarter, Year }

public abstract record RangeField : ExtractionField
{
    private protected RangeField(object min, object max)
    {
        Min = min;
        Max = max;
    }

    // The concrete constructors constrain both bounds to the same supported type.
    public object Min { get; }
    public object Max { get; }
    public string? Unit { get; init; }
    public ExtractionScale? Scale { get; init; }

    [JsonIgnore]
    public double? Step { get; init; }

    [JsonIgnore]
    public CalendarStep? CalendarStep { get; init; }

    [JsonInclude, JsonPropertyName("step")]
    internal object? WireStep => CalendarStep is { } calendarStep ? calendarStep : Step;
}

public sealed record EstimateField : RangeField
{
    public EstimateField(double min, double max) : base(min, max) { }
    public EstimateField(DateOnly min, DateOnly max) : base(min, max) { }
}

public sealed record ForecastField : RangeField
{
    public ForecastField(double min, double max, int horizon, ForecastInterval interval) : base(min, max)
    {
        Horizon = horizon;
        Interval = interval;
    }

    public ForecastField(DateOnly min, DateOnly max, int horizon, ForecastInterval interval) : base(min, max)
    {
        Horizon = horizon;
        Interval = interval;
    }

    public int Horizon { get; }
    public ForecastInterval Interval { get; }
}
