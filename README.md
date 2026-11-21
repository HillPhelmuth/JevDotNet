# DecisionsDotNet and ExtractionsDotNet

.NET 10 SDKs for calibrated decision and extraction models: TypeSafe AI's Jev models, the Decisions and Extractions APIs from OpenRouter and OpenAI, and Microsoft's decision models in Microsoft Foundry and Azure OpenAI.

| Package | What it does | Docs |
| --- | --- | --- |
| [DecisionsDotNet](https://www.nuget.org/packages/DecisionsDotNet) | Typed yes/no, choice, score, and bounding-box questions through TypeSafe, OpenRouter, OpenAI, Microsoft Foundry, or Azure OpenAI, with DI, batching, and bounded retries. | [README](DecisionsDotNet/README.md) |
| [DecisionsDotNet.Testing](https://www.nuget.org/packages/DecisionsDotNet.Testing) | A scripted fake and a recording wrapper for `IDecisionsClient`, for unit tests. | [README](DecisionsDotNet.Testing/README.md) |
| [DecisionsDotNet.Caching](https://www.nuget.org/packages/DecisionsDotNet.Caching) | Caches evaluations in `HybridCache` as a client pipeline decorator. | [README](DecisionsDotNet.Caching/README.md) |
| [DecisionsDotNet.AI](https://www.nuget.org/packages/DecisionsDotNet.AI) | Exposes any Decisions client as a Microsoft.Extensions.AI tool for chat clients and agents, plus chat guardrails and workflow routing. | [README](DecisionsDotNet.AI/README.md) |
| [ExtractionsDotNet](https://www.nuget.org/packages/ExtractionsDotNet) | Typed field extraction from text, images, and PDFs through OpenRouter. | [README](ExtractionsDotNet/README.md) |

```powershell
dotnet add package DecisionsDotNet
dotnet add package DecisionsDotNet.AI
dotnet add package ExtractionsDotNet
```

`DecisionsDotNet` and its add-ons (`.AI`, `.Testing`, `.Caching`) always share a version. `ExtractionsDotNet` versions on its own. See the [changelog](CHANGELOG.md) for releases and the migration from 0.3.

[docs/recipes.md](docs/recipes.md) shows the semantic conditional verbs in ASP.NET Core endpoint filters, FluentValidation rules, and MediatR behaviors.

## Demo

[JevDotNet.Demo](JevDotNet.Demo/README.md) is a Blazor Server app with support triage, image decisions, bulk triage, Jev as Judge, a chase game, and the Extractions Lab. It needs an OpenRouter API key:

```powershell
dotnet user-secrets set "OpenRouter:ApiKey" "<your-api-key>" --project JevDotNet.Demo
dotnet run --project JevDotNet.Demo
```

## Build and test

```powershell
dotnet test --project JevDotNet.Tests/JevDotNet.Tests.csproj
dotnet test --project JevDotNet.Demo.Tests/JevDotNet.Demo.Tests.csproj
dotnet pack DecisionsDotNet/DecisionsDotNet.csproj
```

`JevDotNet.Tests` covers the three packages and `JevDotNet.Demo.Tests` covers the demo app.

The xUnit v3 tests use fake HTTP handlers and need no API key. The direct client follows the [TypeSafe AI API reference](https://docs.typesafe.ai/api) and [model listing reference](https://docs.typesafe.ai/models).

Three checks run on every build of the packages:

- Every public member needs an XML doc comment.
- The public surface must match each package's `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. When you change public API on purpose, update `PublicAPI.Unshipped.txt` in the same commit; the IDE offers this as a quick fix on the RS0016 error.
- `dotnet pack` runs package validation.

Package versions live in `Directory.Build.props`.

### Live smoke tests

The tests in `LiveSmokeTests.cs` send real requests to each provider. They are skipped unless you set `LIVE_TESTS=1` and the provider's settings: `OPENROUTER_API_KEY`, `TYPESAFE_API_KEY`, or `OPENAI_API_KEY`; for Foundry, `FOUNDRY_ENDPOINT`, `FOUNDRY_DEPLOYMENT` and `FOUNDRY_API_KEY`; for Azure OpenAI, `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT` and `AZURE_OPENAI_API_KEY`. The Foundry tests also accept `FOUNDRY_DECISIONS_ENDPOINT`, `FOUNDRY_DECISIONS_DEPLOYMENT` and `FOUNDRY_DECISIONS_API_KEY`, and fall back to the Azure OpenAI key; `AZURE_OPENAI_APIKEY` works in place of `AZURE_OPENAI_API_KEY`. Each request costs a fraction of a cent.

```powershell
$env:LIVE_TESTS = "1"
$env:OPENROUTER_API_KEY = "<your-api-key>"
dotnet test --project JevDotNet.Tests/JevDotNet.Tests.csproj
```

### Checking the packages

`eng/verify-packages.sh` packs all three packages into `artifacts/nuget`, then builds and runs a small console app against those files instead of the project sources. It runs on every pull request.

## Releasing

A release is a tag on `main`. `DecisionsDotNet` and `DecisionsDotNet.AI` always ship together at one version; `ExtractionsDotNet` versions on its own.

1. Set `DecisionsVersion` or `ExtractionsVersion` in `Directory.Build.props`, update `CHANGELOG.md`, and merge to `main`.
2. Tag the merge commit `decisions-v<version>` or `extractions-v<version>` and push the tag. Tag `origin/main` after a fetch, so a local checkout that is behind cannot put the tag on an old commit:

```powershell
git fetch origin
git tag decisions-v1.0.0 origin/main
git push origin decisions-v1.0.0
```

For a stable release, also move the entries in each package's `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` in step 1.

The publish workflow fails early if the tag's version does not match `Directory.Build.props` or the tag is not on `main`. Otherwise it builds, tests, checks the packages, and pushes them to nuget.org. Running the workflow by hand from the Actions tab is a rehearsal that does everything except publish.

## License

[MIT](LICENSE)
