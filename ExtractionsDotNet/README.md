# ExtractionsDotNet

A .NET 10 client for OpenRouter's alpha Extractions API.

```powershell
dotnet add package ExtractionsDotNet --version 0.1.0-preview
```

## Discover models

```csharp
using ExtractionsDotNet;

using var http = new HttpClient();
foreach (var model in await OpenRouterExtractionsClient.ListModelsAsync(http))
{
    Console.WriteLine($"{model.Id} ({string.Join(", ", model.InputModalities)})");
}
```

## Extract fields from text

Set `OPENROUTER_API_KEY` and select a model that supports the fields you need.

```csharp
using ExtractionsDotNet;

using var client = OpenRouterExtractionsClient.FromEnvironment();
var invoice = await client.ExtractAsync(new ExtractionRequest(
    "cloudflare/clairic",
    new Dictionary<string, ExtractionField>
    {
        ["invoice_number"] = new TextField(),
        ["total"] = new NumberField { Decimals = 2, Unit = "USD" },
        ["due_date"] = new DateField { Instructions = "The due date stated on the invoice" }
    })
{
    State = "Invoice INV-2291 is due 2026-10-02. Total $1080."
});

var total = (ExtractionAnswer<double?>)invoice.Answers["total"];
Console.WriteLine($"Total {total.Value}, confidence {total.Confidence}");
```

Scalar answers use `ExtractionAnswer<T>` with nullable values. The type argument is `string?`, `double?`, `DateOnly?`, `TimeOnly?`, or `ExtractionDateTime?` depending on the field. `ExtractionDateTime` preserves the wall-clock time and any offset returned by the model. Duration answers also expose the returned unit. Lists use `ExtractionListAnswer<T>` with the requested item type. Estimates and forecasts use `ExtractionEstimateAnswer<T>` and `ExtractionForecastAnswer<T>` with `double` or `DateOnly` according to the bounds.

When a model cannot determine a value, `Value` can be null and `Guess` can still be present. Spans use zero-based Unicode code point offsets with an exclusive end. Use `string.EnumerateRunes()` to index the returned `StateText` or original state by code point. A list span can be null at an individual item position.

## Extract from images and PDFs

Reuse the client with a model that accepts the asset format. These examples send only the asset. When you supply accompanying state text, use the asset name in that text or in a field's instructions as required by OpenRouter. [Asset rules](https://openrouter.ai/docs/guides/community/extractions#assets)

```csharp
var fields = new Dictionary<string, ExtractionField>
{
    ["invoice_number"] = new TextField()
};

var imageResult = await client.ExtractAsync(new ExtractionRequest("cloudflare/clairic", fields)
{
    Assets = new Dictionary<string, ExtractionAsset>
    {
        ["invoice-photo"] = new(ExtractionAssetFormat.Jpeg, await File.ReadAllBytesAsync("invoice.jpg"))
    }
});

var pdfResult = await client.ExtractAsync(new ExtractionRequest("scaledown/extract", fields)
{
    Assets = new Dictionary<string, ExtractionAsset>
    {
        ["invoice-pdf"] = new(ExtractionAssetFormat.Pdf, await File.ReadAllBytesAsync("invoice.pdf"))
    }
});
```

Pass a cancellation token to either async method. `ExtractionsApiException` exposes the HTTP status and response body. You can supply your own `HttpClient` to configure timeouts and retry policies. You retain ownership of that client.

[OpenRouter Extractions guide](https://openrouter.ai/docs/guides/community/extractions) · [REST reference](https://openrouter.ai/docs/api/api-reference/alphaextractions/submit-an-extractions-request)
