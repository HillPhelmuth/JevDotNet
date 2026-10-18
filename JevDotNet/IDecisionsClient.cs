namespace DecisionsDotNet;

/// <summary>Evaluates typed Decisions requests through a configured provider.</summary>
public interface IDecisionsClient
{
    Task<DecisionsResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default);
}
