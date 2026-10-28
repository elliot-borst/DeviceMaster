using System.Collections.Concurrent;
using System.Diagnostics;
using DeviceMaster.AiPause.Providers;

namespace DeviceMaster.AiPause;

public enum AiPauseState
{
    /// <summary>Not holding AI paused — watching for games (or automation is off).</summary>
    Running,

    /// <summary>AI is paused for a game, by the user, or until the restore delay ends.</summary>
    Paused,

    /// <summary>Pausing or restoring right now.</summary>
    Busy,

    /// <summary>Detection failed, a pause/restore didn't finish, or saved AI is waiting to come back.</summary>
    Attention,
}

public sealed record ProviderLine(string Name, string Text, bool Error);

public sealed record LibraryGame(GameEntry Game, GameRule Rule, bool Running);

public sealed record ActivityLine(DateTime At, string Text);

/// <summary>Immutable view of everything the AI Pause page shows; safe to read from the UI thread.</summary>
public sealed record AiPauseSnapshot
{
    public AiPauseState State { get; init; }
    public string Headline { get; init; } = "AI RUNNING";
    public string Hint { get; init; } = "Watching for games.";
    public bool ManualHold { get; init; }
    public bool Holding { get; init; }
    public bool Automation { get; init; }
    public IReadOnlyList<RunningGame> RunningGames { get; init; } = [];
    public IReadOnlyList<RunningGame> AskPending { get; init; } = [];
    public TimeSpan? Countdown { get; init; }
    public long? FreedBytes { get; init; }
    public IReadOnlyList<ProviderLine> Providers { get; init; } = [];
    public IReadOnlyList<LibraryGame> Library { get; init; } = [];
    public IReadOnlyDictionary<string, string> DiscoveryErrors { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ActivityLine> Activity { get; init; } = [];
    public string? Suggestion { get; init; }
    public bool DetectionOk { get; init; } = true;
}

/// <summary>
/// AI Pause: frees GPU memory from local AI while you game and brings it back afterwards.
/// One background worker owns all decisions and provider calls; the UI only enqueues commands
/// and reads <see cref="Snapshot"/>. Idea and behaviour ported from GamePause for Local AI (MIT).
/// </summary>
public sealed class AiPauseService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DetectionAlarm = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SuggestAfter = TimeSpan.FromSeconds(10);

    private readonly string _settingsPath;
    private readonly string _journalPath;
    private readonly IReadOnlyList<IAiProvider> _providers;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly PauseCoordinator _coordinator = new();
    private readonly ProcessScanner _scanner = new();
    private readonly List<ActivityLine> _activity = [];
    private readonly Dictionary<string, ProviderLine> _providerNotes = [];
    private readonly Task _worker;

    private volatile AiPauseSnapshot _snapshot = new();
    private DiscoveryResult _discovery = new([], new Dictionary<string, string>());
    private GameMatcher? _matcher;
    private DateTime _discoveredAt = DateTime.MinValue;
    private bool _rediscover = true;
    private RecoveryJournal? _journal;
    private string? _journalError;
    private bool _busy;
    private string? _lastError;
    private DateTime _retryAt = DateTime.MinValue;
    private DateTime? _detectionDownSince;
    private IReadOnlyList<RunningGame> _running = [];
    private IReadOnlyList<ProcInfo> _processes = [];
    private CoordinatorDecision? _lastDecision;
    private (string Path, DateTime Since)? _fullscreenCandidate;
    private string? _suggestion;
    private bool _partiallyRestored;
    private readonly HashSet<string> _suggested = new(StringComparer.OrdinalIgnoreCase);

    public AiPauseSettings Settings { get; }

    public AiPauseSnapshot Snapshot => _snapshot;

    /// <summary>Raised on the worker thread for tray notifications (title, text).</summary>
    public event Action<string, string>? Notify;

