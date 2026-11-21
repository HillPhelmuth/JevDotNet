using DecisionsDotNet;
using ExtractionsDotNet;

/// <summary>
/// One real request per provider. These spend a fraction of a cent each, so they are skipped unless
/// <c>LIVE_TESTS=1</c> is set along with the provider's usual API key variable.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveSmokeTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenRouterDecisionsAnswersAYesNoQuestionAsync()
    {
        RequireLive("OPENROUTER_API_KEY");
        using var client = OpenRouterDecisionsClient.FromEnvironment();
        var models = await client.ListModelsAsync(TestCancellation);
        Assert.NotEmpty(models);

        var response = await client.EvaluateAsync(Ticket().Build(models[0].Id), TestCancellation);

        AssertUrgent(response);
    }

    [Fact]
    public async Task TypeSafeDecisionsAnswersAYesNoQuestionAsync()
    {
        RequireLive("TYPESAFE_API_KEY");
        using var client = TypeSafeDecisionsClient.FromEnvironment();

        var response = await client.EvaluateAsync(Ticket().Build(TypeSafeDecisionsClient.DefaultModel), TestCancellation);

        AssertUrgent(response);
    }

    [Fact]
    public async Task OpenAIDecisionsAnswersAYesNoQuestionAsync()
    {
        RequireLive("OPENAI_API_KEY");
        using var client = OpenAIDecisionsClient.FromEnvironment();

        var response = await client.EvaluateAsync(Ticket().Build(OpenAIDecisionsClient.DefaultModel), TestCancellation);

        AssertUrgent(response);
    }

    [Fact]
    public async Task FoundryDecisionsAnswersAYesNoQuestionAsync()
    {
        RequireLive("FOUNDRY_API_KEY", "FOUNDRY_DECISIONS_API_KEY", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_APIKEY");
        using var client = new FoundryDecisionsClient(new Uri(Required("FOUNDRY_ENDPOINT", "FOUNDRY_DECISIONS_ENDPOINT")),
            Required("FOUNDRY_DEPLOYMENT", "FOUNDRY_DECISIONS_DEPLOYMENT"), Required("FOUNDRY_API_KEY", "FOUNDRY_DECISIONS_API_KEY", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_APIKEY"));

        var response = await client.EvaluateAsync(Ticket().Build(client.DeploymentName), TestCancellation);

        AssertUrgent(response);
    }

    [Fact]
    public async Task AzureOpenAIDecisionsAnswersAYesNoQuestionAsync()
    {
        RequireLive("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_APIKEY");
        using var client = new AzureOpenAIDecisionsClient(new Uri(Required("AZURE_OPENAI_ENDPOINT")), Required("AZURE_OPENAI_DEPLOYMENT"),
            Required("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_APIKEY"));

        var response = await client.EvaluateAsync(Ticket().Build(client.DeploymentName), TestCancellation);

        AssertUrgent(response);
    }

    [Fact]
    public async Task OpenRouterExtractionsReadsATextFieldAsync()
    {
        RequireLive("OPENROUTER_API_KEY");
        using var client = OpenRouterExtractionsClient.FromEnvironment();
        var models = await client.ListModelsAsync(TestCancellation);
        var model = Assert.IsType<ExtractionModel>(models.FirstOrDefault(m => m.InputModalities.Contains("text")));

        var response = await client.ExtractAsync(new ExtractionRequest(model.Id,
            new Dictionary<string, ExtractionField> { ["invoice_number"] = new TextField() })
        {
            State = "Invoice INV-20418 from Northwind Traders, due 30 November 2026, total 1,250.00 USD."
        }, TestCancellation);

        Assert.Contains("20418", response.GetText("invoice_number").Value);
    }

    private const string Outage = "Our checkout page has been down for an hour and customers cannot pay.";

    private static async Task<DecisionModel> OpenRouterModelAsync(OpenRouterDecisionsClient client)
    {
        var models = await client.ListModelsAsync(TestCancellation);
        Assert.NotEmpty(models);
        return client.ForModel(models[0].Id);
    }

    [Fact]
    public async Task MatchesAndProbabilityAnswerLiveAsync()
    {
        RequireLive("OPENROUTER_API_KEY");
        using var client = OpenRouterDecisionsClient.FromEnvironment();
        var model = await OpenRouterModelAsync(client);

        Assert.True(await model.MatchesAsync(Outage, "Does this need attention right now?", minProbability: 0.5, cancellationToken: TestCancellation));
        Assert.InRange(await model.ProbabilityAsync(Outage, "Is this a question about invoices?", cancellationToken: TestCancellation), 0.0, 0.5);
    }

    [Fact]
    public async Task VerdictAnswersLiveAsync()
    {
        RequireLive("OPENROUTER_API_KEY");
        using var client = OpenRouterDecisionsClient.FromEnvironment();
        var verdict = await (await OpenRouterModelAsync(client)).VerdictAsync(Outage, "Does this need attention right now?",
            (0.3, 0.7), cancellationToken: TestCancellation);

        Assert.Equal(VerdictOutcome.Yes, verdict.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Response.Model));
    }

    [Fact]
    public async Task ChooseAnswersLiveAsync()
    {
        RequireLive("OPENROUTER_API_KEY");
        using var client = OpenRouterDecisionsClient.FromEnvironment();
        var route = await (await OpenRouterModelAsync(client)).ChooseAsync<Team>(Outage, "Which team should handle this request?", TestCancellation);

        Assert.Equal(Team.Technical, route.Value);
        Assert.InRange(route.SelectedProbability, 0.0, 1.0);
    }

    [Fact]
    public async Task ScoreAnswersLiveAsync()
    {
        RequireLive("OPENROUTER_API_KEY");
        using var client = OpenRouterDecisionsClient.FromEnvironment();
        var score = await (await OpenRouterModelAsync(client)).ScoreAsync<Mood>(
            "This is the third time this week. I am furious and I want a manager now.", "How frustrated is the customer?", TestCancellation);

        Assert.NotEqual(Mood.Calm, score.Value);
    }

    private static DecisionsRequestBuilder Ticket() => DecisionsRequest
        .Create("Our checkout page has been down for an hour and customers cannot pay.")
        .Noul("urgent", "Does this need attention right now?");

    private static void AssertUrgent(DecisionsResponse response)
    {
        Assert.False(string.IsNullOrWhiteSpace(response.Model));
        Assert.InRange(response.GetAnswer<NoulAnswer>("urgent").Noul, 0.5, 1.0);
    }

    /// <summary>Reads the first of <paramref name="variables"/> that is set; later names are accepted aliases.</summary>
    private static string Required(params string[] variables)
    {
        var value = Read(variables);
        Assert.SkipWhen(value is null, $"Set {string.Join(" or ", variables)} to run this test.");
        return value!;
    }

    private static void RequireLive(params string[] keyVariables)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("LIVE_TESTS") == "1", "Set LIVE_TESTS=1 to run live smoke tests.");
        Assert.SkipWhen(Read(keyVariables) is null, $"Set {string.Join(" or ", keyVariables)} to run this test.");
    }

    private static string? Read(string[] variables) => variables
        .Select(Environment.GetEnvironmentVariable)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
