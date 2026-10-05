using System.Text.Json;

namespace JevDotNet.Demo.Services;

public sealed record LunaJudgeCacheEntry(string Key, LunaJudgeRequest Request, DateTimeOffset StartedAt,
    LunaJudgeResult? Result = null, string? Error = null);

/// <summary>Durable, content-addressed judgments. A file lock coalesces calls across app instances.</summary>
public sealed class LunaJudgeCache(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public async Task<StoredLunaJudgeResult> GetOrEvaluateAsync(LunaJudgeRequest request, ILunaJudgeClient? client,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var key = request.CacheKey;
        var path = Path.Combine(DirectoryPath, key + ".json");
        await using var fileLock = await AcquireLockAsync(Path.Combine(DirectoryPath, key + ".lock"), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            var entry = JsonSerializer.Deserialize<LunaJudgeCacheEntry>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
                ?? throw new JsonException("The stored Luna judgment is empty; it will not be requested again automatically.");
            if (entry.Key != key || entry.Request != request)
                throw new JsonException("The stored Luna judgment does not match this request; no LLM request was sent.");
            if (entry.Result is null)
                throw new InvalidOperationException(entry.Error ?? "This Luna request was already started but has no stored result. It will not be sent again automatically.");
            ValidateResult(entry.Result);
            return new(entry.Result, true);
        }
        if (client is null) throw new InvalidOperationException("Configure OpenRouter:ApiKey before creating a new Luna judgment.");

        var pending = new LunaJudgeCacheEntry(key, request, DateTimeOffset.UtcNow);
        // Persist the attempt before sending. Interrupted/failed calls are never automatically resubmitted.
        await SaveAsync(path, pending);
        try
        {
            var result = await client.EvaluateAsync(request, cancellationToken);
            ValidateResult(result);
            // Once a response arrives, persist it even if the viewer just cancelled or navigated away.
            await SaveAsync(path, pending with { Result = result });
            return new(result, false);
        }
        catch (Exception error)
        {
            await SaveAsync(path, pending with { Error = "The previous Luna attempt failed or was interrupted: " + error.Message + " No identical request will be sent automatically." });
            throw;
        }
    }

    private static void ValidateResult(LunaJudgeResult result)
    {
        var parsed = LunaJudgeAgent.Parse(result.RawResponse);
        if (parsed.Rating != result.Rating || parsed.Rationale != result.Rationale || string.IsNullOrWhiteSpace(result.Model) || result.Elapsed < TimeSpan.Zero)
            throw new JsonException("The stored Luna judgment contains inconsistent result data; no LLM request was sent.");
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
            { await Task.Delay(100, cancellationToken); }
        }
    }

    private static async Task SaveAsync(string path, LunaJudgeCacheEntry entry)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, entry, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
