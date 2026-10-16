# JevDotNet

A type-safe C# client for TypeSafe AI's Jev models, through either TypeSafe's direct API or OpenRouter's Decisions API. Targets .NET 10 and uses `System.Text.Json`, `HttpClient`, and Microsoft.Extensions.Http for dependency injection.

## Direct TypeSafe quick start

Set `TYPESAFE_API_KEY` in your environment, then:

```csharp
using JevDotNet;

using var client = JevClient.FromEnvironment();

var request = new DecisionsRequest(
    JevValue.FromObject(new { message = "My card was charged twice." }),
    new Dictionary<string, JevQuestion>
    {
        ["urgent"] = new NoulQuestion("Does this need immediate attention?"),
        ["team"] = new ChoiceQuestion("Which team should handle this?",
            new Dictionary<string, JevValue?>
            {
                ["billing"] = "Payments and refunds",
                ["technical"] = "Bugs and outages"
            }),
        ["frustration"] = new ScoreQuestion("How frustrated is the customer?",
            new JevValue[] { "Calm", "Frustrated", "Very angry" })
    });

var response = await client.EvaluateAsync(request);
double urgency = response.GetAnswer<NoulAnswer>("urgent").Noul;
string team = response.GetAnswer<ChoiceAnswer>("team").Choice;
double frustration = response.GetAnswer<ScoreAnswer>("frustration").Score;
Console.WriteLine($"{response.Model}: {team}, urgency {urgency}, frustration {frustration}");
```

Plain text implicitly converts to `JevValue`. For structured state, instructions, or criteria, use `JevValue.FromObject(...)` or `JevValue.FromArray(...)`. You can pass custom `JsonSerializerOptions` to those methods. Choice criteria may be `null` when an option needs no description.

`DecisionsRequest` is shared by both clients. It defaults to `jev-latest` for the direct TypeSafe API; pass a model ID as its third argument to pin a version or to use OpenRouter. Use `ListModelsAsync()` to fetch direct model aliases available to your account. The response's `Model` property contains the model that answered.

The client retries HTTP 429 and 529 by default, honoring `Retry-After` up to the configured maximum delay. Configure `JevClientOptions` to change the base URI, number of attempts, or delays. Other HTTP failures throw `JevApiException` with `StatusCode` and `ResponseBody`. Cancellation is supported on both client methods. An injected `HttpClient` remains owned by the caller.

## Verify

```powershell
dotnet run --project JevDotNet.Tests/JevDotNet.Tests.csproj
dotnet pack JevDotNet/JevDotNet.csproj
```

The contract checks use fake HTTP handlers and need no API key. The direct client follows the [TypeSafe AI API reference](https://docs.typesafe.ai/api) and [model listing reference](https://docs.typesafe.ai/models).

## OpenRouter Decisions API

Set `OPENROUTER_API_KEY` in your environment, then use the same `DecisionsRequest` and typed questions with `OpenRouterDecisionsClient`:

```csharp
using JevDotNet;

using var client = OpenRouterDecisionsClient.FromEnvironment();
var request = new DecisionsRequest(
    "My card was charged twice.",
    new Dictionary<string, JevQuestion>
    {
        ["refund"] = new NoulQuestion("Does this customer request a refund?")
    },
    "typesafe/jev-1.13");

var response = await client.EvaluateAsync(request);
Console.WriteLine(response.GetAnswer<NoulAnswer>("refund").Noul);
Console.WriteLine($"{response.Model}: {response.Usage.InputTokens} input tokens");
```

The OpenRouter client always sends `state` as a string for compatibility with models that require text input. Plain text is preserved; object and array states are serialized to JSON text inside that string. The original `DecisionsRequest` and the direct TypeSafe client's structured-state behavior remain unchanged.

OpenRouter responses can also expose `Id`, `Provider`, and `Usage.Cost`; these are nullable because direct TypeSafe responses do not include them. The OpenRouter client retries HTTP 429, 502, 503, and 529 with bounded backoff. It surfaces other errors as `JevApiException` with `Service` set to `OpenRouter`. See the [OpenRouter Decisions reference](https://openrouter.ai/docs/api/api-reference/alphadecisions/submit-a-decisions-questions-and-answers-request).

## Dependency injection

Register one provider, then inject `IDecisionsClient` into your service. Registration accepts either a key string or `IConfiguration`; the configuration overload reads `OpenRouter:ApiKey` or `TypeSafe:ApiKey` at startup and rejects a missing key. Keep these values in server-side configuration, such as ASP.NET Core user secrets.

```csharp
using JevDotNet;

builder.Services.AddOpenRouterDecisionsClient(builder.Configuration);
// Or: builder.Services.AddJevDecisionsClient(builder.Configuration);
// Both also accept a key string and optional provider-specific client options.

public sealed class DecisionService(IDecisionsClient client)
{
    public Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
        => client.EvaluateAsync(request, cancellationToken);
}
```

The registration returns `IHttpClientBuilder` for HTTP pipeline configuration. Set `DecisionsRequest.Model` to an OpenRouter Decisions model ID, such as `"typesafe/jev-1.13"` or `"jaredpalmer/kev-4b"`; the request default `jev-latest` is for the direct TypeSafe API. `JevClient.ListModelsAsync()` remains available on the concrete direct client.

## Blazor demo

The **Pick a lane** demo at `/pick-a-lane` lets the selected model drive alone on an animated three-lane road. Start, pause/resume, or reset; there are no steering controls. Relaxed, Normal, and Fast give barriers 5, 3, and 2 seconds to reach the car. Driving continues during requests, with one request in flight and at most two starts per second. A collision or 100 applied decisions ends the round. The dashboard shows lane probabilities, barriers passed, latency, tokens, and reported cost. Hiding the tab pauses the game. Run its deterministic browser simulation checks with `node --test JevDotNet.Tests/lane-simulation.test.mjs`.

`JevDotNet.Demo` is an Interactive Server showcase with support triage, content review, lead qualification, bulk support triage, Jev as Judge, and a live chase game. It injects `IDecisionsClient` configured for OpenRouter. Model selectors and validation use the IDs and names returned by [OpenRouter's Decisions model catalog](https://openrouter.ai/api/v1/models?output_modalities=decisions), with Jev 1.13 as the default selection. The catalog loads at startup without an API key and refreshes every 15 minutes. Startup requires a successful catalog fetch; later fetch failures are logged and retain the last successful catalog. Those pages start with editable sample input and call the selected model only when you click **Evaluate**. In the game, clicking **Start chase** begins a bounded round of up to 100 chaser moves, with one live Decisions request per move. Pause or leave the page to stop the requests.

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
