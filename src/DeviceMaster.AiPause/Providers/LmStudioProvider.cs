using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeviceMaster.AiPause.Providers;

public sealed record LmStudioModel(string Identifier, string ModelKey, int? ContextLength, long? TtlSeconds);

public sealed record LmStudioRecord(List<LmStudioModel> Models);

/// <summary>
/// LM Studio through its <c>lms</c> CLI: <c>lms ps --json</c> to capture, <c>lms unload</c> per
/// instance, <c>lms load &lt;key&gt; --identifier --context-length --ttl</c> to restore. This restores
/// the identifier, context length and TTL; other per-load settings come back at LM Studio's
/// defaults (GamePause uses LM Studio's internal protocol for those — not ported). A closed LM
/// Studio is never contacted, because the CLI would start it in the background.
/// </summary>
public sealed class LmStudioProvider(AiPauseSettings settings) : IAiProvider
{
    public string Id => "lmstudio";

    public string DisplayName => "LM Studio";

    public bool IsPresent => Running || ResolveLms() is not null;

    private static bool Running =>
        PathUtil.IsRunning("LM Studio") || PathUtil.IsRunning("llmster");

    public string Describe() => Running ? "Ready" : "Not running";

    public async Task<CaptureResult?> CaptureAsync(CancellationToken token)
    {
        if (!Running)
        {
            return null;
        }

        var models = LmStudioPlan.ParsePs(await LmsAsync(["ps", "--json"], TimeSpan.FromSeconds(30), token));
        if (models.Count == 0)
        {
            return null;
        }

        return new CaptureResult(Json.ToElement(new LmStudioRecord(models)),
            $"{models.Count} model(s): {string.Join(", ", models.Select(m => m.Identifier))}", null);
    }

    public async Task UnloadAsync(JsonElement record, CancellationToken token)
    {
        var models = Json.From<LmStudioRecord>(record).Models;
        foreach (var model in models)
        {
            await LmsAsync(["unload", model.Identifier], TimeSpan.FromMinutes(10), token);
        }

        var still = LmStudioPlan.ParsePs(await LmsAsync(["ps", "--json"], TimeSpan.FromSeconds(30), token))
            .Select(m => m.Identifier)
            .Intersect(models.Select(m => m.Identifier))
            .ToList();
        if (still.Count > 0)
        {
            throw new InvalidOperationException($"still loaded after unload: {string.Join(", ", still)}");
        }
    }

    public async Task RestoreAsync(JsonElement record, CancellationToken token)
    {
        var loaded = LmStudioPlan.ParsePs(await LmsAsync(["ps", "--json"], TimeSpan.FromSeconds(60), token))
            .Select(m => m.Identifier)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var model in Json.From<LmStudioRecord>(record).Models.Where(m => !loaded.Contains(m.Identifier)))
        {
            await LmsAsync(LmStudioPlan.LoadArguments(model), TimeSpan.FromMinutes(10), token);
        }
    }

    private string? ResolveLms()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        if (settings.LmsPath is { Length: > 0 } configured)
        {
            candidates.Add(configured);
        }

        candidates.Add(Path.Combine(profile, ".lmstudio", "bin", "lms.exe"));
        candidates.Add(Path.Combine(profile, ".cache", "lm-studio", "bin", "lms.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim(), "lms.exe"));
            }
            catch
            {
                // malformed PATH entry
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<string> LmsAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token)
    {
        var lms = ResolveLms() ?? throw new FileNotFoundException("LM Studio's lms CLI not found — install it from LM Studio, or set its path");
        var psi = new ProcessStartInfo(lms)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("lms failed to start");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"lms {args[0]} exited {process.ExitCode}: {(await stderr).Trim()}");
            }

            return await stdout;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            throw new TimeoutException($"lms {args[0]} timed out");
        }
    }
}

/// <summary>Pure <c>lms ps --json</c> parsing and load command construction.</summary>
public static class LmStudioPlan
{
    /// <summary>Every loaded instance; throws when one can't be identified (never unload what can't come back).</summary>
    public static List<LmStudioModel> ParsePs(string json)
    {
        var start = json.IndexOf('[');
        if (start < 0)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json[start..]);
        var result = new List<LmStudioModel>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var identifier = Str(m, "identifier");
            var key = Str(m, "modelKey") ?? Str(m, "path");
            if (identifier is null || key is null)
            {
                throw new InvalidDataException("lms ps returned a model without identifier/modelKey — refusing to unload");
            }

            var context = Int(m, "contextLength") ?? Int(m, "context_length");
            var ttlMs = Int(m, "ttlMs");
            result.Add(new LmStudioModel(identifier, key, context is > 0 ? (int)context : null,
                ttlMs is > 0 ? (long)Math.Ceiling(ttlMs.Value / 1000.0) : null));
        }

        return result;
    }

    public static IReadOnlyList<string> LoadArguments(LmStudioModel model)
    {
        var args = new List<string> { "load", model.ModelKey, "--identifier", model.Identifier, "--yes" };
        if (model.ContextLength is { } context)
        {
            args.AddRange(["--context-length", context.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (model.TtlSeconds is { } ttl)
        {
            args.AddRange(["--ttl", ttl.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return args;
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static long? Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
