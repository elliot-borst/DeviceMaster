using DeviceMaster.Sensors;

namespace DeviceMaster.Core.Tests;

public class NInferJournalTests
{
    // real journal lines captured from the running server (journalctl -o cat)
    private const string ThroughputLine =
        "[2026-08-30 21:44:30.704] [info] ninfer-serve: throughput interval=5.000s prefill=0.0tok/s "
        + "decode=139.8tok/s running=1 prefilling=0 decode_ready=1 waiting=0 materializing=0 "
        + "capture_pending=0 terminal_pending=0 avg_decode_batch=1.00 host=44.74ms "
        + "decode-host=168.2us/round wait=18644.5us/round boundary=0.17ms maintenance=0.11ms";

    private const string RequestLine =
        "[2026-08-29 20:13:27.990] [info] ninfer-serve: [req 10] done finish=output_limit prompt=26793 "
        + "gen=256 cache=0 reuse=root ttft=4080ms prefill=6597.2tok/s decode=83.2tok/s wall=7.15s "
        + "host=568.97ms decode-host=178.7us/round wait=28769.4us/round speculative=mtp 2.41tok/round (47.0%)";

    [Fact]
    public void ParsesThroughputLine()
    {
        var tp = NInferJournal.TryParseThroughput(ThroughputLine);
        Assert.NotNull(tp);
        Assert.Equal(0.0, tp.PrefillTokPerS);
        Assert.Equal(139.8, tp.DecodeTokPerS);
        Assert.Equal(1, tp.RunningRequests);
        Assert.Equal(0, tp.WaitingRequests);
    }

    [Fact]
    public void ThroughputWaitingIsNotConfusedWithWaitPerRound()
    {
        // the line also carries "wait=18644.5us/round" — waiting= must win for the queue depth
        var tp = NInferJournal.TryParseThroughput(
            ThroughputLine.Replace("waiting=0", "waiting=3"));
        Assert.NotNull(tp);
        Assert.Equal(3, tp.WaitingRequests);
    }

    [Fact]
    public void ParsesRequestLine()
    {
        var req = NInferJournal.TryParseRequest(RequestLine);
        Assert.NotNull(req);
        Assert.Equal(10, req.Id);
        Assert.Equal("output_limit", req.Finish);
        Assert.Equal(26793, req.PromptTokens);
        Assert.Equal(256, req.GenTokens);
        Assert.Equal(4080, req.TtftMs);
        Assert.Equal(83.2, req.DecodeTokPerS);
        Assert.Equal(7.15, req.WallSeconds);
        Assert.Equal(47.0, req.SpeculativeAcceptPercent);
    }

    [Fact]
    public void RequestWithoutSpeculativeSectionParsesWithNullAcceptance()
    {
        var line = RequestLine[..RequestLine.IndexOf(" speculative=", StringComparison.Ordinal)];
        var req = NInferJournal.TryParseRequest(line);
        Assert.NotNull(req);
        Assert.Null(req.SpeculativeAcceptPercent);
        Assert.Equal(83.2, req.DecodeTokPerS);
    }

    [Fact]
    public void OtherLinesParseAsNothing()
    {
        const string boot = "[2026-08-30 21:40:01.000] [info] ninfer-serve: loading weights from /models/…";
        Assert.Null(NInferJournal.TryParseThroughput(boot));
        Assert.Null(NInferJournal.TryParseRequest(boot));
        Assert.Null(NInferJournal.TryParseThroughput(RequestLine));
        Assert.Null(NInferJournal.TryParseRequest(ThroughputLine));
    }
}
