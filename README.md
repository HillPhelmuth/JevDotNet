# DecisionsDotNet

A type-safe C# client for TypeSafe AI's Jev models, through either TypeSafe's direct API or OpenRouter's Decisions API. Targets .NET 10 and uses `System.Text.Json`, `HttpClient`, and Microsoft.Extensions.Http for dependency injection.

```powershell
dotnet add package DecisionsDotNet --prerelease
```

The package ID and namespace are `DecisionsDotNet`. The repository and project folders keep their original `JevDotNet` names.

## Direct TypeSafe quick start

Set `TYPESAFE_API_KEY` in your environment, then:

```csharp
using DecisionsDotNet;

using var client = JevClient.FromEnvironment();

var request = new DecisionsRequest(
    DecisionsValue.FromObject(new { message = "My card was charged twice." }),
    new Dictionary<string, DecisionsQuestion>
    {
        ["urgent"] = new NoulQuestion("Does this need immediate attention?"),
        ["team"] = new ChoiceQuestion("Which team should handle this?",
            new Dictionary<string, DecisionsValue?>
            {
                ["billing"] = "Payments and refunds",
                ["technical"] = "Bugs and outages"
            }),
        ["frustration"] = new ScoreQuestion("How frustrated is the customer?",
            new DecisionsValue[] { "Calm", "Frustrated", "Very angry" })
    });

var response = await client.EvaluateAsync(request);
double urgency = response.GetAnswer<NoulAnswer>("urgent").Noul;
string team = response.GetAnswer<ChoiceAnswer>("team").Choice;
double frustration = response.GetAnswer<ScoreAnswer>("frustration").Score;
Console.WriteLine($"{response.Model}: {team}, urgency {urgency}, frustration {frustration}");
```

### Fluent requests

`DecisionsRequest.Create` builds the same request one question at a time. Each method has an overload with an `out` key that remembers the answer type, so reading answers needs no casts or repeated strings:

```csharp
var request = DecisionsRequest.Create(DecisionsValue.FromObject(new { message = "My card was charged twice." }))
    .Noul("urgent", "Does this need immediate attention?", out var urgent)
    .Choice("team", "Which team should handle this?", out var team,
        ("billing", "Payments and refunds"),
        ("technical", "Bugs and outages"))
    .Score("frustration", "How frustrated is the customer?", out var frustration,
        "Calm", "Frustrated", "Very angry")
    .Build();

var response = await client.EvaluateAsync(request);
Console.WriteLine($"{response.Get(team).Choice}, urgency {response.Get(urgent).Noul}, frustration {response.Get(frustration).Score}");
```

`Noul` takes optional `whenTrue` and `whenFalse` criteria, `Choice` takes plain option names or `(option, description)` pairs, `Score` takes levels lowest first, and `Box` takes instructions only. Duplicate question IDs throw. `Build(model)` defaults to `jev-latest`. Use `response.TryGetAnswer<T>(id, out var answer)` when an answer may be absent or of another type.

Plain text implicitly converts to `DecisionsValue`. For structured state, instructions, or criteria, use `DecisionsValue.FromObject(...)` or `DecisionsValue.FromArray(...)`. You can pass custom `JsonSerializerOptions` to those methods. Choice criteria may be `null` when an option needs no description.

`DecisionsRequest` is shared by both clients. It defaults to `jev-latest` for the direct TypeSafe API; pass a model ID as its third argument to pin a version or to use OpenRouter. Use `ListModelsAsync()` to fetch direct model aliases available to your account. The response's `Model` property contains the model that answered.

The client retries HTTP 429 and 529 by default, honoring `Retry-After` up to the configured maximum delay. Configure `JevClientOptions` to change the base URI, number of attempts, or delays. Other HTTP failures throw `JevApiException` with `StatusCode` and `ResponseBody`. Cancellation is supported on both client methods. An injected `HttpClient` remains owned by the caller.

### Local or custom endpoints

Set `JevClientOptions.BaseUri` to an absolute HTTP or HTTPS service root:

```csharp
using DecisionsDotNet;

var options = new JevClientOptions
{
    BaseUri = new Uri("http://localhost:1234/")
};

using var client = new JevClient("local-key", options: options);
```

