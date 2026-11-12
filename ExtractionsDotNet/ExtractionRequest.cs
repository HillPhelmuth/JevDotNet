using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExtractionsDotNet;

public sealed record ExtractionRequest(string Model, IReadOnlyDictionary<string, ExtractionField> Fields)
{
    public string State { get; init; } = "";
    public IReadOnlyDictionary<string, ExtractionAsset>? Assets { get; init; }
    public DateOnly? ReferenceDate { get; init; }
    public ExtractionProviderOptions? Provider { get; init; }
    public string? User { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyDictionary<string, object?>? Trace { get; init; }
}

public enum ExtractionAssetFormat { Png, Jpg, Jpeg, Webp, Pdf }

public sealed class ExtractionAsset
{
    public ExtractionAsset(ExtractionAssetFormat format, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (data.Length == 0)
            throw new ArgumentException("An extraction asset must contain data.", nameof(data));

        Format = format;
        DataBase64 = Convert.ToBase64String(data);
    }

    public ExtractionAssetFormat Format { get; }
    public string Type => Format == ExtractionAssetFormat.Pdf ? "document" : "image";
    public string DataBase64 { get; }
}

public sealed record ExtractionProviderOptions
{
    public IReadOnlyList<string>? Order { get; init; }
    public IReadOnlyList<string>? Only { get; init; }
    public IReadOnlyList<string>? Ignore { get; init; }
    public bool? AllowFallbacks { get; init; }
    public IReadOnlyDictionary<string, object?>? Options { get; init; }

    /// <summary>Additional OpenRouter provider routing properties, keyed by their JSON names.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
