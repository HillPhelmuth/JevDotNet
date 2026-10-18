using DecisionsDotNet;
using JevDotNet;
using JevDotNet.Demo.Components;
using JevDotNet.Demo.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpClient(DecisionModelsRefreshService.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<DecisionModelsRefreshService>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["OpenRouter:ApiKey"]))
{
    builder.Services.AddOpenRouterDecisionsClient(builder.Configuration);
    builder.Services.AddScoped<ILunaTriageClient>(services =>
        new LunaTriageAgent(services.GetRequiredService<IConfiguration>()["OpenRouter:ApiKey"]!));
    builder.Services.AddScoped<ILunaJudgeClient>(services =>
        new LunaJudgeAgent(services.GetRequiredService<IConfiguration>()["OpenRouter:ApiKey"]!));
}
builder.Services.AddScoped<DemoEvaluationService>();
builder.Services.AddScoped<BulkTriageService>();
builder.Services.AddScoped<JudgeService>();
builder.Services.AddSingleton(services => new LunaJudgeCache(Path.Combine(
    services.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
    builder.Configuration["Judge:LunaCachePath"] ?? "Data/judge-luna-cache")));
builder.Services.AddScoped<JudgeComparisonService>();

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Judge:RunComparison"))
{
    // This command exits before the web host (and its hosted services) starts.
    using var modelsClient = app.Services.GetRequiredService<IHttpClientFactory>()
        .CreateClient(DecisionModelsRefreshService.HttpClientName);
    await DemoDecisionModels.RefreshAsync(modelsClient);
    using var scope = app.Services.CreateScope();
    await JudgeComparisonReport.RunAsync(scope.ServiceProvider.GetRequiredService<JudgeService>(),
        scope.ServiceProvider.GetRequiredService<JudgeComparisonService>(),
        Path.Combine(app.Environment.ContentRootPath, "Data", "judge-comparison-results.json"));
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/demo-images/{id}", (string id) => ImageDemoCatalog.Find(id) is { } image
    ? Results.File(image.Bytes.ToArray(), "image/png")
    : Results.NotFound());
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
