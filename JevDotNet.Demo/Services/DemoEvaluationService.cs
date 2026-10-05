using JevDotNet;

namespace JevDotNet.Demo.Services;

public sealed class DemoEvaluationService(IConfiguration configuration, IDecisionsClient? decisionsClient = null)
{
    public bool HasApiKey => decisionsClient is not null &&
        !string.IsNullOrWhiteSpace(configuration["OpenRouter:ApiKey"]);

    public Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            throw new InvalidOperationException("Configure the OpenRouter:ApiKey user secret before evaluating.");

        return decisionsClient!.EvaluateAsync(request, cancellationToken);
    }
}