Evaluation requests go to `http://localhost:1234/v1/systemone`, and `ListModelsAsync()` uses `http://localhost:1234/v1/models`. Supply the service root, not the full evaluation URL; include a trailing slash when the root contains a path prefix. HTTP also supports LAN addresses and container hostnames. The default remains `https://api.typesafe.ai/`.

For dependency injection, pass the same options to either registration overload:

```csharp
builder.Services.AddJevSystemOneClient("local-key", options);
// Or: builder.Services.AddJevSystemOneClient(builder.Configuration, options);
```

For a local server with default client settings, use the convenience registration:

```csharp
builder.Services.AddLocalJevSystemOneClient(new Uri("http://localhost:1234/"));
// Supply apiKey: "your-key" if the server requires authentication.
```

This registers `IDecisionsClient` and returns `IHttpClientBuilder` for HTTP pipeline configuration. It sends `local-key` as the default bearer key. Use `AddJevSystemOneClient` with options when you also need custom retry settings or model capability overrides.

`JevClient.FromEnvironment(options: options)` also accepts the override. API keys remain required and are sent as bearer authorization; use a nonempty placeholder such as `local-key` if your local server does not require authentication. The server must implement the existing Jev request and response contract.

## Verify

```powershell
dotnet run --project JevDotNet.Tests/JevDotNet.Tests.csproj
dotnet pack JevDotNet/JevDotNet.csproj
```

