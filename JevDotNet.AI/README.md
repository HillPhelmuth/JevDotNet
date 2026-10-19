# DecisionsDotNet.AI

Turns any DecisionsDotNet `IDecisionsClient` (TypeSafe Jev, OpenRouter, or OpenAI Decisions) into a Microsoft.Extensions.AI `AIFunction`, so a chat client or a Microsoft Agent Framework agent can call a calibrated classifier as a tool.

```powershell
dotnet add package DecisionsDotNet.AI --prerelease
```

## A fixed set of questions

Use `AsAIFunction` when the app knows which decisions it needs and the agent only decides when to ask. The model supplies the text:

```csharp
using DecisionsDotNet;
using DecisionsDotNet.AI;

var triage = decisionsClient.AsAIFunction(
    name: "triage_ticket",
    description: "Scores a support ticket for refund intent, anger, and the team that should own it.",
    questions: DecisionsRequest.Create("unused")
        .Noul("refund", "Does the customer want money back?")
        .Noul("angry", "Is the customer angry or frustrated?")
        .Choice("team", "Which team should handle this?", "billing", "technical", "account", "other")
        .Questions,
    model: "typesafe/jev-1.14");

AIAgent agent = new ChatClientAgent(chatClient,
    instructions: "You help support leads. Call triage_ticket before recommending how to handle a ticket.",
    tools: [triage]);
```

## Questions the model writes

`AsOpenDecisionsAIFunction` lets the model ask its own yes/no, choice, and score questions about any text:

```csharp
var decide = decisionsClient.AsOpenDecisionsAIFunction(model: OpenAIDecisionsClient.DefaultModel);
var response = await chatClient.GetResponseAsync(messages, new ChatOptions { Tools = [decide] });
```

The tool takes `input` and an array of `questions`, each with an `id`, a `type` of `yes_no`, `choice`, or `score`, `instructions`, and `options` (choice) or `levels` (score, lowest first).

## What the model sees

Both tools return compact JSON keyed by question ID, rounded to three decimals:

```json
{
  "model": "jev-1.14.0",
  "answers": {
    "refund": { "type": "yes_no", "probability_yes": 0.912 },
    "team": { "type": "choice", "choice": "billing", "confidence": 0.71, "probabilities": { "billing": 0.83, "other": 0.17 } },
    "anger": { "type": "score", "score": 1.4, "most_likely_level": "Furious", "confidence": 0.5 }
  }
}
```

Refused questions (OpenAI only) appear as `{ "type": "refusal" }`. Invalid questions from the model throw `ArgumentException`, which Microsoft.Extensions.AI reports back to the model so it can correct the call.
