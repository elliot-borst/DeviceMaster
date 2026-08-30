using System.Globalization;
using System.Text.RegularExpressions;

namespace DeviceMaster.Sensors;

/// <summary>One "throughput" journal line — the server emits it every ~5 s while running.</summary>
public sealed record NInferThroughput(
    double PrefillTokPerS, double DecodeTokPerS, int RunningRequests, int WaitingRequests);

/// <summary>One "[req N] done" journal line — the per-request completion summary.</summary>
public sealed record NInferRequestSummary(
    int Id, string Finish, int? PromptTokens, int? GenTokens, int? TtftMs,
    double? DecodeTokPerS, double? WallSeconds, double? SpeculativeAcceptPercent);

/// <summary>
/// Pure parsers for the NInfer server's journald output (streamed with <c>journalctl -o cat</c>).
/// Real line shapes:
/// <code>
/// [ts] [info] ninfer-serve: throughput interval=5.000s prefill=0.0tok/s decode=139.8tok/s running=1 prefilling=0 decode_ready=1 waiting=0 …
/// [ts] [info] ninfer-serve: [req 10] done finish=output_limit prompt=26793 gen=256 cache=0 reuse=root ttft=4080ms prefill=6597.2tok/s decode=83.2tok/s wall=7.15s host=568.97ms decode-host=178.7us/round wait=28769.4us/round speculative=mtp 2.41tok/round (47.0%)
/// </code>
/// Values are key=value tokens with unit suffixes. Keys are matched with a leading
/// (?&lt;![\w-]) guard so "host=" never matches inside "decode-host=", and "wait=" never
/// matches "waiting=" thanks to the trailing "=" anchor on the full key.
/// </summary>
public static class NInferJournal
{
    public static NInferThroughput? TryParseThroughput(string line)
    {
        if (!line.Contains("ninfer-serve: throughput ", StringComparison.Ordinal))
        {
            return null;
        }

        if (Num(line, "prefill") is not { } prefill
            || Num(line, "decode") is not { } decode
            || Num(line, "running") is not { } running
            || Num(line, "waiting") is not { } waiting)
        {
            return null;
        }

        return new NInferThroughput(prefill, decode, (int)running, (int)waiting);
    }

    public static NInferRequestSummary? TryParseRequest(string line)
    {
        var req = Regex.Match(line, @"ninfer-serve: \[req (\d+)\] done\b");
        if (!req.Success)
        {
            return null;
        }

        var finish = Regex.Match(line, @"(?<![\w-])finish=([\w-]+)");
        var speculative = Regex.Match(line, @"(?<![\w-])speculative=.*?\((\d+(?:\.\d+)?)%\)");
        return new NInferRequestSummary(
            Id: int.Parse(req.Groups[1].Value, CultureInfo.InvariantCulture),
            Finish: finish.Success ? finish.Groups[1].Value : "?",
            PromptTokens: (int?)Num(line, "prompt"),
            GenTokens: (int?)Num(line, "gen"),
            TtftMs: (int?)Num(line, "ttft"),
            DecodeTokPerS: Num(line, "decode"),
            WallSeconds: Num(line, "wall"),
            SpeculativeAcceptPercent: speculative.Success
                ? double.Parse(speculative.Groups[1].Value, CultureInfo.InvariantCulture)
                : null);
    }

    /// <summary>Numeric value of a key=value token, ignoring any unit suffix ("ms", "tok/s", …).</summary>
    private static double? Num(string line, string key)
    {
        var match = Regex.Match(line, $@"(?<![\w-]){Regex.Escape(key)}=(-?\d+(?:\.\d+)?)");
        return match.Success
            ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }
}
