using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DecisionsDotNet;

/// <summary>Describes how to pack many items into a few Decisions requests that ask the same questions about each item.</summary>
public sealed class DecisionsBatchOptions<TItem>
{
    /// <summary>A unique, stable ID per item. It prefixes each question ID as <c>{itemId}_{questionKey}</c>.</summary>
    public required Func<TItem, string> ItemId { get; init; }

    /// <summary>Builds the shared state for one request from the items it contains, such as a JSON array of the items' text and IDs.</summary>
    public required Func<IReadOnlyList<TItem>, DecisionsValue> State { get; init; }

    /// <summary>The questions to ask about one item, keyed by a short name that is the same for every item.</summary>
    public required Func<TItem, IReadOnlyDictionary<string, DecisionsQuestion>> Questions { get; init; }

    public string Model { get; init; } = "jev-latest";

    /// <summary>Items per request, from 1 upward. Providers cap the questions per request, so size batches to fit.</summary>
    public int ItemsPerRequest { get; init; } = 10;

    /// <summary>When true (the default), a failed request is reported in its result and later batches still run.</summary>
    public bool ContinueOnError { get; init; } = true;
}

/// <summary>The outcome of one request in a batched evaluation.</summary>
public sealed class DecisionsBatchResult<TItem>
{
    private readonly Func<TItem, string> _itemId;

    internal DecisionsBatchResult(int number, IReadOnlyList<TItem> items, Func<TItem, string> itemId,
        TimeSpan elapsed, DecisionsResponse? response, Exception? error)
    {
        Number = number;
        Items = items;
        _itemId = itemId;
        Elapsed = elapsed;
        Response = response;
        Error = error;
    }

    /// <summary>The one-based request number.</summary>
    public int Number { get; }
    public IReadOnlyList<TItem> Items { get; }
    public TimeSpan Elapsed { get; }
    public DecisionsResponse? Response { get; }
    public Exception? Error { get; }

    public bool Succeeded => Response is not null;

    /// <summary>Reads one item's answer to the question with the given key.</summary>
    public TAnswer GetAnswer<TAnswer>(TItem item, string questionKey) where TAnswer : JevAnswer
        => (Response ?? throw new InvalidOperationException($"Request {Number} failed: {Error?.Message}", Error))
            .GetAnswer<TAnswer>(DecisionsBatch.QuestionId(_itemId(item), questionKey));
}

public static class DecisionsBatch
{
    /// <summary>The question ID used for an item's question in a batched request.</summary>
    public static string QuestionId(string itemId, string questionKey) => $"{itemId}_{questionKey}";

    /// <summary>
    /// Evaluates items in sequential requests of up to <see cref="DecisionsBatchOptions{TItem}.ItemsPerRequest"/> items,
    /// yielding each request's result as it completes.
    /// </summary>
    public static async IAsyncEnumerable<DecisionsBatchResult<TItem>> EvaluateBatchesAsync<TItem>(
        this IDecisionsClient client,
        IEnumerable<TItem> items,
        DecisionsBatchOptions<TItem> options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ItemsPerRequest < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "ItemsPerRequest must be at least 1.");

        cancellationToken.ThrowIfCancellationRequested();
        var all = new List<TItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            all.Add(item);
            var id = options.ItemId(item);
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                throw new ArgumentException("Every item needs a unique, nonblank ID.", nameof(items));
        }

        var number = 0;
        foreach (var chunk in all.Chunk(options.ItemsPerRequest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = BuildRequest(chunk, options);
            var stopwatch = Stopwatch.StartNew();
            DecisionsResponse? response = null;
            Exception? error = null;
            try
            {
                response = await client.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception caught) when (options.ContinueOnError)
            {
                error = caught;
            }
            stopwatch.Stop();
            yield return new DecisionsBatchResult<TItem>(++number, chunk, options.ItemId, stopwatch.Elapsed, response, error);
        }
    }

    private static DecisionsRequest BuildRequest<TItem>(IReadOnlyList<TItem> chunk, DecisionsBatchOptions<TItem> options)
    {
        var questions = new Dictionary<string, DecisionsQuestion>(StringComparer.Ordinal);
        foreach (var item in chunk)
        {
            var itemId = options.ItemId(item);
            foreach (var (key, question) in options.Questions(item))
            {
                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException("Question keys must be nonblank.", nameof(options));
                if (!questions.TryAdd(QuestionId(itemId, key), question))
                    throw new ArgumentException($"Question ID '{QuestionId(itemId, key)}' is ambiguous; adjust the item IDs or keys.", nameof(options));
            }
        }
        return new DecisionsRequest(options.State(chunk), questions, options.Model);
    }
}