The contract checks use fake HTTP handlers and need no API key. The direct client follows the [TypeSafe AI API reference](https://docs.typesafe.ai/api) and [model listing reference](https://docs.typesafe.ai/models).

## OpenRouter Decisions API

Set `OPENROUTER_API_KEY` in your environment, then use the same `DecisionsRequest` and typed questions with `OpenRouterDecisionsClient`:

```csharp
using DecisionsDotNet;

using var client = OpenRouterDecisionsClient.FromEnvironment();
var request = new DecisionsRequest(
    "My card was charged twice.",
    new Dictionary<string, DecisionsQuestion>
    {
        ["refund"] = new NoulQuestion("Does this customer request a refund?")
    },
    "typesafe/jev-1.13");

var response = await client.EvaluateAsync(request);
Console.WriteLine(response.GetAnswer<NoulAnswer>("refund").Noul);
Console.WriteLine($"{response.Model}: {response.Usage.InputTokens} input tokens");
```

The OpenRouter client chooses the `state` wire format from the selected model's `architecture.input_modalities`. Models advertising a non-text input modality receive native objects and arrays; text-only models receive objects and arrays serialized as JSON text. Plain strings are always preserved exactly, and the original request is never modified. This changes the previous behavior that serialized all structured OpenRouter state to text.

On first use, the SDK fetches the public [Decisions catalog](https://openrouter.ai/api/v1/models?output_modalities=decisions). Snapshots are shared across client instances with the same base URI for 15 minutes. Concurrent lookups share one fetch. Failed refreshes retain the last successful snapshot and defer another attempt for 15 minutes. Unknown models or unavailable capabilities fall back to JSON text for structured state, and image requests fail locally with `NotSupportedException`. Caller cancellation propagates rather than triggering fallback.

To read the catalog yourself, such as to fill a model picker, call `ListModelsAsync()` on a client, or the static overload when you have no API key yet. The catalog is public, so neither sends a key:

```csharp
using var http = new HttpClient();
var models = await OpenRouterDecisionsClient.ListModelsAsync(http);
foreach (var model in models.Where(model => model.SupportsImageInput))
    Console.WriteLine($"{model.Id}: {model.Name}");
```

Each `OpenRouterDecisionsModel` carries `Id`, `CanonicalSlug`, `Name`, `Description`, `Created`, and `InputModalities`. Listing retries like evaluation does and reports HTTP failures as `JevApiException`.

OpenRouter responses can also expose `Id`, `Provider`, and `Usage.Cost`; these are nullable because direct TypeSafe responses do not include them. The OpenRouter client retries HTTP 429, 502, 503, and 529 with bounded backoff. It surfaces other errors as `JevApiException` with `Service` set to `OpenRouter`. See the [OpenRouter Decisions reference](https://openrouter.ai/docs/api/api-reference/alphadecisions/submit-a-decisions-questions-and-answers-request).

## Image inputs

Use `ImageFromBytes` with in-memory image bytes and a media type, or `ImageFromDataUrl` with an existing base64 data URL. `FromParts` combines text, structured context, and images in order:

```csharp
var photo = DecisionsValue.ImageFromBytes(await File.ReadAllBytesAsync("listing.png"), "image/png");
// Alternatively: DecisionsValue.ImageFromDataUrl("data:image/png;base64,...");
var state = DecisionsValue.FromParts("Listing title: Red square sticker", photo);
var questions = new Dictionary<string, DecisionsQuestion>
{
    ["matches_title"] = new NoulQuestion("Does the photo show the item in the title?")
};

using var router = OpenRouterDecisionsClient.FromEnvironment();
var result = await router.EvaluateAsync(new DecisionsRequest(state, questions, "cloudflare/clef-flash"));

using var direct = JevClient.FromEnvironment();
var directResult = await direct.EvaluateAsync(new DecisionsRequest(state, questions, "jev-1.14.0"));
```

A standalone image value is normalized into a one-item state array. Images created through `FromObject`, `FromArray`, or `FromJson` with the same `image_url` shape are also recognized and validated. This release supports image parts directly in the top-level state array. Nested images and images in question instructions or criteria throw `NotSupportedException`. Text items are strings, not `{ "type": "text" }` parts.

OpenRouter receives base64 `image_url` parts in `state`. Direct TypeSafe receives image references in state and a top-level `assets` map, and the SDK adds `TypeSafe-Danger-Zone-Images: true` to evaluations containing images or Box questions. PNG and JPEG work with both clients; WebP is accepted only through OpenRouter, subject to the selected provider's support. Remote image URLs, empty data, unsupported media types, and malformed base64 are rejected locally. See [OpenRouter's multimodal guide](https://openrouter.ai/docs/guides/community/multimodal-decisions) and [TypeSafe's image contract](https://docs.typesafe.ai/images).

Prepare image dimensions, orientation, and compression before calling the SDK. It does not decode, resize, rotate, or fetch images. Image counts, dimensions, and request-size limits differ by provider and are validated by the server. Provider errors retain their status code and response body in `JevApiException`. Direct TypeSafe's image feature is experimental; using image input opts the request into its required header.

### Model capability overrides

Both client options expose `ModelInputModalitiesOverrides`, keyed by exact model ID. Overrides take precedence over discovery or built-in defaults and are copied when the client is constructed. For OpenRouter, configuring a model skips discovery for that model:

```csharp
var options = new OpenRouterDecisionsClientOptions
{
    ModelInputModalitiesOverrides = new Dictionary<string, IReadOnlyList<string>>
    {
        ["custom/image-model"] = new[] { "text", "image" },
        ["custom/text-model"] = new[] { "text" }
    }
};
using var client = OpenRouterDecisionsClient.FromEnvironment(options: options);
```

Use the same property on `JevClientOptions` for direct models. The direct client recognizes image support for `jev-1.13.0`, `jev-1.14.0`, `jev-latest`, and `jev-preview`. Other IDs require an override before sending images. Direct requests without images retain native structured state for unresolved models; an explicit text-only override serializes structured state to JSON text. Overrides describe capabilities; they cannot add image support to a provider that lacks it.

### Box questions and answers

`BoxQuestion` locates one target in an image. It takes instructions without criteria and returns a `BoxAnswer` containing a `BoundingBox` and `Confidence`:

```csharp
var photo = DecisionsValue.ImageFromBytes(await File.ReadAllBytesAsync("kitchen.jpg"), "image/jpeg");
var request = new DecisionsRequest(
    DecisionsValue.FromParts("Kitchen photo", photo),
    new Dictionary<string, DecisionsQuestion>
    {
        ["mug"] = new BoxQuestion("Find the coffee mug in the photo."),
        ["visible"] = new NoulQuestion("Is a coffee mug visible in the photo?")
    },
    "jev-1.14.0");

using var client = JevClient.FromEnvironment();
var response = await client.EvaluateAsync(request);
BoxAnswer mug = response.GetAnswer<BoxAnswer>("mug");
double leftPixel = mug.Box.Left * imageWidth;
double topPixel = mug.Box.Top * imageHeight;
double rightPixel = mug.Box.Right * imageWidth;
double bottomPixel = mug.Box.Bottom * imageHeight;
```

Here `imageWidth` and `imageHeight` are the dimensions of the image you sent. Edges are fractions from zero to one measured from the top-left corner. The SDK validates that all edges and confidence are finite and within that range, with `Left < Right` and `Top < Bottom`. Missing coordinates, confidence, or a box object cause a `JsonException` when reading the API response.

Box requires an image and a model supporting Box questions: use `jev-1.14.0` or `jev-preview` directly, or `typesafe/jev-1.14` through OpenRouter with the same request structure. Image capability alone does not imply Box support; model-specific question support is validated by the provider. Box always returns a rectangle, including when the target is absent. Use a companion Noul question to assess presence before acting on the box. See the [TypeSafe Box documentation](https://docs.typesafe.ai/primitives/box).

## Batching many items

Asking the same questions about many items, such as support messages, is cheaper in a few large requests than in many small ones. `EvaluateBatchesAsync` packs items into requests of `ItemsPerRequest`, names each question `{itemId}_{questionKey}`, and yields one result per request as it completes:

```csharp
var options = new DecisionsBatchOptions<Ticket>
{
    ItemId = ticket => ticket.Id,
    State = tickets => DecisionsValue.FromObject(new { tickets = tickets.Select(t => new { id = t.Id, text = t.Text }) }),
    Questions = ticket => new Dictionary<string, DecisionsQuestion>
    {
        ["refund"] = new NoulQuestion($"Does ticket {ticket.Id} ask for money back?"),
        ["angry"] = new NoulQuestion($"Is the customer in ticket {ticket.Id} angry?")
    },
    Model = "typesafe/jev-1.14",
    ItemsPerRequest = 20
};

await foreach (var batch in client.EvaluateBatchesAsync(tickets, options, cancellationToken))
{
    if (!batch.Succeeded) { Console.WriteLine($"Request {batch.Number} failed: {batch.Error!.Message}"); continue; }
    foreach (var ticket in batch.Items)
        Console.WriteLine($"{ticket.Id}: refund {batch.GetAnswer<NoulAnswer>(ticket, "refund").Noul:P0}");
}
```

Requests run one after another. By default a failed request is reported through `Error` and later batches still run; set `ContinueOnError = false` to throw instead. Cancellation always stops the run. Item IDs must be unique. Providers limit questions per request, so choose `ItemsPerRequest` with the number of questions per item in mind.

## Dependency injection

Register one provider, then inject `IDecisionsClient` into your service. Registration accepts either a key string or `IConfiguration`; the configuration overload reads `OpenRouter:ApiKey` or `TypeSafe:ApiKey` at startup and rejects a missing key. Keep these values in server-side configuration, such as ASP.NET Core user secrets.

```csharp
using DecisionsDotNet;

builder.Services.AddOpenRouterDecisionsClient(builder.Configuration);
// Or: builder.Services.AddJevSystemOneClient(builder.Configuration);
// Both also accept a key string and optional provider-specific client options.

public sealed class DecisionService(IDecisionsClient client)
{
    public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        => client.EvaluateAsync(request, cancellationToken);
}
```

The registration returns `IHttpClientBuilder` for HTTP pipeline configuration. Set `DecisionsRequest.Model` to an OpenRouter Decisions model ID, such as `"typesafe/jev-1.13"` or `"jaredpalmer/kev-4b"`; the request default `jev-latest` is for the direct TypeSafe API. Both concrete clients expose `ListModelsAsync()`.

### Several providers side by side

The registrations above replace one another, because each one registers the single `IDecisionsClient`. To use more than one provider in an app, register each under a key and inject it with `[FromKeyedServices]`:

```csharp
builder.Services.AddKeyedJevSystemOneClient("typesafe", builder.Configuration);
builder.Services.AddKeyedOpenRouterDecisionsClient("openrouter", builder.Configuration);
builder.Services.AddKeyedJevSystemOneClient("local", "local-key",
    new JevClientOptions { BaseUri = new Uri("http://localhost:1234/") });

public sealed class Comparison(
    [FromKeyedServices("typesafe")] IDecisionsClient direct,
    [FromKeyedServices("openrouter")] IDecisionsClient router) { }
```

Each keyed registration gets its own named `HttpClient` and returns its `IHttpClientBuilder`. Keyed registrations do not add an unkeyed default.

## Blazor demo

Two image demos use the embedded PNGs in `JevDotNet.Demo/Images/`, loaded through `FileHelper`:

- **Image decisions** (`/image-decisions`) lets you select an image-capable model, edit a yes/no question, and evaluate Noul, Choice, and Score questions together.
- **Find it in the image** (`/image-box`) lets you describe a target, then draws the returned `BoxAnswer` over the original image using a responsive SVG overlay. A companion Noul answer reports target visibility; you can toggle the box and inspect its coordinates and confidence.

Both pages support switching between the breakfast and alien-invasion images, cancellation, response JSON, elapsed time, token usage, and reported cost. Changing an input clears the previous result. Requests run only on **Evaluate image** or **Find target**. The image selector uses `DemoDecisionModel.ModelSupportsImageInput()`; the Box page further limits its models to known Jev 1.14 entries because image support alone does not establish Box support. `FileHelper.ExtractImageBytesFromAssembly` removes bytes appended after the PNG `IEND` marker before previewing or encoding the image; the PNG chunks and pixels remain unchanged. Strict provider decoders can reject that appended data even when a browser displays the image. Providers with smaller upload limits may still reject an image; the page displays a retryable error.

The **Pick a lane** demo at `/pick-a-lane` lets the selected model drive alone on an animated three-lane road. Start, pause/resume, or reset; there are no steering controls. Relaxed, Normal, and Fast give barriers 5, 3, and 2 seconds to reach the car. Driving continues during requests, with one request in flight and at most two starts per second. A collision or 100 applied decisions ends the round. The dashboard shows lane probabilities, barriers passed, latency, tokens, and reported cost. Hiding the tab pauses the game.

`JevDotNet.Demo` is an Interactive Server showcase with support triage, content review, lead qualification, image decisions, image localization, bulk support triage, Jev as Judge, and a live chase game. It injects `IDecisionsClient` configured for OpenRouter. Model selectors and validation use the IDs and names returned by [OpenRouter's Decisions model catalog](https://openrouter.ai/api/v1/models?output_modalities=decisions), with Jev 1.14 as the default selection. The catalog loads at startup without an API key and refreshes every 15 minutes. Startup requires a successful catalog fetch; later fetch failures are logged and retain the last successful catalog. Those pages start with editable sample input and call the selected model only when you click **Evaluate**. In the game, clicking **Start chase** begins a bounded round of up to 100 chaser moves, with one live Decisions request per move. Pause or leave the page to stop the requests.

The **Bulk triage** page uses 95 bundled support messages. Select a Decisions model, then choose a batch size from 2 to 95 messages per request, or evaluate one message per request. Each message adds five questions, and the final batch may be smaller than the chosen size. A Microsoft Agent Framework agent sends the same five questions to `openai/gpt-6-luna` one message at a time with JSON-schema structured output. The three tabs show each model's scores and request times, then yes/no agreement at 50% and the average absolute score gap. Percentages from the bundled JSON appear as reference values only; they are never sent to either model or counted in the agreement metrics. Cancel stops future requests, and a rejected Decisions batch is reported without changing modes automatically.

The **[Jev as Judge](http://localhost:5120/jev-as-judge)** page evaluates Relevance, Completeness, Equivalence, and Groundedness on five-level rubrics. It bundles 100 hand-authored examples in `JevDotNet.Demo/Data/judge-data.json`: twenty-five per evaluator across five scenario families, with five examples at each expected rating from 1 to 5. These labels illustrate the rubrics rather than serving as a validated benchmark.

Choose a decision model and evaluator above the **Single execution run** and **Bulk execution run** tabs. In Single, choose an example, edit its inputs, and click **Judge response**. Relevance uses a query and response; Completeness and Equivalence also use a reference answer; Groundedness uses source context. Expected scores and author-written explanations are never sent to the model. Editing hides the original reference comparison until **Reset example**; selecting a dataset row opens its inputs and full completed Jev result in Single. Switching tabs preserves edits and completed results.

The displayed integer Likert rating is the most probable level (ties favor the lower level). The weighted score is Jev's zero-based score plus one, so it can be fractional. Results show all five probabilities, distribution confidence, actual model, measured request time, token usage, and reported cost when available. Dataset summaries report exact integer-rating agreement and mean absolute rating error across successful requests only. Failed items are reported individually and remaining items continue; cancellation preserves completed results. All examples remain browsable without an API key.

In Bulk, **Run 25 examples · Jev + Luna** runs the selected decision model and `openai/gpt-6-luna` sequentially on the original twenty-five examples with the same rubric and evidence. Every bulk execution includes both judges. The nested **Luna vs Jev** and **Jev details** tabs switch between the comparison and detailed Jev table from the same run. Luna returns an integer rating and a short rationale through Microsoft Agent Framework with strict JSON-schema output. The comparison shows both judges' agreement with reference labels, their agreement with each other, rating gaps, and original request times. Edited inputs are evaluated only by the single-response decision-model action.

Luna requests and raw responses are saved in `JevDotNet.Demo/Data/judge-luna-cache/`, keyed by a SHA-256 fingerprint of the model, instructions, rubric, inputs, output schema, and output limit. Identical successful judgments are read from disk across runs, viewers, decision-model changes, and process restarts. Cache files contain original ratings, rationales, model, token usage, timestamps, and measured model latency; stored latency is not presented as cache retrieval time. Cache JSON files are included in build/publish output. Set `Judge:LunaCachePath` to a persistent writable directory when deploying; keep that directory across upgrades.

The **Only show results that differ from expected** toggle filters both bulk result tabs and persists when switching views. Jev details includes rows with a completed Jev rating different from the expected integer score; Luna vs Jev includes rows where either judge differs from expected, even if the judges agree with each other. Pending and failed requests have no rating and are hidden by the filter; turn it off to inspect those rows. Filtering changes only displayed rows: summary metrics, execution, and saved judgments remain unchanged.

A file lock serializes identical requests, and SDK HTTP retries are disabled. Only valid successful Luna judgments are cached. Failed, malformed, or cancelled requests are reported for the current run without saving an error entry, so a later run can try them again. Legacy failed or pending entries are discarded when encountered. Successful judgments remain reusable; corrupt or inconsistent stored results are surfaced as storage errors rather than silently replaced. No credentials are stored in the cache.

Run all four comparisons and save a detailed report to `Data/judge-comparison-results.json` with:

```powershell
dotnet run --project JevDotNet.Demo -- --Judge:RunComparison=true
```

Repeating this command re-evaluates the decision model and reuses stored Luna responses.

Configure an API key as an ASP.NET Core user secret:

```powershell
dotnet user-secrets set "OpenRouter:ApiKey" "<your-api-key>" --project JevDotNet.Demo
dotnet run --project JevDotNet.Demo
```

The key remains server-side. Without it, the app shows setup guidance and disables evaluation. The recommendations in the demo are illustrative C# rules and do not perform ticket routing, moderation, or CRM actions.

The chase game starts with the runner at column 6, row 6 and the chaser at column 1, row 1. Move the runner with arrow keys or the on-screen controls. Each request describes both positions, the walls, and the shortest path after each candidate chaser move. Criteria retain the move descriptions and distances, with exactly `up` and `right` as criteria. The game calculates `down = 1 - up` and `left = 1 - right`, then chooses the legal move with the highest probability. The page shows that move, all four probabilities, token use, reported cost, and average completed decisions per second. The rate includes the 400 ms turn pacing and excludes pauses. Reset the game to choose another model.
