using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace DeviceMaster.AiPause.Providers;

/// <summary>One model Ollama had resident when the pause began.</summary>
public sealed record OllamaModel(
    string Model,
    long? SizeVram,
    int? ContextLength,
    long? KeepAliveSeconds, // left at capture: seconds, -1 = indefinite, null = had already expired
    bool Reload);

public sealed record OllamaRecord(List<OllamaModel> Models);

/// <summary>
/// Ollama via its local HTTP API. Pause: list /api/ps, unload each model with keep_alive 0, then
/// wait until /api/ps no longer shows them (Ollama acknowledges at once but keeps a busy model
/// until its generation finishes). Restore: reload completion models with the observed context
/// and the keep-alive they had left — frozen during the pause, so a model with 4 of 5 minutes
/// left gets 4 minutes again. Embedding/unknown models are unloaded but not reloaded; cloud
/// models use no local VRAM and are left alone. If nothing answers, Ollama isn't running: no-op.
/// </summary>
public sealed class OllamaProvider(AiPauseSettings settings) : IAiProvider
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string Id => "ollama";

    public string DisplayName => "Ollama";

    public bool IsPresent =>
        PathUtil.IsRunning("ollama")
        || PathUtil.IsRunning("ollama app")
        || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Ollama\ollama.exe"));

    public string Describe() =>
        PathUtil.IsRunning("ollama") ? "Ready" : "Not running";

    private string Url(string path) => settings.OllamaEndpoint.TrimEnd('/') + path;

    public async Task<CaptureResult?> CaptureAsync(CancellationToken token)
    {
        string ps;
        try
        {
            ps = await GetAsync("/api/ps", TimeSpan.FromSeconds(3), token);
        }
        catch (HttpRequestException)
        {
            return null; // nothing listening — Ollama isn't running, not an error
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }

        var resident = OllamaPlan.ParseResident(ps, DateTimeOffset.UtcNow);
        if (resident.Count == 0)
        {
            return null;
        }

        var models = new List<OllamaModel>();
        foreach (var model in resident)
        {
            var capabilities = await CapabilitiesAsync(model.Model, token);
            models.Add(model with { Reload = model.Reload && capabilities.Contains("completion") && !capabilities.Contains("embedding") });
        }

        var bytes = models.Sum(m => m.SizeVram ?? 0);
        var names = string.Join(", ", models.Select(m => m.Model));
        return new CaptureResult(Json.ToElement(new OllamaRecord(models)), $"{models.Count} model(s): {names}", bytes > 0 ? bytes : null);
    }

    public async Task UnloadAsync(JsonElement record, CancellationToken token)
    {
        var models = Json.From<OllamaRecord>(record).Models;
        foreach (var model in models)
        {
            await PostAsync("/api/generate", new { model = model.Model, prompt = "", stream = false, keep_alive = 0 },
                TimeSpan.FromSeconds(30), token);
        }

        // unloading waits for running inference — give a busy model up to 10 minutes
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (true)
        {
            var still = OllamaPlan.ParseResident(await GetAsync("/api/ps", TimeSpan.FromSeconds(5), token), DateTimeOffset.UtcNow)
                .Select(m => m.Model)
                .Intersect(models.Select(m => m.Model))
                .ToList();
            if (still.Count == 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"still loaded after 10 min (busy?): {string.Join(", ", still)}");
            }

            await Task.Delay(1000, token);
        }
    }

    public async Task RestoreAsync(JsonElement record, CancellationToken token)
    {
        foreach (var model in Json.From<OllamaRecord>(record).Models.Where(m => m.Reload && m.KeepAliveSeconds is not null))
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = model.Model,
                ["prompt"] = "",
                ["stream"] = false,
                ["keep_alive"] = model.KeepAliveSeconds,
            };
            if (model.ContextLength is { } ctx)
            {
                body["options"] = new Dictionary<string, object> { ["num_ctx"] = ctx };
            }

            await PostAsync("/api/generate", body, TimeSpan.FromMinutes(5), token);
        }
    }

    private async Task<HashSet<string>> CapabilitiesAsync(string model, CancellationToken token)
    {
        try
        {
            var body = await PostAsync("/api/show", new { model }, TimeSpan.FromSeconds(10), token);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
            {
                return caps.EnumerateArray().Select(c => c.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // unknown capabilities ⇒ unload but don't reload
        }

        return [];
    }

    private async Task<string> GetAsync(string path, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        using var response = await Http.GetAsync(Url(path), cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token);
    }

    private async Task<string> PostAsync(string path, object body, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        using var response = await Http.PostAsJsonAsync(Url(path), body, cts.Token);
        var text = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{path} → {(int)response.StatusCode}: {text.Trim()}");
        }

        return text;
    }
}

/// <summary>Pure /api/ps parsing and keep-alive arithmetic.</summary>
public static class OllamaPlan
{
    /// <summary>Anything this far out is Ollama's encoding of keep_alive -1 (it reports ~292 years).</summary>
    private static readonly TimeSpan Indefinite = TimeSpan.FromDays(365 * 50);

    public static List<OllamaModel> ParseResident(string psJson, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(psJson);
        var result = new List<OllamaModel>();
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var m in models.EnumerateArray())
        {
            var name = Str(m, "model") ?? Str(m, "name");
            if (name is null || Str(m, "remote_host") is { Length: > 0 })
            {
                continue; // cloud-backed: no local VRAM to free
            }

            long? keepAlive = null;
            if (Str(m, "expires_at") is { } expiresText
                && DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires))
            {
                var left = expires - now;
                keepAlive = left > Indefinite ? -1 : left > TimeSpan.Zero ? (long)Math.Ceiling(left.TotalSeconds) : null;
            }

            result.Add(new OllamaModel(
                name,
                Positive(Long(m, "size_vram")) ?? Positive(Long(m, "size")),
                Positive(Long(m, "context_length")) is { } ctx ? (int)ctx : null,
                keepAlive,
                Reload: keepAlive is not null));
        }

        return result;
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Positive(long? n) => n > 0 ? n : null;

    private static long? Long(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
