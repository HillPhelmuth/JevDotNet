namespace JevDotNet;

/// <summary>Evaluates typed Decisions requests through a configured provider.</summary>
public interface IDecisionsClient
{
    Task<JevResponse> EvaluateAsync(DecisionsRequest request, CancellationToken cancellationToken = default);
}
