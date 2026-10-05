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

## GitHub Actions publishing

Both workflows in `.github/workflows` install .NET 10, build in Release, and run the contract checks before publishing. Configure their secrets and variables in the repository's **Settings > Secrets and variables > Actions**.

### NuGet

`publish-nuget.yml` publishes `JevDotNet` to nuget.org when a GitHub release is published, or when manually run from the Actions tab. Set the repository secret `NUGET_API_KEY` to a nuget.org API key with permission to push `JevDotNet`.

The package version comes from `PackageVersion` in `JevDotNet/JevDotNet.csproj`; update it before creating a release and use a matching release tag (for example, `v1.0.1`). The workflow overrides the local package output path, pushes the package and its `.snupkg` symbols, and skips versions already published.

### Azure App Service demo

`deploy-demo.yml` deploys `JevDotNet.Demo` on relevant pushes to `main`, or when manually run from the Actions tab. Change its branch filter if your default branch has a different name. Create an Azure App Service with the .NET 10 runtime, then configure:

- Repository variable `AZURE_WEBAPP_NAME`: the existing App Service name.
- Repository secret `AZURE_WEBAPP_PUBLISH_PROFILE`: the complete contents of the App Service's downloaded publish profile. Publish profile authentication requires **SCM Basic Auth Publishing Credentials** to be enabled; see [Azure's GitHub Actions deployment guide](https://learn.microsoft.com/en-us/azure/app-service/deploy-github-actions).
- App Service application setting `OpenRouter__ApiKey`: the OpenRouter API key used by the demo.
- App Service application setting `Judge__LunaCachePath`: a persistent, writable directory outside the deployed application directory, such as `/home/data/judge-luna-cache` on Linux or `D:\home\data\judge-luna-cache` on Windows, to preserve cached Luna judgments across deployments.

The workflow deploys the framework-dependent publish output to the production slot using `azure/webapps-deploy`.

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

`JevDotNet.Demo` is an Interactive Server showcase with support triage, content review, lead qualification, bulk support triage, Jev as Judge, and a live chase game. It injects `IDecisionsClient` configured for OpenRouter. The individual workflow demos let you choose Jev 1.13 (the default), Jev Latest, Kev 4B, Span-01, Span-01 Lite, or Span-01 Lite (free). Those pages start with editable sample input and call the selected model only when you click **Evaluate**. In the game, clicking **Start chase** begins a bounded round of up to 100 chaser moves, with one live Decisions request per move. Pause or leave the page to stop the requests.

The **Bulk triage** page uses 95 bundled support messages. Select a Decisions model, then choose a batch size from 2 to 95 messages per request, or evaluate one message per request. Each message adds five questions, and the final batch may be smaller than the chosen size. A Microsoft Agent Framework agent sends the same five questions to `openai/gpt-6-luna` one message at a time with JSON-schema structured output. The three tabs show each model's scores and request times, then yes/no agreement at 50% and the average absolute score gap. Percentages from the bundled JSON appear as reference values only; they are never sent to either model or counted in the agreement metrics. Cancel stops future requests, and a rejected Decisions batch is reported without changing modes automatically.

The **[Jev as Judge](http://localhost:5120/jev-as-judge)** page evaluates Relevance, Completeness, Equivalence, and Groundedness on five-level rubrics. It bundles 80 hand-authored examples in `JevDotNet.Demo/Data/judge-data.json`: twenty per evaluator across four scenario families, with four examples at each expected rating from 1 to 5. These labels illustrate the rubrics rather than serving as a validated benchmark.

Choose a decision model and evaluator above the **Single execution run** and **Bulk execution run** tabs. In Single, choose an example, edit its inputs, and click **Judge response**. Relevance uses a query and response; Completeness and Equivalence also use a reference answer; Groundedness uses source context. Expected scores and author-written explanations are never sent to the model. Editing hides the original reference comparison until **Reset example**; selecting a dataset row opens its inputs and full completed Jev result in Single. Switching tabs preserves edits and completed results.

The displayed integer Likert rating is the most probable level (ties favor the lower level). The weighted score is Jev's zero-based score plus one, so it can be fractional. Results show all five probabilities, distribution confidence, actual model, measured request time, token usage, and reported cost when available. Dataset summaries report exact integer-rating agreement and mean absolute rating error across successful requests only. Failed items are reported individually and remaining items continue; cancellation preserves completed results. All examples remain browsable without an API key.

In Bulk, **Run 20 examples · Jev + Luna** runs the selected decision model and `openai/gpt-6-luna` sequentially on the original twenty examples with the same rubric and evidence. Every bulk execution includes both judges. The nested **Luna vs Jev** and **Jev details** tabs switch between the comparison and detailed Jev table from the same run. Luna returns an integer rating and a short rationale through Microsoft Agent Framework with strict JSON-schema output. The comparison shows both judges' agreement with reference labels, their agreement with each other, rating gaps, and original request times. Edited inputs are evaluated only by the single-response decision-model action.

Luna requests and raw responses are saved in `JevDotNet.Demo/Data/judge-luna-cache/`, keyed by a SHA-256 fingerprint of the model, instructions, rubric, inputs, output schema, and output limit. Identical successful judgments are read from disk across runs, viewers, decision-model changes, and process restarts. Cache files contain original ratings, rationales, model, token usage, timestamps, and measured model latency; stored latency is not presented as cache retrieval time. Cache JSON files are included in build/publish output. Set `Judge:LunaCachePath` to a persistent writable directory when deploying; keep that directory across upgrades.

To avoid duplicate LLM calls, a file lock serializes identical requests, an attempt is written before contacting Luna, and SDK HTTP retries are disabled. Failed, interrupted, or corrupt entries are surfaced as errors and are never automatically resubmitted. Preserve the cache files; deleting an entry permits a new request. No credentials are stored in the cache.

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

The chase game starts with the runner at column 6, row 6 and the chaser at column 1, row 1. Move the runner with arrow keys or the on-screen controls. Each request describes both positions, the walls, and the shortest path after each legal chaser move. The page shows the model's last choice, probabilities, token use, reported cost, and average completed decisions per second. The rate includes the 400 ms turn pacing and excludes pauses. Reset the game to choose another model.
