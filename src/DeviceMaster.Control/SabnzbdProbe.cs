using System.Globalization;
using System.Text.Json;

namespace DeviceMaster.Control;

/// <summary>SABnzbd's queue as the fleet display shows it (#49).</summary>
public sealed record SabnzbdStatus
{
    public bool Reachable { get; init; }
    public string? Error { get; init; }
    public string Status { get; init; } = "";
    public bool Paused { get; init; }
    public double KBps { get; init; }
    public string SizeLeft { get; init; } = "";
    public string TimeLeft { get; init; } = "";
    public int Items { get; init; }
    public string? Current { get; init; }

    /// <summary>Parses SABnzbd's <c>mode=queue&amp;output=json</c> answer (public for tests).</summary>
    public static SabnzbdStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var q = doc.RootElement.GetProperty("queue");
        string S(string name) => q.TryGetProperty(name, out var v) ? v.ToString() : "";
        double.TryParse(S("kbpersec"), NumberStyles.Float, CultureInfo.InvariantCulture, out var kbps);
        int.TryParse(S("noofslots"), out var items);
        string? current = null;
        if (q.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array && slots.GetArrayLength() > 0
            && slots[0].TryGetProperty("filename", out var fn))
        {
            current = fn.GetString();
        }

        return new SabnzbdStatus
        {
            Reachable = true,
            Status = S("status"),
            Paused = S("paused").Equals("true", StringComparison.OrdinalIgnoreCase),
            KBps = kbps,
            SizeLeft = S("sizeleft"),
            TimeLeft = S("timeleft"),
            Items = items,
            Current = current,
        };
    }
}

/// <summary>Polls SABnzbd's API for the display; the key is read from a file (never stored in config).</summary>
public sealed class SabnzbdProbe(string baseUrl, string apiKeyFile, HttpClient http)
{
    public async Task<SabnzbdStatus> FetchAsync(CancellationToken token)
    {
        try
        {
            var key = (await File.ReadAllTextAsync(apiKeyFile, token)).Trim();
            var url = $"{baseUrl.TrimEnd('/')}/api?mode=queue&output=json&limit=1&apikey={Uri.EscapeDataString(key)}";
            return SabnzbdStatus.Parse(await http.GetStringAsync(url, token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            return new SabnzbdStatus { Reachable = false, Error = ex is TaskCanceledException ? "no answer" : ex.Message.Split('?')[0] }; // never echo the key
        }
    }
}