    /// <param name="settings">Loaded from <c>ai-pause.json</c> in <paramref name="dataDirectory"/>; the
    /// providers share this same instance so setting changes reach them.</param>
    public AiPauseService(string dataDirectory, AiPauseSettings settings, IEnumerable<IAiProvider> providers, Action<string>? log)
    {
        _settingsPath = Path.Combine(dataDirectory, "ai-pause.json");
        _journalPath = Path.Combine(dataDirectory, "ai-pause-state.json");
        _providers = providers.ToList();
        _log = log;
        Settings = settings;
        _coordinator.Automation = Settings.AutomationEnabled;
        _coordinator.RestoreDelay = TimeSpan.FromSeconds(Settings.RestoreDelaySeconds);

        try
        {
            _journal = RecoveryJournal.Load(_journalPath);
            if (_journal is not null)
            {
                // providers that must stay off at launch (NInfer) are dropped, not restored
                foreach (var provider in _providers.Where(p => !p.RestoreOnLaunch && _journal.Providers.ContainsKey(p.Id)))
                {
                    _journal.Providers.Remove(provider.Id);
                    AddActivity($"{provider.DisplayName} was paused when DeviceMaster last closed — it stays off. Start it from Local LLM.");
                }

                if (_journal.Providers.Count == 0)
                {
                    RecoveryJournal.Delete(_journalPath);
                    _journal = null;
                }
                else
                {
                    _journal.Save(_journalPath);
                    _coordinator.Holding = true;
                    AddActivity($"Found saved AI from {_journal.PausedAt.LocalDateTime:g} — it will be restored once no game is running.");
                }
            }
        }
        catch (Exception ex)
        {
            // never delete an unreadable journal: it may be the only record of what to restore
            _journalError = $"Recovery file unreadable ({ex.Message}). Fix or remove {_journalPath}.";
            _coordinator.Holding = true;
        }

        Publish();
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    // ---------- commands (any thread) ----------

    public void PauseNow() => Enqueue(() =>
    {
        _coordinator.ManualPause();
        AddActivity("Pause AI requested.");
    });

    /// <summary>Resume now; running games are allowed to keep running alongside AI.</summary>
    public void ResumeNow() => Enqueue(() =>
    {
        _coordinator.ManualResume(_lastDecision?.Triggering ?? []);
        _retryAt = DateTime.MinValue;
        AddActivity(_running.Count > 0 ? "Resume AI requested while a game runs." : "Resume AI requested.");
    });

    public void AnswerAsk(bool pause, bool remember) => Enqueue(() =>
    {
        var pending = _lastDecision?.AskPending ?? [];
        if (pending.Count == 0)
        {
            return;
        }

        if (pause)
        {
            _coordinator.ApproveAsk(pending);
        }
        else
        {
            _coordinator.DeclineAsk(pending);
        }

        if (remember)
        {
            lock (_gate)
            {
                foreach (var game in pending)
                {
                    Settings.SetRule(game.Path, pause ? GameRule.On : GameRule.Off);
                }
            }

            SaveSettings();
            _rediscover = true;
        }

        AddActivity($"{(pause ? "Pausing" : "Not pausing")} for {string.Join(", ", pending.Select(g => g.Name).Distinct())}{(remember ? " (remembered)" : "")}.");
    });

    public void SetAutomation(bool on)
    {
        lock (_gate)
        {
            Settings.AutomationEnabled = on;
        }

        SaveSettings();
        Enqueue(() =>
        {
            _coordinator.Automation = on;
            AddActivity(on ? "Automatic pausing on." : "Automatic pausing off — a running session still restores normally.");
        });
    }

    /// <summary>Change settings under the lock, persist, and re-run discovery/matching.</summary>
    public void UpdateSettings(Action<AiPauseSettings> change)
    {
        lock (_gate)
        {
            change(Settings);
        }

        SaveSettings();
        Enqueue(() =>
        {
            _coordinator.RestoreDelay = TimeSpan.FromSeconds(Settings.RestoreDelaySeconds);
            _rediscover = true;
        });
    }

    public void SetRule(string gamePath, GameRule rule) => UpdateSettings(s => s.SetRule(gamePath, rule));

    public void AddGame(string exePath) => UpdateSettings(s =>
    {
        if (!s.ExtraGames.Any(g => PathUtil.Canonical(g.Path) == PathUtil.Canonical(exePath)))
        {
            s.ExtraGames.Add(new CustomGame { Name = Path.GetFileNameWithoutExtension(exePath), Path = exePath });
        }

        s.ExcludedPaths.RemoveAll(p => PathUtil.Canonical(p) == PathUtil.Canonical(exePath));
        s.DismissedSuggestions.RemoveAll(p => PathUtil.Canonical(p) == PathUtil.Canonical(exePath));
        _suggestion = null;
    });

    public void RemoveCustomGame(string path) => UpdateSettings(s =>
        s.ExtraGames.RemoveAll(g => PathUtil.Canonical(g.Path) == PathUtil.Canonical(path)));

    public void DismissSuggestion() => UpdateSettings(s =>
    {
        if (_suggestion is { } path)
        {
            s.DismissedSuggestions.Add(path);
        }

        _suggestion = null;
    });

    public void RefreshGames() => Enqueue(() => _rediscover = true);

    /// <summary>Windowed apps running right now (for "Add as game"), excluding known non-games.</summary>
    public IReadOnlyList<ProcInfo> WindowedApps()
    {
        var result = new List<ProcInfo>();
        var known = _processes.ToDictionary(p => p.Pid, p => p);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero
                        && known.TryGetValue(process.Id, out var info)
                        && !GameMatcher.NotGames.Contains(info.Name)
                        && !GameMatcher.Helpers.Contains(info.Name)
                        && !PathUtil.IsInside(info.Path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
                    {
                        result.Add(info);
                    }
                }
                catch
                {
                    // exited mid-scan
                }
            }
        }

