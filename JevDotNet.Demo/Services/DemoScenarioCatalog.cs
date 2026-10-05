using JevDotNet;

namespace JevDotNet.Demo.Services;

public sealed record DemoScenario(
    string Slug,
    string Number,
    string Title,
    string Summary,
    string InputLabel,
    string SampleInput,
    string Pattern);

public sealed record DemoRecommendation(string Title, string Detail);

public static class DemoScenarioCatalog
{
    public const int MaxInputLength = 8000;

    public static IReadOnlyList<DemoScenario> All { get; } =
    [
        new("support", "01", "Support triage",
            "Route a customer message, spot urgency, and gauge frustration in one call.",
            "Customer message",
            "My card was charged twice and I still can't access my account. Can someone fix this today?",
            "Choice + Noul + Score"),
        new("content", "02", "Content review",
            "Compare a post with simple community rules and suggest a human review priority.",
            "Community post",
            "You've posted the same promotional link in every thread today. Please stop spamming the forum.",
            "Policy check + category + severity"),
        new("leads", "03", "Lead qualification",
            "Identify inquiry intent, estimate customer fit, and spot demo interest.",
            "Inbound inquiry",
            "I'm the operations lead at a 120-person SaaS company. We're comparing workflow tools and would like a demo next week.",
            "Intent + interest + fit")
    ];

    public static DemoScenario? Find(string slug)
        => All.FirstOrDefault(scenario => string.Equals(scenario.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public static DecisionsRequest BuildRequest(
        string slug, string input, string modelId = DemoDecisionModels.DefaultModelId)
    {
        modelId = DemoDecisionModels.RequireSupported(modelId);
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Enter some text to evaluate.", nameof(input));
        if (input.Length > MaxInputLength)
            throw new ArgumentException($"Keep input under {MaxInputLength:N0} characters.", nameof(input));

        return slug.ToLowerInvariant() switch
        {
            "support" => new DecisionsRequest(
                JevValue.FromObject(new { ticket = input }),
                new Dictionary<string, JevQuestion>
                {
                    ["team"] = new ChoiceQuestion("Which team should handle this customer message?",
                        new Dictionary<string, JevValue?>
                        {
                            ["billing"] = "Payments, invoices, and refunds",
                            ["technical"] = "Product bugs, outages, or access failures",
                            ["account"] = "Plans, profiles, and account changes",
                            ["other"] = "Anything outside these teams"
                        }),
                    ["urgent"] = new NoulQuestion("Does this customer message need a prompt response?",
                        new NoulCriteria("Explicitly time-sensitive or blocking the customer", "Can wait for the normal queue")),
                    ["frustration"] = new ScoreQuestion("How frustrated does the customer sound?",
                        new JevValue[] { "Calm", "Frustrated but civil", "Very upset" })
                }, modelId),
            "content" => new DecisionsRequest(
                JevValue.FromObject(new
                {
                    post = input,
                    community_rules = new[]
                    {
                        "No repeated unsolicited promotions or links.",
                        "No insults, threats, or targeted harassment."
                    }
                }),
                new Dictionary<string, JevQuestion>
                {
                    ["possible_violation"] = new NoulQuestion("Does the post itself potentially violate a community rule?",
                        new NoulCriteria("The post contains a potential violation", "The post does not contain a potential violation")),
                    ["category"] = new ChoiceQuestion("Which category best describes the post itself?",
                        new Dictionary<string, JevValue?>
                        {
                            ["spam"] = "Repeated unsolicited promotion or links",
                            ["harassment"] = "Insults, threats, or targeted abuse",
                            ["ordinary"] = "Ordinary discussion or criticism",
                            ["other"] = "Does not clearly fit the other categories"
                        }),
                    ["severity"] = new ScoreQuestion("How much attention might this post need from a moderator?",
                        new JevValue[] { "Routine", "Needs review", "Urgent human review" })
                }, modelId),
            "leads" => new DecisionsRequest(
                JevValue.FromObject(new
                {
                    inquiry = input,
                    ideal_customer_profile = "Business teams evaluating workflow software for a company with at least 50 employees."
                }),
                new Dictionary<string, JevQuestion>
                {
                    ["intent"] = new ChoiceQuestion("What is the primary intent of this inquiry?",
                        new Dictionary<string, JevValue?>
                        {
                            ["buying"] = "Evaluating or purchasing workflow software",
                            ["research"] = "General information gathering",
                            ["support"] = "Help with an existing product",
                            ["other"] = "Another intent"
                        }),
                    ["demo_interest"] = new NoulQuestion("Does the sender explicitly ask for a product demo?"),
                    ["fit"] = new ScoreQuestion("How closely does this inquiry match the ideal customer profile?",
                        new JevValue[] { "Little evidence of fit", "Some evidence of fit", "Strong evidence of fit" })
                }, modelId),
            _ => throw new ArgumentException("Unknown demo scenario.", nameof(slug))
        };
    }

    public static DemoRecommendation Recommend(string slug, JevResponse response)
        => slug.ToLowerInvariant() switch
        {
            "support" => RecommendSupport(response),
            "content" => RecommendContent(response),
            "leads" => RecommendLead(response),
            _ => throw new ArgumentException("Unknown demo scenario.", nameof(slug))
        };

    private static DemoRecommendation RecommendSupport(JevResponse response)
    {
        var team = response.GetAnswer<ChoiceAnswer>("team");
        var urgency = response.GetAnswer<NoulAnswer>("urgent");
        var frustration = response.GetAnswer<ScoreAnswer>("frustration");
        if (team.Confidence < 0.65)
            return new("Human review suggested", "Team confidence is below 0.65; let a person choose the route.");
        var priority = urgency.Noul >= 0.7 || frustration.Score >= 1.5 ? "priority" : "standard";
        return new($"Route to {team.Choice} · {priority}",
            "Demo logic: urgency of at least 0.70 or frustration of at least 1.5 raises priority.");
    }

    private static DemoRecommendation RecommendContent(JevResponse response)
    {
        var violation = response.GetAnswer<NoulAnswer>("possible_violation");
        var category = response.GetAnswer<ChoiceAnswer>("category");
        var severity = response.GetAnswer<ScoreAnswer>("severity");
        if (category.Confidence < 0.65)
            return new("Human review suggested", "Category confidence is below 0.65; no automated action is taken.");
        if (violation.Noul >= 0.7 && severity.Score >= 1.5)
            return new("Priority human review", "Demo logic: possible violation is at least 0.70 and severity is at least 1.5.");
        if (violation.Noul >= 0.4)
            return new("Human review suggested", "Demo logic: possible violation is at least 0.40. No enforcement action is taken.");
        return new("Low review priority", "Demo logic: possible violation is below 0.40. No enforcement action is taken.");
    }

    private static DemoRecommendation RecommendLead(JevResponse response)
    {
        var intent = response.GetAnswer<ChoiceAnswer>("intent");
        var interest = response.GetAnswer<NoulAnswer>("demo_interest");
        var fit = response.GetAnswer<ScoreAnswer>("fit");
        if (intent.Confidence < 0.65)
            return new("Manual qualification suggested", "Intent confidence is below 0.65; a person should review the inquiry.");
        if (intent.Choice == "buying" && interest.Noul >= 0.7 && fit.Score >= 1.5)
            return new("Sales follow-up suggested", "Demo logic: buying intent, demo interest of at least 0.70, and fit of at least 1.5.");
        return new("Standard follow-up", "Demo logic: the inquiry does not meet all three sales follow-up conditions.");
    }
}
