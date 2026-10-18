using System.Text.Json;

namespace DecisionsDotNet;

internal static class ImageInput
{
    internal static void ValidateMediaType(string mediaType)
    {
        if (mediaType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new ArgumentException("Image media type must be image/png, image/jpeg, or image/webp.", nameof(mediaType));
    }

    internal static (string MediaType, string Base64) ParseDataUrl(string dataUrl)
    {
        ArgumentNullException.ThrowIfNull(dataUrl);
        var separator = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
        if (!dataUrl.StartsWith("data:", StringComparison.Ordinal) || separator < 5)
            throw new ArgumentException("Images require a base64 data URL; remote URLs are not supported.", nameof(dataUrl));
        var mediaType = dataUrl[5..separator];
        ValidateMediaType(mediaType);
        var base64 = dataUrl[(separator + 8)..];
        if (base64.Length == 0 || base64.Any(char.IsWhiteSpace) || base64.Length % 4 != 0)
            throw new ArgumentException("Image data must be nonempty standard base64 without whitespace.", nameof(dataUrl));
        // Validate without allocating a decoded copy of the image.
        if (!System.Buffers.Text.Base64.IsValid(System.Text.Encoding.UTF8.GetBytes(base64)))
            throw new ArgumentException("Image data contains malformed base64.", nameof(dataUrl));
        return (mediaType, base64);
    }

    internal static bool IsImage(JsonElement value)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var type) &&
           type.ValueKind == JsonValueKind.String && type.GetString() == "image_url";

    internal static (string MediaType, string Base64) ReadImage(JsonElement value)
    {
        if (!value.TryGetProperty("image_url", out var image) || image.ValueKind != JsonValueKind.Object ||
            !image.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
            throw new ArgumentException("An image part requires image_url.url containing a base64 data URL.");
        if (image.TryGetProperty("detail", out var detail) &&
            (detail.ValueKind != JsonValueKind.String || detail.GetString() is not ("auto" or "low" or "high")))
            throw new ArgumentException("Image detail must be auto, low, or high.");
        return ParseDataUrl(url.GetString()!);
    }

    internal static bool ContainsImage(JsonElement value)
        => IsImage(value) || (value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsImage(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().Any(ContainsImage),
            _ => false
        });

    internal static (JsonElement State, bool HasImages) ValidateRequest(DecisionsRequest request)
    {
        var state = request.State.ToJsonElement();
        if (IsImage(state)) state = JsonSerializer.SerializeToElement(new[] { state });
        var hasImages = false;
        if (state.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in state.EnumerateArray())
            {
                if (IsImage(part))
                {
                    ReadImage(part);
                    hasImages = true;
                    // An image's optional fields must not hide additional image parts either.
                    foreach (var property in part.EnumerateObject()) RejectNested(property.Value);
                }
                else RejectNested(part);
            }
        }
        else RejectNested(state);

        foreach (var question in request.Questions.Values)
        {
            if (ContainsImage(JsonSerializer.SerializeToElement<DecisionsQuestion>(question)))
                throw new NotSupportedException("Images in question instructions or criteria are not supported; put images in the top-level state array.");
        }
        return (state, hasImages);
    }

    private static void RejectNested(JsonElement value)
    {
        if (ContainsImage(value))
            throw new NotSupportedException("Nested images are not supported; put image parts directly in the top-level state array.");
    }

    internal static bool IsMultimodal(IReadOnlyList<string>? modalities)
        => modalities is not null && modalities.Any(modality => modality != "text");

    internal static void RequireImageSupport(IReadOnlyList<string>? modalities, string model)
    {
        if (modalities is null || !modalities.Contains("image", StringComparer.Ordinal))
            throw new NotSupportedException($"Image input is not supported or could not be confirmed for model '{model}'. Configure ModelInputModalitiesOverrides for a custom model.");
    }

    internal static object AsJsonText(JsonElement state)
        => state.ValueKind == JsonValueKind.String ? state.GetString()! : JsonSerializer.Serialize(state);
}
