using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DeviceMaster.Sensors.Linux;

/// <summary>One serving slot (conversation) of a local LLM server.</summary>
public sealed record LlmSlot(int Id, string State, int HeldTokens);

/// <summary>What the dashboard shows about a local LLM server, from Strata's <c>/metrics</c>.</summary>
public sealed record LlmStatus(
    string Model,
    string State,
    double? TokPerSec,
    IReadOnlyList<LlmSlot> Slots,
    int Queued,
    long Requests,
    long OutputTokens,
    IReadOnlyList<double> TokHistory,
    double? GpuUtil,
    double? GpuTempC,
    double? GpuPowerW,
    double? GpuMemUsedMb,
    double? GpuMemTotalMb,
    string? GpuName,
    DateTime ReadAtUtc,
    int Running = 0,          // conversations being worked on right now (slots + outside them)
    int OutsideSlots = 0,     // single conversations run outside the batch slots (Strata's solo path)
    string? Phase = null,     // what the model is doing: "thinking", "answering", "writing a tool call: …"
    int? PromptTokens = null,
    double? PromptTokPerSec = null,   // average prompt-reading speed since start (cache reuse excluded)
    double? DraftHitPercent = null,   // speculative draft tokens accepted since start, 0–100
    double? CacheHitPercent = null,   // prompt tokens reused from the conversation cache since start, 0–100
    double? GpuPowerLimitW = null);

/// <summary>
/// Read-only client for a Strata (llama.cpp-style) server's JSON <c>/metrics</c>: model, live
/// state and speed, per-slot activity, request totals and the GPU figures Strata already samples.
/// Only ever GETs that one endpoint (never chat/completions), with the server's bearer key.
/// </summary>
public sealed class StrataStatus : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _metricsUrl;

    /// <param name="baseUrl">e.g. http://127.0.0.1:8081</param>
    /// <param name="apiKey">bearer key; null when the server needs none</param>
    public StrataStatus(string baseUrl, string? apiKey)
    {
        _metricsUrl = baseUrl.TrimEnd('/') + "/metrics";
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>Reads <c>API_KEY=…</c> (or <c>STRATA_API_KEY=…</c>) from an env-style file; null if absent.</summary>
    public static string? ReadKeyFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            foreach (var name in new[] { "API_KEY=", "STRATA_API_KEY=" })
            {
                if (trimmed.StartsWith(name, StringComparison.Ordinal))
                {
                    return trimmed[name.Length..].Trim().Trim('"', '\'');
                }
            }
        }

        return null;
    }

    public async Task<LlmStatus?> ReadAsync(CancellationToken token)
    {
        using var response = await _http.GetAsync(_metricsUrl, token);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return Parse(await response.Content.ReadAsStringAsync(token), DateTime.UtcNow);
    }

    /// <summary>Pure parser for the <c>/metrics</c> JSON (tolerant of missing fields).</summary>
    public static LlmStatus Parse(string json, DateTime nowUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var engine = Obj(root, "engine");
        var live = Obj(root, "live");
        var totals = Obj(root, "totals");
        var hw = Obj(root, "hardware");
        var hwStatic = Obj(root, "hardware_static");
        var history = Obj(root, "history");

        var slots = new List<LlmSlot>();
        if (live is { } l && l.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (var slot in s.EnumerateArray())
            {
                slots.Add(new LlmSlot((int)(Num(slot, "slot") ?? slots.Count), Str(slot, "state") ?? "?", (int)(Num(slot, "held_tokens") ?? 0)));
            }
        }

        var tokHistory = new List<double>();
        if (history is { } h && h.TryGetProperty("tok_s", out var th) && th.ValueKind == JsonValueKind.Array)
        {
            tokHistory.AddRange(th.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()));
        }

        return new LlmStatus(
            Model: (engine is { } e ? Str(e, "model") : null) ?? "model",
            State: (live is { } l2 ? Str(l2, "state") : null) ?? "unknown",
            TokPerSec: live is { } l3 ? Num(l3, "tok_s") : null,
            Slots: slots,
            Queued: (int)((live is { } l4 ? (Num(l4, "queued") ?? 0) + (Num(l4, "waiting") ?? 0) : 0)),
            Requests: (long)(totals is { } t ? Num(t, "requests") ?? 0 : 0),
            OutputTokens: (long)(totals is { } t2 ? Num(t2, "output_tokens") ?? 0 : 0),
            TokHistory: tokHistory,
            GpuUtil: hw is { } a ? Num(a, "gpu_util") : null,
            GpuTempC: hw is { } b ? Num(b, "gpu_temp") : null,
            GpuPowerW: hw is { } c ? Num(c, "gpu_power") : null,
            GpuMemUsedMb: hw is { } d && Num(d, "gpu_mem_used") is { } used ? used / 1048576.0 : null,
            GpuMemTotalMb: hw is { } f && Num(f, "gpu_mem_total") is { } total ? total / 1048576.0 : null,
            GpuName: hwStatic is { } g ? Str(g, "gpu_name") : null,
            ReadAtUtc: nowUtc,
            Running: (int)(live is { } l5 ? Num(l5, "running") ?? 0 : 0),
            OutsideSlots: (int)(live is { } l6 ? Num(l6, "outside_slots") ?? 0 : 0),
            Phase: live is { } l7 ? Str(l7, "phase") : null,
            PromptTokens: live is { } l8 && (Num(l8, "prompt_total") ?? Num(l8, "prompt_tokens")) is { } pt ? (int)pt : null,
            PromptTokPerSec: totals is { } t3 ? PromptSpeed(t3) : null,
            DraftHitPercent: totals is { } t4 && Num(t4, "drafts_offered") is { } offered && offered > 0
                ? 100.0 * (Num(t4, "drafts_accepted") ?? 0) / offered : null,
            CacheHitPercent: totals is { } t5 && Num(t5, "prompt_tokens") is { } prompted && prompted > 0
                ? 100.0 * (Num(t5, "reused") ?? 0) / prompted : null,
            GpuPowerLimitW: hw is { } k && Num(k, "gpu_power_limit") is { } limit && limit > 0 ? limit : null);
    }

    /// <summary>Prompt tokens actually read (not reused from the cache) per second of prompt time.</summary>
    private static double? PromptSpeed(JsonElement totals)
    {
        var ms = Num(totals, "prompt_ms") ?? 0;
        var read = (Num(totals, "prompt_tokens") ?? 0) - (Num(totals, "reused") ?? 0);
        return ms > 0 && read > 0 ? read / (ms / 1000.0) : null;
    }

    private static JsonElement? Obj(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public void Dispose() => _http.Dispose();
}