        return result.DistinctBy(p => PathUtil.Canonical(p.Path)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Enqueue(Action command)
    {
        _commands.Enqueue(command);
        _wake.Release();
    }

    private void SaveSettings()
    {
        try
        {
            lock (_gate)
            {
                Settings.Save(_settingsPath);
            }
        }
        catch (Exception ex)
        {
            AddActivity($"Settings not saved: {ex.Message}");
        }
    }

    // ---------- worker ----------

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                while (_commands.TryDequeue(out var command))
                {
                    command();
                }

                await TickAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = $"Internal error: {ex.Message}";
                _log?.Invoke($"ai-pause: tick failed: {ex}");
            }

            Publish();
            try
            {
                await _wake.WaitAsync(TickInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var now = DateTime.UtcNow;
        if (_rediscover || now - _discoveredAt > DiscoveryInterval)
        {
            Discover();
        }

        var games = DetectGames(now);
        var decision = _coordinator.Decide(now, games);
        _lastDecision = decision;

        if (decision.AskPending.Count > 0 && (_snapshot.AskPending.Count == 0
            || decision.AskPending.Any(g => !_snapshot.AskPending.Any(s => s.Key == g.Key))))
        {
            Raise("Pause AI for this game?", $"{decision.AskPending[0].Name} is set to Ask. Open DeviceMaster to pause AI.");
        }

        if (_journalError is not null)
        {
            return; // never act on top of an unreadable journal
        }

        switch (decision.Action)
        {
            case CoordinatorAction.Pause when now >= _retryAt:
                await PauseAsync(decision, token);
                break;
            case CoordinatorAction.Restore when now >= _retryAt:
                await RestoreAsync(token);
                break;
            case CoordinatorAction.None when _partiallyRestored && now >= _retryAt
                && (decision.Triggering.Count > 0 || _coordinator.ManualHold):
                await PauseAsync(decision, token); // some AI came back before a new game started — pause it again
                break;
            case CoordinatorAction.None when _coordinator.Holding && _journal is not null && now >= _retryAt
                && _journal.Providers.Values.Any(p => p.Stage == ProviderStage.Captured):
                await RetryUnloadAsync(token); // a provider failed to unload earlier — keep trying while gaming
                break;
        }
    }

    private void Discover()
    {
        AiPauseSettings copy;
        lock (_gate)
        {
            copy = System.Text.Json.JsonSerializer.Deserialize<AiPauseSettings>(System.Text.Json.JsonSerializer.Serialize(Settings))!;
            copy.GameRules = new Dictionary<string, GameRule>(Settings.GameRules, StringComparer.OrdinalIgnoreCase);
        }

        var result = GameDiscovery.Scan(copy);
        var added = _discovery.Games.Count > 0 ? result.Games.Count - _discovery.Games.Count : 0;
        _discovery = result;
        _matcher = new GameMatcher(result.Games, copy);
        _discoveredAt = DateTime.UtcNow;
        if (_rediscover && added != 0)
        {
            AddActivity($"Game list refreshed: {result.Games.Count} games.");
        }

        _rediscover = false;
    }

    /// <summary>Recognized running games, or null when the process scan failed.</summary>
    private IReadOnlyList<RunningGame>? DetectGames(DateTime now)
    {
        try
        {
            _processes = _scanner.Scan();
            _detectionDownSince = null;
        }
        catch (Exception ex)
        {
            _detectionDownSince ??= now;
            if (now - _detectionDownSince > DetectionAlarm)
            {
                _lastError = $"Game detection unavailable: {ex.Message}";
            }

            return null;
        }

        var matcher = _matcher!;
        var running = new List<RunningGame>();
        foreach (var process in _processes)
        {
            if (matcher.Match(process.Path) is { } game)
            {
                GameRule rule;
                lock (_gate)
                {
                    rule = Settings.RuleFor(game.Path);
                }

                running.Add(new RunningGame(process.Pid, process.CreatedTicks, game.Name, game.Path, rule));
            }
        }

        var previous = _running.Select(g => g.Key).ToHashSet();
        foreach (var started in running.Where(g => !previous.Contains(g.Key)).DistinctBy(g => g.Path))
        {
            AddActivity($"Game started: {started.Name}{(started.Rule == GameRule.On ? "" : $" (rule {started.Rule})")}.");
        }

        var current = running.Select(g => g.Key).ToHashSet();
        foreach (var exited in _running.Where(g => !current.Contains(g.Key)).DistinctBy(g => g.Path))
        {
            AddActivity($"Game exited: {exited.Name}.");
        }

        _running = running;
        TrackFullscreen(now);
        return running;
    }

    /// <summary>Suggest (never pause for) a program that stays fullscreen in front for 10 s.</summary>
    private void TrackFullscreen(DateTime now)
    {
        bool enabled;
        lock (_gate)
        {
            enabled = Settings.SuggestUnknownGames;
        }

        if (!enabled || ProcessScanner.FullscreenForegroundPid() is not { } pid
            || _processes.FirstOrDefault(p => p.Pid == pid) is not { } process
            || _running.Any(g => g.Pid == pid)
            || GameMatcher.NotGames.Contains(process.Name)
            || _matcher!.IsExcluded(process.Path)
            || PathUtil.IsInside(process.Path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
        {
            _fullscreenCandidate = null;
            return;
        }

        lock (_gate)
        {
            if (Settings.DismissedSuggestions.Any(p => PathUtil.Canonical(p) == PathUtil.Canonical(process.Path)))
            {
                _fullscreenCandidate = null;
                return;
            }
        }

        if (_fullscreenCandidate?.Path != process.Path)
        {
            _fullscreenCandidate = (process.Path, now);
        }
        else if (now - _fullscreenCandidate.Value.Since >= SuggestAfter && _suggested.Add(process.Path))
        {
            _suggestion = process.Path;
            AddActivity($"{process.Name} looks like a game DeviceMaster doesn't know.");
            Raise("Is this a game?", $"{process.Name} is running fullscreen. Add it as a game to pause AI next time.");
        }
    }

    // ---------- pause / restore ----------

    private bool Enabled(IAiProvider provider)
    {
        lock (_gate)
        {
            return provider.Id switch
            {
                "ollama" => Settings.OllamaEnabled,
                "lmstudio" => Settings.LmStudioEnabled,
                "ninfer" => Settings.NInferEnabled,
                _ => true,
            };
        }
    }

    private async Task PauseAsync(CoordinatorDecision decision, CancellationToken token)
    {
        _busy = true;
        Publish();
        var reason = decision.Triggering.Count > 0
            ? string.Join(", ", decision.Triggering.Select(g => g.Name).Distinct())
            : "manual pause";
        AddActivity($"Pausing AI for {reason}.");
        var errors = new List<string>();
        try
        {
            _journal ??= new RecoveryJournal
            {
                PausedAt = DateTimeOffset.Now,
                Reason = reason,
                Games = decision.Triggering.Select(g => g.Path).Distinct().ToList(),
            };
            _journal.Save(_journalPath); // intent on disk before anything changes
            _coordinator.Holding = true;

            foreach (var provider in _providers.Where(Enabled))
            {
                if (_journal.Providers.ContainsKey(provider.Id))
                {
                    continue;
                }

                CaptureResult? capture;
                try
                {
                    capture = await provider.CaptureAsync(token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    errors.Add($"{provider.DisplayName}: {ex.Message}");
                    Note(provider, $"Couldn't read what's loaded: {ex.Message}", error: true);
                    continue;
                }

                if (capture is null)
                {
                    Note(provider, "Nothing loaded — left alone", error: false);
                    continue;
                }

                var entry = new ProviderEntry { Stage = ProviderStage.Captured, Record = capture.Record, Summary = capture.Summary, Bytes = capture.Bytes };
                _journal.Providers[provider.Id] = entry;
                _journal.Save(_journalPath);
                Note(provider, $"Unloading {capture.Summary}…", error: false);
                Publish();
                await UnloadAsync(provider, entry, errors, token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            errors.Add(ex.Message);
        }
        finally
        {
            _busy = false;
        }

        FinishStep("Pause", errors);
        if (errors.Count == 0)
        {
            _partiallyRestored = false;
            var freed = FreedBytes();
            var paused = _journal?.Providers.Count ?? 0;
            AddActivity(paused == 0 ? "Nothing was loaded — nothing to pause." : $"AI paused{(freed is { } b ? $", about {Gb(b)} freed" : "")}.");
            if (paused > 0)
            {
                Raise("AI paused", $"For {reason}{(freed is { } f ? $" · about {Gb(f)} freed" : "")}.");
            }
        }
    }

    private async Task RetryUnloadAsync(CancellationToken token)
    {
        _busy = true;
        Publish();
        var errors = new List<string>();
        try
        {
            foreach (var (id, entry) in _journal!.Providers.Where(p => p.Value.Stage == ProviderStage.Captured).ToList())
            {
                if (_providers.FirstOrDefault(p => p.Id == id) is { } provider)
                {
                    await UnloadAsync(provider, entry, errors, token);
                }
            }
        }
        finally
        {
            _busy = false;
        }

        FinishStep("Pause", errors);
    }

    private async Task UnloadAsync(IAiProvider provider, ProviderEntry entry, List<string> errors, CancellationToken token)
    {
        try
        {
            await provider.UnloadAsync(entry.Record, token);
            entry.Stage = ProviderStage.Unloaded;
            _journal!.Save(_journalPath);
            Note(provider, $"Paused — {entry.Summary}", error: false);
            _log?.Invoke($"ai-pause: {provider.Id} unloaded ({entry.Summary})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            errors.Add($"{provider.DisplayName}: {ex.Message}");
            Note(provider, $"Unload failed: {ex.Message}", error: true);
        }
    }

    private async Task RestoreAsync(CancellationToken token)
    {
        _busy = true;
        Publish();
        AddActivity("Restoring AI.");
        var errors = new List<string>();
        var deferred = false;
        var restored = 0;
        try
        {
            var journal = _journal ?? new RecoveryJournal();
            foreach (var (id, entry) in journal.Providers.ToList())
            {
                // a game that started during restoration defers the remaining loads
                if (GameStartedSince())
                {
                    deferred = true;
                    AddActivity("A game started — remaining restores deferred.");
                    break;
                }

                var provider = _providers.FirstOrDefault(p => p.Id == id);
                if (provider is null)
                {
                    errors.Add($"{id}: provider unavailable on this machine");
                    continue;
                }

                Note(provider, "Restoring…", error: false);
                Publish();
                try
                {
                    await provider.RestoreAsync(entry.Record, token);
                    journal.Providers.Remove(id);
                    journal.Save(_journalPath);
                    restored++;

                    Note(provider, $"Restored — {entry.Summary}", error: false);
                    _log?.Invoke($"ai-pause: {id} restored");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    errors.Add($"{provider.DisplayName}: {ex.Message}");
                    Note(provider, $"Restore failed: {ex.Message}", error: true);
                }
            }

            if (!deferred && errors.Count == 0)
            {
                RecoveryJournal.Delete(_journalPath);
                _journal = null;
                _coordinator.Holding = false;
                _partiallyRestored = false;
                AddActivity(restored > 0 ? "AI restored." : "Pause ended — nothing needed restoring.");
                if (restored > 0)
                {
                    Raise("AI restored", "Your local AI is back.");
                }
            }
            else if (restored > 0)
            {
                _partiallyRestored = true;
            }
        }
        finally
        {
            _busy = false;
        }

        FinishStep("Restore", errors);
    }

    /// <summary>A game instance with rule On that wasn't running at the last tick.</summary>
    private bool GameStartedSince()
    {
        if (!_coordinator.Automation || _matcher is not { } matcher)
        {
            return false;
        }

        try
        {
            var known = _running.Select(g => g.Key).ToHashSet();
            return new ProcessScanner().Scan().Any(p => !known.Contains(p.Key)
                && matcher.Match(p.Path) is { } game
                && RuleFor(game) == GameRule.On);
        }
        catch
        {
            return true; // unknown detection holds recovery
        }

        GameRule RuleFor(GameEntry game)
        {
            lock (_gate)
            {
                return Settings.RuleFor(game.Path);
            }
        }
    }

    private void FinishStep(string step, List<string> errors)
    {
        if (errors.Count == 0)
        {
            _lastError = null;
            _retryAt = DateTime.MinValue;
            return;
        }

        _lastError = $"{step} incomplete — {string.Join("; ", errors)}. Retrying in {RetryInterval.TotalSeconds:F0} s.";
        _retryAt = DateTime.UtcNow + RetryInterval;
        AddActivity(_lastError);
        _log?.Invoke($"ai-pause: {_lastError}");
        Raise($"AI {step.ToLowerInvariant()} needs attention", string.Join("; ", errors));
    }

    // ---------- snapshot ----------

    private void Note(IAiProvider provider, string text, bool error)
    {
        lock (_activity)
        {
            _providerNotes[provider.Id] = new ProviderLine(provider.DisplayName, text, error);
        }
    }

    private void AddActivity(string text)
    {
        lock (_activity)
        {
            _activity.Insert(0, new ActivityLine(DateTime.Now, text));
            if (_activity.Count > 60)
            {
                _activity.RemoveAt(_activity.Count - 1);
            }
        }

        _log?.Invoke($"ai-pause: {text}");
    }

    private void Raise(string title, string text)
    {
        bool enabled;
        lock (_gate)
        {
            enabled = Settings.NotificationsEnabled;
        }

        if (enabled)
        {
            try
            {
                Notify?.Invoke(title, text);
            }
            catch
            {
                // a notification is never worth a failed tick
            }
        }
    }

    private long? FreedBytes()
    {
        var total = _journal?.Providers.Values.Where(p => p.Stage == ProviderStage.Unloaded).Sum(p => p.Bytes ?? 0) ?? 0;
        return total > 0 ? total : null;
    }

    public static string Gb(long bytes) => $"{bytes / 1_000_000_000.0:F1} GB";

    private void Publish()
    {
        var decision = _lastDecision;
        var holding = _coordinator.Holding;
        AiPauseState state;
        string headline, hint;
        if (_journalError is not null)
        {
            (state, headline, hint) = (AiPauseState.Attention, "AI NEEDS ATTENTION", _journalError);
        }
        else if (_busy)
        {
            (state, headline, hint) = (AiPauseState.Busy, "LOADING", holding && _journal?.Providers.Values.All(p => p.Stage == ProviderStage.Unloaded) == true
                ? "Working on it…" : "Pausing or restoring AI…");
        }
        else if (_lastError is not null)
        {
            (state, headline, hint) = (AiPauseState.Attention, "AI NEEDS ATTENTION", _lastError);
        }
        else if (holding)
        {
            state = AiPauseState.Paused;
            headline = "AI PAUSED";
            hint = decision?.CountdownRemaining is { } left
                ? $"Restoring in {Math.Ceiling(left.TotalSeconds):F0} s — starting another game cancels this."
                : _coordinator.ManualHold
                    ? "Paused by you. Resume AI brings it back."
                    : decision?.Triggering.Count > 0
                        ? $"Paused for {string.Join(", ", decision.Triggering.Select(g => g.Name).Distinct())}."
                        : "Paused.";
        }
        else
        {
            state = AiPauseState.Running;
            headline = "AI RUNNING";
            hint = !_coordinator.Automation
                ? "Automatic pausing is off."
                : _coordinator.HasResumeOverride
                    ? "Running alongside the game, as you asked."
                    : "Watching for games.";
        }

        IReadOnlyList<LibraryGame> library;
        var runningPaths = _running.Select(g => PathUtil.Canonical(g.Path)).ToHashSet();
        lock (_gate)
        {
            library = _discovery.Games
                .Select(g => new LibraryGame(g, Settings.RuleFor(g.Path), runningPaths.Contains(PathUtil.Canonical(g.Path))))
                .ToList();
        }

        List<ProviderLine> lines;
        ActivityLine[] activity;
        lock (_activity)
        {
            lines = [];
            foreach (var provider in _providers)
            {
                if (!Enabled(provider))
                {
                    continue;
                }

                if (_journal?.Providers.TryGetValue(provider.Id, out var entry) == true)
                {
                    lines.Add(_providerNotes.TryGetValue(provider.Id, out var note)
                        ? note
                        : new ProviderLine(provider.DisplayName,
                            entry.Stage == ProviderStage.Unloaded ? $"Paused — {entry.Summary}" : $"Waiting to unload — {entry.Summary}", false));
                }
                else if (_providerNotes.TryGetValue(provider.Id, out var note) && (note.Error || _busy))
                {
                    lines.Add(note);
                }
                else if (SafePresent(provider))
                {
                    lines.Add(new ProviderLine(provider.DisplayName, SafeDescribe(provider), false));
                }
            }

            activity = _activity.ToArray();
        }

        _snapshot = new AiPauseSnapshot
        {
            State = state,
            Headline = headline,
            Hint = hint,
            ManualHold = _coordinator.ManualHold,
            Holding = holding,
            Automation = _coordinator.Automation,
            RunningGames = _running,
            AskPending = decision?.AskPending ?? [],
            Countdown = decision?.CountdownRemaining,
            FreedBytes = FreedBytes(),
            Providers = lines,
            Library = library,
            DiscoveryErrors = _discovery.Errors,
            Activity = activity,
            Suggestion = _suggestion,
            DetectionOk = _detectionDownSince is null,
        };
    }

    private static bool SafePresent(IAiProvider provider)
    {
        try
        {
            return provider.IsPresent;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeDescribe(IAiProvider provider)
    {
        try
        {
            return provider.Describe();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // shutting down
        }

        _cts.Dispose();
    }
}
