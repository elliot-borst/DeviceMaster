using DeviceMaster.Sensors;

namespace DeviceMaster.Core.Tests;

/// <summary>
/// Tests for PresentMonFpsReader.SelectFpsPid — the pure "which process's FPS do we report" logic.
/// Behaviour: report whatever is RENDERING MOST (focus-agnostic); the compositor/shell are excluded
/// so a real app always wins, but dwm.exe is the never-blank last resort so an idle desktop still
/// shows a number (the monitor refresh rate).
/// </summary>
public class PresentMonFpsSelectionTests
{
    private const long Now = 1_000_000;

    // A queue of `count` frames each `ms` apart, the most recent `ageMs` ago.
    private static Queue<(long Tick, double Ms)> Frames(int count, double ms, long ageMs = 0)
    {
        var q = new Queue<(long, double)>();
        for (var i = count - 1; i >= 0; i--)
        {
            q.Enqueue((Now - ageMs - (i * (long)ms), ms));
        }

        return q;
    }

    private const int Game = 17976;
    private const int Dwm = 856;

    private static (Dictionary<int, Queue<(long Tick, double Ms)>> byPid, Dictionary<int, string> names) Composed()
    {
        // game and dwm both presenting ~115 fps, as captured in "Composed: Flip" mode
        var byPid = new Dictionary<int, Queue<(long Tick, double Ms)>>
        {
            [Game] = Frames(60, 8.7),
            [Dwm] = Frames(60, 8.7),
        };
        var names = new Dictionary<int, string> { [Game] = "StarCitizen.exe", [Dwm] = "dwm.exe" };
        return (byPid, names);
    }

    [Fact]
    public void Busiest_real_app_wins_over_the_compositor_at_the_same_rate()
    {
        var (byPid, names) = Composed();
        Assert.Equal(Game, PresentMonFpsReader.SelectFpsPid(byPid, names, Now));
    }

    [Fact]
    public void Compositor_is_the_last_resort_when_nothing_app_like_presents()
    {
        // idle desktop: only dwm composites — report its rate (the monitor refresh) instead of blanking
        var byPid = new Dictionary<int, Queue<(long Tick, double Ms)>> { [Dwm] = Frames(60, 8.7) };
        var names = new Dictionary<int, string> { [Dwm] = "dwm.exe" };
        Assert.Equal(Dwm, PresentMonFpsReader.SelectFpsPid(byPid, names, Now));
    }

    [Fact]
    public void Selection_ignores_focus_and_follows_the_busiest_app()
    {
        // the shell is present too, but a real app renders — it wins regardless of what is focused
        var (byPid, names) = Composed();
        byPid[999] = Frames(60, 16.6);
        names[999] = "explorer.exe"; // shell — excluded from the app pick
        Assert.Equal(Game, PresentMonFpsReader.SelectFpsPid(byPid, names, Now));
    }

    [Fact]
    public void Between_two_apps_the_one_rendering_more_frames_wins()
    {
        var byPid = new Dictionary<int, Queue<(long Tick, double Ms)>>
        {
            [Game] = Frames(30, 8.7),  // fewer fresh frames
            [5555] = Frames(60, 8.7),  // busier app
        };
        var names = new Dictionary<int, string> { [Game] = "StarCitizen.exe", [5555] = "OtherGame.exe" };
        Assert.Equal(5555, PresentMonFpsReader.SelectFpsPid(byPid, names, Now));
    }

    [Fact]
    public void Nothing_presenting_returns_none()
    {
        var byPid = new Dictionary<int, Queue<(long Tick, double Ms)>>
        {
            [Game] = Frames(60, 8.7, ageMs: 10_000), // every frame stale (older than the freshness window)
        };
        var names = new Dictionary<int, string> { [Game] = "StarCitizen.exe" };
        Assert.Equal(0, PresentMonFpsReader.SelectFpsPid(byPid, names, Now));
    }

    [Fact]
    public void Our_own_process_is_never_reported_even_as_the_last_resort()
    {
        var byPid = new Dictionary<int, Queue<(long Tick, double Ms)>> { [4242] = Frames(60, 8.7) };
        var names = new Dictionary<int, string> { [4242] = "DeviceMaster.exe" };
        Assert.Equal(0, PresentMonFpsReader.SelectFpsPid(byPid, names, Now));
    }
}
