using System.Net;
using System.Text.Json;
using DecisionsDotNet;

public sealed class BuilderChecks
{
    [Fact]
    public async Task BuildersCreateRequestsAndEvaluateDecisionsAsync()
    {
        var request = DecisionsRequest.Create(DecisionsValue.FromObject(new { message = "Help" }))
            .Noul("urgent", "Is this urgent?", out var urgent, "Time-sensitive", "Not urgent")
            .Noul("plain", "Plain?")
            .Choice("route", "Which team?", out var route, ("billing", "Payment questions"), ("other", null))
            .Choice("tone", "Which tone?", "calm", "angry")
            .Score("severity", "How severe?", out var severity, "Calm", "Angry")
            .Box("mug", "Find the mug", out var mug)
            .Build("jev-1.14.0");

        Assert.True(request.Model == "jev-1.14.0" && request.Questions.Count == 6, "Builder model and count");
        Assert.True(request.Questions["urgent"] is NoulQuestion { Criteria.True: not null, Criteria.False: not null }, "Builder Noul criteria");
        Assert.True(request.Questions["plain"] is NoulQuestion { Criteria: null }, "Builder Noul without criteria");
        Assert.True(request.Questions["route"] is ChoiceQuestion route1 && route1.Criteria["other"] is null &&
            route1.Criteria["billing"]!.AsText() == "Payment questions", "Builder described choice");
        Assert.True(request.Questions["tone"] is ChoiceQuestion tone && tone.Criteria.Keys.SequenceEqual(new[] { "calm", "angry" }) &&
            tone.Criteria.Values.All(value => value is null), "Builder plain choice");
        Assert.True(request.Questions["severity"] is ScoreQuestion { Criteria.Count: 2 }, "Builder score");
        Assert.True(request.Questions["mug"] is BoxQuestion, "Builder box");

        // The builder produces the same wire format as hand-built dictionaries.
        var json = JsonSerializer.SerializeToElement(request);
        Assert.True(json.GetProperty("questions").GetProperty("urgent").GetProperty("criteria").GetProperty("true").GetString() == "Time-sensitive",
            "Builder wire format");

        ExpectArgument(() => DecisionsRequest.Create("x").Noul("a", "A?").Noul("a", "Again?"), "Duplicate question ID");
        ExpectArgument(() => DecisionsRequest.Create("x").Noul(" ", "A?"), "Blank question ID");
        ExpectArgument(() => DecisionsRequest.Create("x").Choice("c", "C?", ("a", null), ("a", null)), "Duplicate choice option");
        ExpectArgument(() => DecisionsRequest.Create("x").Build(), "No questions");

        var response = JsonSerializer.Deserialize<DecisionsResponse>("""
            {"model":"jev-1.14.0","answers":{
              "urgent":{"type":"noul","noul":0.9},
              "route":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"other":0.2},"confidence":0.7},
              "severity":{"type":"score","score":0.5,"legend":{"0":"Calm","1":"Angry"},"probabilities":{"0":0.5,"1":0.5},"confidence":0.4},
              "mug":{"type":"box","box":{"top":0.1,"bottom":0.5,"left":0.2,"right":0.6},"confidence":0.8}},
             "usage":{"input_tokens":1,"output_tokens":1}}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(response.Get(urgent).Noul == 0.9 && response.Get(route).Choice == "billing" &&
            response.Get(severity).Score == 0.5 && response.Get(mug).Box.Right == 0.6, "Typed keys read answers");
        Assert.True(response.TryGetAnswer<NoulAnswer>("urgent", out var found) && found.Noul == 0.9, "TryGetAnswer hit");
        Assert.True(!response.TryGetAnswer<ChoiceAnswer>("urgent", out _) && !response.TryGetAnswer<NoulAnswer>("missing", out _),
            "TryGetAnswer type mismatch and miss");
        await Task.CompletedTask;
    }

    private static void ExpectArgument(Action action, string name)
    {
        try { action(); throw new Exception($"Expected argument error: {name}"); }
        catch (ArgumentException) { }
    }

}
