# DecisionsDotNet

A type-safe C# client for TypeSafe AI's Jev models, through TypeSafe's direct API, OpenRouter's Decisions API, or OpenAI's Decisions API, and for Microsoft's decision models in Microsoft Foundry and Azure OpenAI. Targets .NET 10 and uses `System.Text.Json`, `HttpClient`, and Microsoft.Extensions.Http for dependency injection.

```powershell
dotnet add package DecisionsDotNet
```

Companion packages: [DecisionsDotNet.AI](https://www.nuget.org/packages/DecisionsDotNet.AI) exposes these clients as Microsoft.Extensions.AI tools, and [ExtractionsDotNet](https://www.nuget.org/packages/ExtractionsDotNet) extracts typed fields from text, images, and PDFs through OpenRouter.

## Direct TypeSafe quick start

Set `TYPESAFE_API_KEY` in your environment, then:

```csharp
using DecisionsDotNet;

using var client = TypeSafeDecisionsClient.FromEnvironment();

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
    },
    TypeSafeDecisionsClient.DefaultModel);

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
    .Build(TypeSafeDecisionsClient.DefaultModel);

var response = await client.EvaluateAsync(request);
Console.WriteLine($"{response.Get(team).Choice}, urgency {response.Get(urgent).Noul}, frustration {response.Get(frustration).Score}");
```

`Noul` takes optional `whenTrue` and `whenFalse` criteria, `Choice` takes plain option names or `(option, description)` pairs, `Score` takes levels lowest first, and `Box` takes instructions only. Duplicate question IDs throw. `Build(model)` takes the provider-specific model ID. Use `response.TryGet(key, out var answer)` or `response.TryGetAnswer<T>(id, out var answer)` when an answer may be absent or of another type.

Plain text implicitly converts to `DecisionsValue`. For structured state, instructions, or criteria, use `DecisionsValue.FromObject(...)` or `DecisionsValue.FromArray(...)`. You can pass custom `JsonSerializerOptions` to those methods. Choice criteria may be `null` when an option needs no description.

`DecisionsRequest` is shared by every client, and the model ID is always explicit because it differs by provider. `TypeSafeDecisionsClient.DefaultModel` is `jev-latest` for the direct TypeSafe API; pass another ID to pin a version or to use OpenRouter or OpenAI. Use `ListModelsAsync()` to fetch direct model aliases available to your account. The response's `Model` property contains the model that answered.

The client retries HTTP 429 and 529 by default, honoring `Retry-After` up to the configured maximum delay. Configure `TypeSafeDecisionsClientOptions` to change the base URI, number of attempts, or delays. Other HTTP failures throw `DecisionsApiException`, an `HttpRequestException` with `StatusCode`, `ResponseBody`, and `Service`. Cancellation is supported on both client methods. An injected `HttpClient` remains owned by the caller.

### Local or custom endpoints

Set `TypeSafeDecisionsClientOptions.BaseUri` to an absolute HTTP or HTTPS service root:

```csharp
using DecisionsDotNet;

var options = new TypeSafeDecisionsClientOptions
{
    BaseUri = new Uri("http://localhost:1234/")
};

using var client = new TypeSafeDecisionsClient("local-key", options: options);
```

Evaluation requests go to `http://localhost:1234/v1/systemone`, and `ListModelsAsync()` uses `http://localhost:1234/v1/models`. Supply the service root, not the full evaluation URL; include a trailing slash when the root contains a path prefix. HTTP also supports LAN addresses and container hostnames. The default remains `https://api.typesafe.ai/`.

For dependency injection, pass the same options to either registration overload:

```csharp
builder.Services.AddTypeSafeDecisionsClient("local-key", options);
// Or: builder.Services.AddTypeSafeDecisionsClient(builder.Configuration, options);
```

For a local server with default client settings, use the convenience registration:

```csharp
builder.Services.AddLocalTypeSafeDecisionsClient(new Uri("http://localhost:1234/"));
// Supply apiKey: "your-key" if the server requires authentication.
```

This registers `IDecisionsClient` and returns `IHttpClientBuilder` for HTTP pipeline configuration. It sends `local-key` as the default bearer key. Use `AddTypeSafeDecisionsClient` with options when you also need custom retry settings or model capability overrides.

`TypeSafeDecisionsClient.FromEnvironment(options: options)` also accepts the override. API keys remain required and are sent as bearer authorization; use a nonempty placeholder such as `local-key` if your local server does not require authentication. The server must implement the existing Jev request and response contract.

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

Each `OpenRouterDecisionsModel` carries `Id`, `CanonicalSlug`, `Name`, `Description`, `Created`, and `InputModalities`. Listing retries like evaluation does and reports HTTP failures as `DecisionsApiException`.

OpenRouter responses can also expose `Id`, `Provider`, and `Usage.Cost`; these are nullable because direct TypeSafe responses do not include them. The OpenRouter client retries HTTP 429, 502, 503, and 529 with bounded backoff. It surfaces other errors as `DecisionsApiException` with `Service` set to `OpenRouter`. See the [OpenRouter Decisions reference](https://openrouter.ai/docs/api/api-reference/alphadecisions/submit-a-decisions-questions-and-answers-request).

## OpenAI Decisions API

`OpenAIDecisionsClient` sends the same `DecisionsRequest` to OpenAI's [Decisions API](https://developers.openai.com/api/docs/guides/decisions) at `POST /v1/decisions`. Set `OPENAI_API_KEY`, then pass `OpenAIDecisionsClient.DefaultModel` (`gpt-6-luna`, the only model the API supports today):

```csharp
using var client = OpenAIDecisionsClient.FromEnvironment();
var request = DecisionsRequest.Create("My card was charged twice.")
    .Noul("refund", "Does this customer want money back?", out var refund)
    .Build(OpenAIDecisionsClient.DefaultModel);
var response = await client.EvaluateAsync(request);
Console.WriteLine(response.Get(refund).Noul);
```

The client translates the request to OpenAI's shape and the answers back to the shared types:

| SDK | OpenAI |
| --- | --- |
| `NoulQuestion` / `NoulAnswer.Noul` | `predicate` / `probability`. Noul criteria are appended to the instructions as "True when" and "False when" lines, because predicates take instructions only. |
| `ChoiceQuestion` / `ChoiceAnswer` | `choice` with `choices[{value, description}]`. An option without a description uses its name as the description. |
| `ScoreQuestion` / `ScoreAnswer` | `score` with `levels[{label, description}]`, lowest first. `Legend` and `Probabilities` are keyed by the zero-based level index, as with Jev. |
| `BoxQuestion` | Not supported; throws `NotSupportedException`. |
| (none) | `refusal` answers become `RefusalAnswer`. Reading one with `GetAnswer<NoulAnswer>` throws, so use `TryGetAnswer` when refusals are possible. |

Text state is sent as the `input` string, and structured state as JSON text. State containing images becomes one user message with `input_text` and `input_image` parts in the original order. Like the other clients, images must be inline base64 data URLs. Set `OpenAIDecisionsClientOptions.Organization` or `Project` to send the matching headers. The client retries HTTP 429, 500, 502, 503, and 504 and reports other failures as `DecisionsApiException` with `Service` set to `OpenAI`. Register it with `AddOpenAIDecisionsClient` (reads `OpenAI:ApiKey`) or `AddKeyedOpenAIDecisionsClient`.

## Microsoft Foundry and Azure OpenAI

Both Azure clients take an endpoint, a deployment name, and either an API key or an Azure.Core `TokenCredential`, such as `DefaultAzureCredential` or `AzureCliCredential` from Azure.Identity. An API key is sent in the `api-key` header. A credential's tokens are requested for `https://cognitiveservices.azure.com/.default` (change it with the options' `TokenScope`), sent as a bearer token, and cached until five minutes before they expire. Azure routes by deployment, so the client always sends its deployment name as the model and the request's `Model` is not sent. Neither service returns an evaluation ID, so `DecisionsResponse.Id` is null for both.

`FoundryDecisionsClient` calls [Microsoft decision models in Foundry](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision), such as Microsoft-Decision-1.1, at `{endpoint}/v1/systemone`. It sends the same Noul, Choice, and Score questions as OpenRouter and reads the same answers. Box questions are not supported. Images in the state array are sent as Foundry `assets` and referenced from the state text as `image:<name>`.

```csharp
using Azure.Identity;

using var foundry = new FoundryDecisionsClient(
    new Uri("https://my-project.services.ai.azure.com/providers/microsoft"),
    deploymentName: "pi-decision-1.1",
    new AzureCliCredential());

var response = await foundry.EvaluateAsync(DecisionsRequest.Create("The API returns 500 on every call.")
    .Choice("team", "Which team should handle this ticket?", out var team,
        ("billing", "Charges, invoices, and refunds"),
        ("engineering", "Bugs, errors, and outages"))
    .Build(foundry.DeploymentName));
Console.WriteLine(response.Get(team).Choice);
```

A bare resource endpoint (`https://my-project.services.ai.azure.com`) also works; the client adds `/providers/microsoft`.

`AzureOpenAIDecisionsClient` calls the [Azure OpenAI Decisions API](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/decisions) at `{endpoint}/openai/v1/decisions` and translates requests and answers exactly as `OpenAIDecisionsClient` does. The endpoint can be the resource root (`https://my-resource.openai.azure.com/`) or the `.../openai/v1/` base URL the OpenAI SDKs use.

```csharp
using var azureOpenAI = new AzureOpenAIDecisionsClient(
    new Uri("https://my-resource.openai.azure.com/"),
    deploymentName: "gpt-6-luna",
    Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!);
```

Both retry HTTP 429, 500, 502, 503, and 504 and report other failures as `DecisionsApiException` with `Service` set to `Foundry` or `Azure OpenAI`. Register them with `AddFoundryDecisionsClient` and `AddAzureOpenAIDecisionsClient`, or the `AddKeyed*` versions. The configuration overloads read `Endpoint`, `DeploymentName`, and `ApiKey` from the `Foundry` or `AzureOpenAI` section and fall back to the `TokenCredential` you pass when no key is configured:

```csharp
builder.Services.AddFoundryDecisionsClient(builder.Configuration, new DefaultAzureCredential());
builder.Services.AddKeyedAzureOpenAIDecisionsClient("azure-openai", builder.Configuration, new DefaultAzureCredential());
```

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

using var direct = TypeSafeDecisionsClient.FromEnvironment();
var directResult = await direct.EvaluateAsync(new DecisionsRequest(state, questions, "jev-1.14.0"));
```

A standalone image value is normalized into a one-item state array. Images created through `FromObject`, `FromArray`, or `FromJson` with the same `image_url` shape are also recognized and validated. This release supports image parts directly in the top-level state array. Nested images and images in question instructions or criteria throw `NotSupportedException`. Text items are strings, not `{ "type": "text" }` parts.

OpenRouter receives base64 `image_url` parts in `state`. Direct TypeSafe receives image references in state and a top-level `assets` map, and the SDK adds `TypeSafe-Danger-Zone-Images: true` to evaluations containing images or Box questions. PNG and JPEG work with both clients; WebP is accepted only through OpenRouter, subject to the selected provider's support. Remote image URLs, empty data, unsupported media types, and malformed base64 are rejected locally. See [OpenRouter's multimodal guide](https://openrouter.ai/docs/guides/community/multimodal-decisions) and [TypeSafe's image contract](https://docs.typesafe.ai/images).

Prepare image dimensions, orientation, and compression before calling the SDK. It does not decode, resize, rotate, or fetch images. Image counts, dimensions, and request-size limits differ by provider and are validated by the server. Provider errors retain their status code and response body in `DecisionsApiException`. Direct TypeSafe's image feature is experimental; using image input opts the request into its required header.

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

Use the same property on `TypeSafeDecisionsClientOptions` for direct models. The direct client recognizes image support for `jev-1.13.0`, `jev-1.14.0`, `jev-latest`, and `jev-preview`. Other IDs require an override before sending images. Direct requests without images retain native structured state for unresolved models; an explicit text-only override serializes structured state to JSON text. Overrides describe capabilities; they cannot add image support to a provider that lacks it.

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

using var client = TypeSafeDecisionsClient.FromEnvironment();
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

## Conditionals in ordinary C#

`ForModel` binds a client to a model, and the verbs on the resulting `DecisionModel` each ask one question and return something you can branch on. Each verb is one paid network call, so put cheap checks first and let `&&` skip the model when it can:

```csharp
var model = client.ForModel("typesafe/jev-1.14");

if (ticket.IsOpen && await model.MatchesAsync(ticket.Text, "Is the customer asking for a refund?",
        minProbability: 0.75, cancellationToken: ct))
{
    await QueueRefundReviewAsync(ticket, ct);
}
```

You always pick the threshold, because a moderation rule and a refund escalation need different tradeoffs. `ProbabilityAsync` returns the raw probability instead. When uncertainty needs its own path, `VerdictAsync` returns Yes, No, Unsure, or Refused:

```csharp
var verdict = await model.VerdictAsync(post.Body, "Does this violate the harassment policy?",
    unsureBand: (0.3, 0.7), cancellationToken: ct);

return verdict.Outcome switch
{
    VerdictOutcome.Yes     => RemovePost(post),
    VerdictOutcome.No      => PublishPost(post),
    VerdictOutcome.Unsure  => QueueForHumanReview(post, verdict.Probability),
    VerdictOutcome.Refused => EscalatePolicyRefusal(post, verdict.Response),
    _                      => throw new UnreachableException()
};
```

`ChooseAsync<TEnum>` offers the members of an enum, using their names as labels and `[Description]` text as criteria. Pass a dictionary instead to offer a subset or vary the criteria. `ScoreAsync<TEnum>` rates on an enum's members, lowest value first:

```csharp
public enum Team
{
    [Description("Payments, invoices, and refunds")] Billing,
    [Description("Bugs, errors, and service outages")] Technical,
    [Description("Anything that does not fit another team")] Other
}

var route = await model.ChooseAsync<Team>(ticket.Text, "Which team should handle this request?", ct);
var queue = route.SelectedProbability < 0.6 ? Queue.ManualReview : route.Value switch
{
    Team.Billing   => Queue.Billing,
    Team.Technical => Queue.Technical,
    _              => Queue.ManualReview
};
```

Every result keeps its evidence: the probability or distribution, the raw `Answer`, and the full `Response` with model and usage. Failures never turn into answers. Provider and network errors propagate as before, invalid probabilities or labels throw `JsonException`, and a refusal throws `DecisionRefusedException` from every verb except `VerdictAsync`, which reports it as `Refused`.

### Several questions in one request

> Experimental (`DECISIONS002`), along with the `[Noul]`, `[Choice]` and `[Score]` attributes. Add `<NoWarn>$(NoWarn);DECISIONS002</NoWarn>` to use it.

`DecideAsync<T>` asks every question declared on a class in one request and returns it populated. Properties can be the rich result types or plain values: `[Noul]` fits `DecisionVerdict` (with `UnsureMin`/`UnsureMax`), `bool` (with `MinProbability`), or `double`; `[Choice]` fits `DecisionChoice<TEnum>` or the enum; `[Score]` fits `DecisionScore<TEnum>` or the enum, or `int`, `double` and `DecisionScore` when you pass text levels.

```csharp
public sealed class TicketTriage
{
    [Noul("Does this require immediate attention?", UnsureMin = 0.3, UnsureMax = 0.7)]
    public DecisionVerdict Urgent { get; init; } = null!;

    [Choice("Which team should handle this request?")]
    public DecisionChoice<Team> Team { get; init; } = null!;

    [Score("How frustrated is the customer?")]
    public DecisionScore<Frustration> Mood { get; init; } = null!;
}

TicketTriage triage = await model.DecideAsync<TicketTriage>(ticket.Text, ct);
```

The class needs a public parameterless constructor and settable properties, and is read by reflection once per type, so `DecideAsync` is not trim or AOT safe. `DecideWithResponseAsync` also returns the response for logging and usage.

### Collections

> Experimental (`DECISIONS002`). Add `<NoWarn>$(NoWarn);DECISIONS002</NoWarn>` to use it.

The collection verbs ask one question about every item and pack items into batched requests, so 200 items with the default `ItemsPerRequest` of 10 is 20 requests, not 200. They accept `IEnumerable<T>` or `IAsyncEnumerable<T>`:

```csharp
var (spam, ham) = await model.PartitionAsync(messages, m => m.Text, "Is this unsolicited marketing?", minProbability: 0.8, ct);

await foreach (var urgent in model.WhereAsync(tickets, t => t.Body, "Is the customer reporting an outage?", minProbability: 0.75, ct))
    await PageOnCallAsync(urgent, ct);

await foreach (var group in model.GroupByChoiceAsync<Team, Ticket>(tickets, t => t.Body, "Which team should handle this?", ct))
    await DispatchBatchAsync(group.Key, group.Items, ct);

var byMood = await model.OrderByScoreAsync<Frustration, Ticket>(tickets, t => t.Body, "How frustrated is the customer?", ct);
```

`WhereAsync` yields matches a batch at a time. `GroupByChoiceAsync` keeps each item's `DecisionChoice` in `group.Results`. Pass `DecisionCollectionOptions<T>` to change `ItemsPerRequest` or add a `Prefilter` that settles obvious items without sending them. A failed request or a refusal stops the operation with its exception.

To inject a bound model, register it after the client: `services.AddDecisionModel("typesafe/jev-1.14")`, or `AddKeyedDecisionModel(key, modelId, clientServiceKey)` beside keyed clients. Hand-built requests still name their model; `DecisionModel.EvaluateAsync(builder)` builds one with the bound model.

For ASP.NET Core filters, FluentValidation rules and MediatR behaviors built on these verbs, see the [recipes](https://github.com/HillPhelmuth/JevDotNet/blob/main/docs/recipes.md). For unit tests, [DecisionsDotNet.Testing](https://www.nuget.org/packages/DecisionsDotNet.Testing) has a scripted `FakeDecisionsClient` and a `RecordingDecisionsClient`.

## Pipelines: caching, fallback, logging, and tracing

> Experimental (`DECISIONS002`), in the `DecisionsDotNet.Pipeline` namespace.

`AddDecisionsClient` wraps a provider client in decorators, in the style of Microsoft.Extensions.AI's `ChatClientBuilder`. The first `Use` call is the outermost. Resolve the inner client by key, since the pipeline itself is the unkeyed `IDecisionsClient`:

```csharp
using DecisionsDotNet.Caching;   // UseCaching, from the DecisionsDotNet.Caching package
using DecisionsDotNet.Pipeline;

builder.Services.AddHybridCache();
builder.Services.AddKeyedOpenRouterDecisionsClient("openrouter", builder.Configuration);
builder.Services.AddKeyedTypeSafeDecisionsClient("typesafe", builder.Configuration);

builder.Services.AddDecisionsClient(sp => sp.GetRequiredKeyedService<IDecisionsClient>("openrouter"))
    .UseLogging()
    .UseOpenTelemetry()
    .UseCaching(TimeSpan.FromHours(1))
    .UseFallback("typesafe", model => model == "typesafe/jev-1.14" ? "jev-1.14.0" : model);
builder.Services.AddDecisionModel("typesafe/jev-1.14");
```

- **`UseFallback`** retries on the keyed fallback client after HTTP 408, 429 or 5xx, network errors, timeouts, and malformed responses. Authentication, permission and other 4xx errors, and your own cancellation, still throw. Model IDs differ by provider, so pass a mapping.
- **`UseLogging`** writes model, question IDs, elapsed time, tokens and refusals at Information, and each answer at Debug. It never logs the state or instructions.
- **`UseOpenTelemetry`** starts a `decisions.evaluate` span with the model, question IDs, each answer (`decisions.answer.<id>` such as `choice:Billing@0.91`), tokens and cost, and records `decisions.client.duration`, `decisions.client.tokens`, `decisions.client.cost` and `decisions.client.refusals`. Subscribe with `AddSource("DecisionsDotNet")` and `AddMeter("DecisionsDotNet")`.
- **`UseCaching`** lives in [DecisionsDotNet.Caching](https://www.nuget.org/packages/DecisionsDotNet.Caching), so core does not depend on `HybridCache`.

Derive from `DelegatingDecisionsClient` for your own decorator and add it with `Use`. For HTTP-level resilience, every `Add*DecisionsClient` returns its `IHttpClientBuilder`, which `Microsoft.Extensions.Http.Resilience` plugs into.

## Agents and chat tools

The companion package `DecisionsDotNet.AI` exposes any `IDecisionsClient` as a Microsoft.Extensions.AI `AIFunction`, so a chat client or Microsoft Agent Framework agent can call Jev, OpenRouter, or OpenAI Decisions as a tool. `AsAIFunction` asks a fixed set of questions about text the model supplies; `AsOpenDecisionsAIFunction` lets the model write its own yes/no, choice, and score questions. See [its README](https://github.com/HillPhelmuth/JevDotNet/blob/main/DecisionsDotNet.AI/README.md).

## Dependency injection

Register one provider, then inject `IDecisionsClient` into your service. Registration accepts either a key string or `IConfiguration`; the configuration overload reads `OpenRouter:ApiKey` or `TypeSafe:ApiKey` at startup and rejects a missing key. Keep these values in server-side configuration, such as ASP.NET Core user secrets.

```csharp
using DecisionsDotNet;

builder.Services.AddOpenRouterDecisionsClient(builder.Configuration);
// Or: builder.Services.AddTypeSafeDecisionsClient(builder.Configuration);
// Both also accept a key string and optional provider-specific client options.

public sealed class DecisionService(IDecisionsClient client)
{
    public Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        => client.EvaluateAsync(request, cancellationToken);
}
```

The registration returns `IHttpClientBuilder` for HTTP pipeline configuration. Set `DecisionsRequest.Model` to an OpenRouter Decisions model ID, such as `"typesafe/jev-1.13"` or `"jaredpalmer/kev-4b"`; `jev-latest` is only valid on the direct TypeSafe API. When several unkeyed registrations are made, the last one is injected. Both concrete clients expose `ListModelsAsync()`.

### Several providers side by side

The registrations above replace one another, because each one registers the single `IDecisionsClient`. To use more than one provider in an app, register each under a key and inject it with `[FromKeyedServices]`:

```csharp
builder.Services.AddKeyedTypeSafeDecisionsClient("typesafe", builder.Configuration);
builder.Services.AddKeyedOpenRouterDecisionsClient("openrouter", builder.Configuration);
builder.Services.AddKeyedTypeSafeDecisionsClient("local", "local-key",
    new TypeSafeDecisionsClientOptions { BaseUri = new Uri("http://localhost:1234/") });

public sealed class Comparison(
    [FromKeyedServices("typesafe")] IDecisionsClient direct,
    [FromKeyedServices("openrouter")] IDecisionsClient router) { }
```

Each keyed registration gets its own named `HttpClient` and returns its `IHttpClientBuilder`. Keyed registrations do not add an unkeyed default.

## Stability

From 1.0.0 the public types in this package follow semantic versioning: breaking changes only arrive in a major version. Two things sit outside that promise.

- **Upstream APIs.** OpenRouter's Decisions route is still served under `/api/alpha/`, and TypeSafe labels image input on its direct API as experimental. When a provider changes its wire format, the SDK adapts in a minor or patch release and keeps its own types stable where it can.
- **Experimental members.** Anything marked `[Experimental]` may change in a minor release: currently, under `DECISIONS002`, decision sets (`DecideAsync<T>`), the collection verbs, the `DecisionsDotNet.Pipeline` namespace, the guardrail and workflow helpers in `DecisionsDotNet.AI`, and everything in `DecisionsDotNet.Testing` and `DecisionsDotNet.Caching`. `ForModel`, `DecisionModel`, its single-question verbs and their result types are stable from 1.1.0, as are the OpenAI, Foundry and Azure OpenAI clients.

See the [changelog](https://github.com/HillPhelmuth/JevDotNet/blob/main/CHANGELOG.md) for the migration from 0.3.
