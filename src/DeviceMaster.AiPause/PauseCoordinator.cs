namespace DeviceMaster.AiPause;

/// <summary>A recognized game instance seen on the latest scan.</summary>
public sealed record RunningGame(int Pid, long CreatedTicks, string Name, string Path, GameRule Rule)
{
    public (int, long) Key => (Pid, CreatedTicks);
}

public enum CoordinatorAction
{
    None,
    Pause,
    Restore,
}

public sealed record CoordinatorDecision(
    CoordinatorAction Action,
    IReadOnlyList<RunningGame> Triggering,
    IReadOnlyList<RunningGame> AskPending,
    TimeSpan? CountdownRemaining);

/// <summary>
/// The pause/restore decision, separated from all I/O so it can be unit-tested tick by tick.
///
/// Rules (after GamePause for Local AI):
/// - A game with rule On starts a pause while automation is on; Off games are ignored; Ask games
///   only pause once the user approves that exact running instance.
/// - Additional games share the session; alt-tabbing changes nothing (only process presence counts).
/// - After the last triggering game exits, a restore countdown starts; a new game cancels it.
/// - Turning automation off never strands paused AI: the running session waits for its games to
///   exit, then restores normally.
/// - Unknown detection (a failed scan) never restores and never starts an automatic pause, and
///   restarts the countdown in full once detection is back.
/// - Manual Pause holds until Resume. Resume while a game runs restores now and lets the listed
///   instances keep running alongside AI; any new game instance revokes that override.
/// </summary>
public sealed class PauseCoordinator
{
    private readonly HashSet<(int, long)> _askApproved = [];
    private readonly HashSet<(int, long)> _askDeclined = [];
    private readonly HashSet<(int, long)> _resumeOverride = [];
    private DateTime? _countdownUntil;
    private bool _resumeNow;

    public bool Automation { get; set; } = true;

    public TimeSpan RestoreDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>AI is currently held paused (a recovery journal exists). Owned by the service.</summary>
    public bool Holding { get; set; }

    public bool ManualHold { get; private set; }

    public bool HasResumeOverride => _resumeOverride.Count > 0;

    public void ManualPause()
    {
        ManualHold = true;
        _resumeOverride.Clear();
        _resumeNow = false;
    }

    /// <summary>Release a manual hold and restore now. <paramref name="allowRunning"/> are the game
    /// instances the user confirmed may keep running alongside AI.</summary>
    public void ManualResume(IEnumerable<RunningGame> allowRunning)
    {
        ManualHold = false;
        foreach (var game in allowRunning)
        {
            _resumeOverride.Add(game.Key);
        }

        _resumeNow = true;
    }

    public void ApproveAsk(IEnumerable<RunningGame> games)
    {
        foreach (var game in games)
        {
            _askApproved.Add(game.Key);
            _askDeclined.Remove(game.Key);
        }
    }

    public void DeclineAsk(IEnumerable<RunningGame> games)
    {
        foreach (var game in games)
        {
            _askDeclined.Add(game.Key);
        }
    }

    /// <param name="games">Recognized games on the latest scan, or null when detection failed.</param>
    public CoordinatorDecision Decide(DateTime now, IReadOnlyList<RunningGame>? games)
    {
        if (games is null)
        {
            _countdownUntil = null; // sleep/outage time never counts toward the restore delay
            _resumeNow = false;
            var manual = !Holding && ManualHold ? CoordinatorAction.Pause : CoordinatorAction.None;
            return new CoordinatorDecision(manual, [], [], null);
        }

        var present = games.Select(g => g.Key).ToHashSet();
        _askApproved.IntersectWith(present);
        _askDeclined.IntersectWith(present);
        _resumeOverride.IntersectWith(present);

        var onRule = games.Where(g => g.Rule == GameRule.On).ToList();
        var approved = games.Where(g => g.Rule == GameRule.Ask && _askApproved.Contains(g.Key)).ToList();
        var askPending = games
            .Where(g => g.Rule == GameRule.Ask && !_askApproved.Contains(g.Key) && !_askDeclined.Contains(g.Key))
            .ToList();

        // a game instance the override didn't cover revokes it entirely
        var candidates = (Automation || Holding ? onRule : new List<RunningGame>()).Concat(approved).ToList();
        if (candidates.Any(g => !_resumeOverride.Contains(g.Key)))
        {
            _resumeOverride.Clear();
        }

        var triggering = candidates.Where(g => !_resumeOverride.Contains(g.Key)).ToList();
        var wantHold = ManualHold || triggering.Count > 0;

        if (!Holding)
        {
            _countdownUntil = null;
            _resumeNow = false;
            return new CoordinatorDecision(wantHold ? CoordinatorAction.Pause : CoordinatorAction.None, triggering, askPending, null);
        }

        if (wantHold)
        {
            _countdownUntil = null;
            _resumeNow = false;
            return new CoordinatorDecision(CoordinatorAction.None, triggering, askPending, null);
        }

        if (_resumeNow)
        {
            _resumeNow = false;
            _countdownUntil = null;
            return new CoordinatorDecision(CoordinatorAction.Restore, triggering, askPending, null);
        }

        _countdownUntil ??= now + RestoreDelay;
        if (now >= _countdownUntil)
        {
            _countdownUntil = null;
            return new CoordinatorDecision(CoordinatorAction.Restore, triggering, askPending, null);
        }

        return new CoordinatorDecision(CoordinatorAction.None, triggering, askPending, _countdownUntil - now);
    }
}
