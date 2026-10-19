namespace DecisionsDotNet;

/// <summary>Bounded retry settings shared by the direct TypeSafe and OpenRouter clients.</summary>
internal sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialDelay, TimeSpan MaxDelay, IReadOnlySet<int> RetryableStatusCodes)
{
    internal void Validate(string paramName)
    {
        if (MaxAttempts < 1 || InitialDelay < TimeSpan.Zero || MaxDelay < InitialDelay)
            throw new ArgumentException("Invalid retry settings.", paramName);
    }
}

internal static class DecisionsHttp
{
    /// <summary>Sends a request built fresh for each attempt and returns the successful response body.</summary>
    internal static async Task<string> SendAsync(HttpClient client, Func<HttpRequestMessage> createMessage,
        RetryPolicy policy, string service, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var message = createMessage();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return content;

            if (attempt >= policy.MaxAttempts || !policy.RetryableStatusCodes.Contains((int)response.StatusCode))
                throw new JevApiException(response.StatusCode, content, service);

            var backoff = TimeSpan.FromMilliseconds(Math.Min(
                policy.MaxDelay.TotalMilliseconds,
                policy.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ??
                (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : backoff);
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > policy.MaxDelay) delay = policy.MaxDelay;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
