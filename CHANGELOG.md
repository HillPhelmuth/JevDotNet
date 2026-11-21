# Changelog

`DecisionsDotNet` and its add-on packages (`DecisionsDotNet.AI`, `DecisionsDotNet.Testing`, `DecisionsDotNet.Caching`) share a version. `ExtractionsDotNet` versions on its own. From 1.0.0 all of them follow [semantic versioning](https://semver.org/).

## 1.1.0 (2026-11-21)

Adds semantic conditionals, the Microsoft Foundry and Azure OpenAI clients, a client pipeline, and two new add-on packages. `DecisionsDotNet.Testing` and `DecisionsDotNet.Caching` are new and version with `DecisionsDotNet`. `ExtractionsDotNet` is unchanged at 1.0.0. Nothing in 1.0.0 is removed or changed incompatibly, which package validation against 1.0.0 now checks on every pack.

### DecisionsDotNet

Now stable (no longer `[Experimental]`), after live smoke tests against every provider:

- `OpenAIDecisionsClient`, `OpenAIDecisionsClientOptions`, `AddOpenAIDecisionsClient` and `AddKeyedOpenAIDecisionsClient` (were `DECISIONS001` in 1.0.0). Suppressing `DECISIONS001` is no longer needed.

Added, stable:

- `FoundryDecisionsClient` for Microsoft decision models (such as Microsoft-Decision-1.1) deployed in Microsoft Foundry, at `{endpoint}/v1/systemone`, and `AzureOpenAIDecisionsClient` for the Azure OpenAI Decisions API at `{endpoint}/openai/v1/decisions`. Both take an endpoint, a deployment name, and either an API key (`api-key` header) or an Azure.Core `TokenCredential` (bearer token for `https://cognitiveservices.azure.com/.default`, cached until five minutes before it expires). `Add[Keyed]FoundryDecisionsClient` and `Add[Keyed]AzureOpenAIDecisionsClient` register them, including from the `Foundry` and `AzureOpenAI` configuration sections. Neither service returns an evaluation ID, so `DecisionsResponse.Id` is null for their responses.
- `IDecisionsClient.ForModel(modelId)` and `DecisionModel`, a client bound to one model. `AddDecisionModel` and `AddKeyedDecisionModel` register one for injection. The low-level request API is unchanged and still names its model.
- Conditional verbs on `DecisionModel`: `MatchesAsync` (caller-chosen threshold), `ProbabilityAsync`, `VerdictAsync` (Yes, No, Unsure, Refused), `ChooseAsync<TEnum>`, `ScoreAsync<TEnum>`, and a text-level `ScoreAsync`.
- Result types `DecisionVerdict`, `VerdictOutcome`, `DecisionChoice<TEnum>`, `DecisionScore<TEnum>` and `DecisionScore`, each keeping the raw answer and full response, and `DecisionRefusedException`.

Added, experimental under `DECISIONS002`:

- Typed decision sets: `DecideAsync<T>` and `DecideWithResponseAsync<T>` ask every `[Noul]`, `[Choice]` and `[Score]` property of a class in one request.
- Batched collection verbs on `DecisionModel`: `WhereAsync`, `PartitionAsync`, `GroupByChoiceAsync<TEnum, T>` and `OrderByScoreAsync<TEnum, T>`, over `IEnumerable<T>` or `IAsyncEnumerable<T>`, built on `EvaluateBatchesAsync`, with `DecisionCollectionOptions<T>` for batch size and a prefilter.
- Client pipeline in `DecisionsDotNet.Pipeline`: `DelegatingDecisionsClient`, `DecisionsClientBuilder`, `AddDecisionsClient` and `AddKeyedDecisionsClient`, with `UseFallback`, `UseLogging` and `UseOpenTelemetry`.

Dependencies:

- Now depends on `Azure.Core` for `TokenCredential`.

### DecisionsDotNet.AI

Added, experimental under `DECISIONS002`:

- `UseDecisionGuardrails` and `DecisionGuardrailChatClient`: calibrated input and output checks around any `IChatClient`, failing closed.
- `Router` and `Gate` workflow steps, with `DecisionRoute<TMessage, TEnum>.When` and `DecisionGate<TMessage>.When` edge conditions for Agent Framework workflows.
- Now depends on `Microsoft.Extensions.AI` (for `ChatClientBuilder`) instead of only `Microsoft.Extensions.AI.Abstractions`.

### DecisionsDotNet.Testing (new)

- `FakeDecisionsClient`, which answers from scripted rules, and `RecordingDecisionsClient`, which wraps any client and records its calls. Experimental under `DECISIONS002`.

### DecisionsDotNet.Caching (new)

- `UseCaching` and `CachingDecisionsClient`, which cache successful evaluations in `HybridCache` by a content hash of the request. Experimental under `DECISIONS002`.

### Docs

- [docs/recipes.md](docs/recipes.md): ASP.NET Core endpoint filter, FluentValidation, MediatR, and multi-condition examples, compiled against the packed packages in the package smoke check.

### Experimental APIs

Members marked `[Experimental]` produce a compiler error until you suppress their diagnostic, and may change in a minor release. From 1.1.0 only one diagnostic remains.

| Diagnostic | Members | Why |
| --- | --- | --- |
| `DECISIONS002` | Decision sets (`DecideAsync<T>`, `DecideWithResponseAsync<T>`, `DecisionSetResult<T>` and the `[Noul]`, `[Choice]` and `[Score]` attributes), the collection verbs and their types, the `DecisionsDotNet.Pipeline` namespace, the guardrail and workflow helpers in `DecisionsDotNet.AI`, and everything in `DecisionsDotNet.Testing` and `DecisionsDotNet.Caching` | New in 1.1 and exercised only against recorded responses so far; the shape may change after real use. |

To use them, add `<NoWarn>$(NoWarn);DECISIONS002</NoWarn>` to your project file, or wrap the code in `#pragma warning disable DECISIONS002`. `DECISIONS001` and `DECISIONS003` are retired; suppressions of them can be removed.

### Prereleases

1.1.0-preview.1 (2026-11-20) introduced the conditional layer with everything under `DECISIONS002`. 1.1.0-preview.2 (2026-11-21) added the Foundry and Azure OpenAI clients under `DECISIONS003`. Both are folded into the notes above.

## 1.0.0 (2026-11-15)

The first stable release. It renames the remaining `Jev` types, makes a few shapes explicit, and brings `ExtractionsDotNet` level with the Decisions packages. Everything below is a change from 0.3.0-preview.

### DecisionsDotNet: migrating from 0.3

| 0.3.0-preview | 1.0.0 |
| --- | --- |
| `JevClient`, `JevClientOptions` | `TypeSafeDecisionsClient`, `TypeSafeDecisionsClientOptions` |
| `JevAnswer` | `DecisionsAnswer` |
| `JevApiException` | `DecisionsApiException` |
| `JevModel`, `JevModelList` | `TypeSafeModel`, `TypeSafeModelList` |
| `AddJevSystemOneClient` | `AddTypeSafeDecisionsClient` |
| `AddLocalJevSystemOneClient` | `AddLocalTypeSafeDecisionsClient` |
| `AddKeyedJevSystemOneClient` | `AddKeyedTypeSafeDecisionsClient` |
| `ServiceCollectionsExtensions` | `DecisionsServiceCollectionExtensions` |
| `new DecisionsRequest(state, questions)` | `new DecisionsRequest(state, questions, model)` |
| `.Build()` | `.Build(model)` |
| `DecisionsBatchOptions` without `Model` | `Model` is required |
| Assembly `JevDotNet.dll` | `DecisionsDotNet.dll` |

Breaking changes:

- **The model is always explicit.** Requests no longer default to `jev-latest`, which is only valid on the direct TypeSafe API. Pass `TypeSafeDecisionsClient.DefaultModel` for the old behaviour, or the model ID your provider expects.
- **`DecisionsApiException` derives from `HttpRequestException`**, not `Exception`. Its `StatusCode`, `ResponseBody`, and `Service` are unchanged. A `catch (HttpRequestException)` that used to miss API errors now catches them.
- **`OpenAIDecisionsClient.DefaultModel` is a static property**, not a `const`, so it can no longer be used in attribute arguments or other constant expressions.
- **Named HTTP clients** are now `DecisionsDotNet.TypeSafe`, `DecisionsDotNet.OpenRouter`, and `DecisionsDotNet.OpenAI`. This matters only if you configure them by name.
- **The assembly is renamed.** Rebuild anything compiled against 0.3; binding redirects will not bridge the two.

Added:

- `DecisionsClientOptions`, a shared base class for the retry settings on all three client options types. Property names are unchanged.
- `DecisionsResponse.TryGet(DecisionKey<T>, out answer)`.
- XML documentation for every public member, shipped in the package.

### DecisionsDotNet.AI

- Follows the renames above. Its own API is unchanged.
- Now requires `Microsoft.Extensions.AI.Abstractions` 10.0.0 or later, down from 10.10.0.
- Assembly renamed from `JevDotNet.AI.dll` to `DecisionsDotNet.AI.dll`.

### ExtractionsDotNet: migrating from 0.3

Breaking changes:

- **The constructor and `FromEnvironment` take options, not a `Uri`.** Replace `new OpenRouterExtractionsClient(key, http, baseUri)` with `new OpenRouterExtractionsClient(key, http, new OpenRouterExtractionsClientOptions { BaseUri = baseUri })`.
- **The client retries.** HTTP 429, 502, 503, and 529 are retried up to three attempts in total, honoring `Retry-After`. Set `MaxAttempts = 1` for the old single attempt.
- **`ExtractionsApiException.StatusCode` is a non-nullable `HttpStatusCode`.** Patterns such as `{ StatusCode: { } status }` become `{ StatusCode: var status }`.
- **The package depends on `Microsoft.Extensions.Http`** 10.0.0 or later.

Added:

- `IExtractionsClient`, for injection and test doubles.
- `AddOpenRouterExtractionsClient` and `AddKeyedOpenRouterExtractionsClient`.
- Answer accessors on `ExtractionResponse`: `GetText`, `GetNumber`, `GetDate`, `GetTime`, `GetDateTime`, `GetDuration`, `GetList<T>`, `GetEstimate<T>`, `GetForecast<T>`, `GetAnswer<TAnswer>`, and `TryGetAnswer<TAnswer>`.
- `ExtractionsApiException.Service`.
- XML documentation for every public member, shipped in the package.

### Experimental APIs in 1.0.0

Members marked `[Experimental]` produce a compiler error until you suppress their diagnostic, and may change in a minor release. See the 1.1.0 notes for the current list.

| Diagnostic | Members | Why |
| --- | --- | --- |
| `DECISIONS001` | `OpenAIDecisionsClient`, `OpenAIDecisionsClientOptions`, `AddOpenAIDecisionsClient`, `AddKeyedOpenAIDecisionsClient` | Verified against recorded response shapes, not yet against the live OpenAI Decisions API. |
| `DECISIONS003` | `FoundryDecisionsClient`, `AzureOpenAIDecisionsClient`, their options, and their `Add*` registrations (added after 1.0.0) | Verified against the documented request shapes, not yet against the live Foundry and Azure OpenAI APIs. |
| `DECISIONS002` | `DecisionModel`, its verbs, result types, decision sets and collection verbs, the `DecisionsDotNet.Pipeline` namespace, the guardrail and workflow helpers in `DecisionsDotNet.AI`, and everything in `DecisionsDotNet.Testing` and `DecisionsDotNet.Caching` (added after 1.0.0) | New in 1.1; the shape may change after real use. |

To use them, add `<NoWarn>$(NoWarn);DECISIONS001</NoWarn>` to your project file, or wrap the code in `#pragma warning disable DECISIONS001`.

### Upstream APIs

OpenRouter serves both Decisions and Extractions under `/api/alpha/`, and TypeSafe labels image input on its direct API as experimental. Changes those providers make to their wire formats are handled in minor or patch releases of these packages.

## 0.x previews

Preview releases up to 0.3.0-preview. See the Git history for details.
