using DeviceMaster.Sensors;

namespace DeviceMaster.Core.Tests;

/// <summary>
/// Tests for PresentMonFpsReader.SelectFpsPid — the pure "which process's FPS do we report" logic.
/// The scenarios mirror a real capture: a game and dwm.exe present at the same rate while composed,
/// and the reading must survive an alt-tab (foreground stops lining up with the presenting PID)
/// instead of blanking, which is what the earlier foreground-only reader did.
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
    public void Foreground_game_is_selected_over_dwm_presenting_at_the_same_rate()
    {
        var (byPid, names) = Composed();
        Assert.Equal(Game, PresentMonFpsReader.SelectFpsPid(byPid, names, foregroundPid: Game, lockedPid: 0, Now));
    }

    [Fact]
    public void Dwm_is_never_selected_even_when_it_is_the_foreground_and_the_only_presenter()
    {
        var byPid = new Dictionary<int, Queue<(long Tick, double Ms)>> { [Dwm] = Frames(60, 8.7) };
        var names = new Dictionary<int, string> { [Dwm] = "dwm.exe" };
        Assert.Equal(0, PresentMonFpsReader.SelectFpsPid(byPid, names, foregroundPid: Dwm, lockedPid: 0, Now));
    }

    [Fact]
    public void Locked_game_is_held_when_foreground_is_something_else_but_the_game_still_presents()
    {
        // Alt-tabbed to the desktop: foreground is explorer, but the game keeps rendering.
        var (byPid, names) = Composed();
        byPid[999] = Frames(60, 16.6); // explorer, foreground now
        names[999] = "explorer.exe";
        Assert.Equal(Game, PresentMonFpsReader.SelectFpsPid(byPid, names, foregroundPid: 999, lockedPid: Game, Now));
    }

    [Fact]
    public void Reacquires_the_game_after_the_foreground_pid_stops_lining_up_with_the_presenter()
    {
        // The failure this fixes: foreground window's PID isn't the presenting PID (a fullscreen
        // exclusive re-acquire / helper window), and we had no lock — still find the game.
        var (byPid, names) = Composed();
        Assert.Equal(Game, PresentMonFpsReader.SelectFpsPid(byPid, names, foregroundPid: 4242, lockedPid: 0, Now));
    }

    [Fact]
    public void Nothing_presenting_returns_none()
    {
        var (byPid, names) = Composed();
        // every frame is stale (older than the freshness window)
        var stale = new Dictionary<int, Queue<(long Tick, double Ms)>>
        {
            [Game] = Frames(60, 8.7, ageMs: 10_000),
        };
        Assert.Equal(0, PresentMonFpsReader.SelectFpsPid(stale, names, foregroundPid: Game, lockedPid: Game, Now));
    }

    [Fact]
    public void A_foreground_game_takes_over_from_a_previously_locked_game()
    {
        // Switching directly from one game to another follows the new foreground game.
        var (byPid, names) = Composed();
        const int other = 5555;
        byPid[other] = Frames(60, 6.9); // a second game, now foreground
        names[other] = "OtherGame.exe";
        Assert.Equal(other, PresentMonFpsReader.SelectFpsPid(byPid, names, foregroundPid: other, lockedPid: Game, Now));
    }
}
