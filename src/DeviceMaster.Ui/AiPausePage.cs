using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeviceMaster.AiPause;

namespace DeviceMaster.Ui;

/// <summary>
/// The AI Pause tab: state card (running / paused / loading / needs attention), per-app lines,
/// Pause/Resume, the Ask prompt and unknown-game suggestion, the game library with per-game
/// rules, other AI apps to stop, and settings. All logic lives in <see cref="AiPauseService"/>;
/// this class renders its snapshot once a second.
/// </summary>
public sealed class AiPausePage : IDisposable
{
    private static readonly Brush Orange = Theme.B("#ffa24d");

    private readonly AiPauseService _service;
    private readonly bool _ninferPresent;

    private Border _stateCard = null!;
    private Border _stateDot = null!;
    private readonly TextBlock _headline = new() { FontSize = 22, FontWeight = FontWeights.Bold };
    private readonly TextBlock _hint = new() { FontSize = 13, Foreground = Theme.Text, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _gameLine = new() { FontSize = 12.5, Foreground = Theme.Dim, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _providerRows = new() { Margin = new Thickness(0, 12, 0, 0) };
    private Border _pauseButton = null!, _resumeButton = null!;
    private TextBlock _resumeLabel = null!;

    private Border _askCard = null!;
    private readonly TextBlock _askText = new() { FontSize = 13, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private Border _suggestCard = null!;
    private readonly TextBlock _suggestText = new() { FontSize = 13, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };

    private readonly TextBox _search = DarkBox("", 260);
    private readonly StackPanel _gameRows = new();
    private readonly TextBlock _gamesSummary = new() { FontSize = 11.5, Foreground = Theme.Faint, Margin = new Thickness(2, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private string _gamesSignature = "?";

    private readonly StackPanel _runningApps = new();
    private readonly StackPanel _appRows = new();
    private readonly StackPanel _activityRows = new();
    private string _activitySignature = "?";

    public AiPausePage(AiPauseService service, bool ninferPresent)
    {
        _service = service;
        _ninferPresent = ninferPresent;
    }

    public UIElement Build()
    {
        var page = new StackPanel { MaxWidth = 1460, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(MainWindow.PageTitle("AI Pause", "frees GPU memory from local AI while you game"));

        page.Children.Add(BuildStateCard());
        page.Children.Add(BuildAskCard());
        page.Children.Add(BuildSuggestCard());
        page.Children.Add(BuildGamesCard());
        page.Children.Add(BuildAppsCard());
        page.Children.Add(BuildSettingsCard());
        page.Children.Add(BuildActivityCard());

        _search.TextChanged += (_, _) => Tick();
        Tick();
        return page;
    }

    // ---------- cards ----------

    private UIElement BuildStateCard()
    {
        var stack = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        _stateDot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(_stateDot);
        top.Children.Add(_headline);
        stack.Children.Add(top);
        stack.Children.Add(_hint);
        stack.Children.Add(_gameLine);
        stack.Children.Add(_providerRows);

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _pauseButton = Theme.Btn("❚❚  Pause AI", primary: true, () => _service.PauseNow());
        _resumeButton = Theme.Btn("▶  Resume AI", primary: false, ConfirmResume);
        _resumeLabel = (TextBlock)_resumeButton.Child;
        foreach (var button in new[] { _pauseButton, _resumeButton })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(button);
        }

        var auto = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        auto.Children.Add(Theme.SmallLabel("Pause automatically when a game starts"));
        auto.Children.Add(Theme.Toggle(_service.Settings.AutomationEnabled, _service.SetAutomation));
        buttons.Children.Add(auto);
        stack.Children.Add(buttons);

        _stateCard = new Border
        {
            Margin = new Thickness(0, 0, 12, 16),
            Padding = new Thickness(22, 18, 22, 18),
            CornerRadius = new CornerRadius(14),
            Background = Theme.Card,
            BorderThickness = new Thickness(1.5),
            Child = stack,
        };
        return _stateCard;
    }

    private UIElement BuildAskCard()
    {
        _askCard = Theme.CardShell("?", "Pause AI for this game?", "this game's rule is Ask — nothing pauses unless you say so", out var body, out _);
        body.Children.Add(_askText);
        var row = new WrapPanel();
        foreach (var button in new[]
        {
            Theme.Btn("Pause AI for this game", primary: true, () => _service.AnswerAsk(pause: true, remember: false)),
            Theme.Btn("Always pause", primary: false, () => _service.AnswerAsk(pause: true, remember: true)),
            Theme.Btn("Never pause", primary: false, () => _service.AnswerAsk(pause: false, remember: true)),
            Theme.Btn("Not now", primary: false, () => _service.AnswerAsk(pause: false, remember: false)),
        })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(button);
        }

        body.Children.Add(row);
        _askCard.Margin = new Thickness(0, 0, 12, 16);
        _askCard.Visibility = Visibility.Collapsed;
        return _askCard;
    }

    private UIElement BuildSuggestCard()
    {
        _suggestCard = Theme.CardShell("◎", "Is this a game?", "a program ran fullscreen in front — AI is never paused on a suggestion alone", out var body, out _);
        body.Children.Add(_suggestText);
        var row = new WrapPanel();
        var add = Theme.Btn("Add as game", primary: true, () =>
        {
            if (_service.Snapshot.Suggestion is { } path)
            {
                _service.AddGame(path);
            }
        });
        var dismiss = Theme.Btn("Not a game", primary: false, _service.DismissSuggestion);
        foreach (var button in new[] { add, dismiss })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(button);
        }

        body.Children.Add(row);
        _suggestCard.Margin = new Thickness(0, 0, 12, 16);
        _suggestCard.Visibility = Visibility.Collapsed;
        return _suggestCard;
    }

    private UIElement BuildGamesCard()
    {
        var card = Theme.CardShell("▶", "Games", "found in Steam, Epic, GOG, EA, Ubisoft, Battle.net and Xbox — On pauses AI, Ask asks, Off ignores",
            out var body, out _);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        tools.Children.Add(Theme.SmallLabel("Search"));
        _search.Margin = new Thickness(0, 0, 12, 0);
        tools.Children.Add(_search);
        foreach (var button in new[]
        {
            Theme.Btn("+  Add game…", primary: false, BrowseForGame),
            Theme.Btn("☰  Running apps", primary: false, ToggleRunningApps),
            Theme.Btn("↻  Refresh list", primary: false, _service.RefreshGames),
        })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            tools.Children.Add(button);
        }

        body.Children.Add(tools);
        _runningApps.Visibility = Visibility.Collapsed;
        _runningApps.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(_runningApps);
        body.Children.Add(_gamesSummary);
        body.Children.Add(new ScrollViewer
        {
            Content = _gameRows,
            MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        card.Margin = new Thickness(0, 0, 12, 16);
        return card;
    }

    private UIElement BuildAppsCard()
    {
        var card = Theme.CardShell("λ", "Other AI apps", "servers without their own integration (llama-server, KoboldCpp, …) — stopped while you game, started again after",
            out var body, out _);
        body.Children.Add(_appRows);
        var add = Theme.Btn("+  Add app…", primary: false, BrowseForApp);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(add);
        RebuildApps();
        card.Margin = new Thickness(0, 0, 12, 16);
        return card;
    }

    private UIElement BuildSettingsCard()
    {
        var settings = _service.Settings;
        var card = Theme.CardShell("⚙", "Settings", "which AI apps to pause, and how", out var body, out _);

        body.Children.Add(SettingRow("Ollama — unload models while gaming", Theme.Toggle(settings.OllamaEnabled,
            on => _service.UpdateSettings(s => s.OllamaEnabled = on))));
        body.Children.Add(SettingRow("LM Studio — unload models while gaming (via the lms CLI)", Theme.Toggle(settings.LmStudioEnabled,
            on => _service.UpdateSettings(s => s.LmStudioEnabled = on))));
        if (_ninferPresent)
        {
            body.Children.Add(SettingRow("NInfer — stop the WSL server while gaming (terminates the distro)", Theme.Toggle(settings.NInferEnabled,
                on => _service.UpdateSettings(s => s.NInferEnabled = on))));
        }

        body.Children.Add(SettingRow("Notifications", Theme.Toggle(settings.NotificationsEnabled,
            on => _service.UpdateSettings(s => s.NotificationsEnabled = on))));
        body.Children.Add(SettingRow("Suggest fullscreen programs that look like games", Theme.Toggle(settings.SuggestUnknownGames,
            on => _service.UpdateSettings(s => s.SuggestUnknownGames = on))));

        var delay = DarkBox(settings.RestoreDelaySeconds.ToString(), 70);
        delay.LostFocus += (_, _) =>
        {
            if (int.TryParse(delay.Text, out var seconds) && seconds is >= 0 and <= 3600)
            {
                _service.UpdateSettings(s => s.RestoreDelaySeconds = seconds);
            }
            else
            {
                delay.Text = _service.Settings.RestoreDelaySeconds.ToString();
            }
        };
        body.Children.Add(SettingRow("Restore delay after the last game exits (seconds)", delay));

        var endpoint = DarkBox(settings.OllamaEndpoint, 240);
        endpoint.LostFocus += (_, _) =>
        {
            var text = endpoint.Text.Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsLoopback)
            {
                _service.UpdateSettings(s => s.OllamaEndpoint = text);
            }
            else
            {
                endpoint.Text = _service.Settings.OllamaEndpoint; // local only — never a remote host
            }
        };
        body.Children.Add(SettingRow("Ollama address (this PC only)", endpoint));

        card.Margin = new Thickness(0, 0, 12, 16);
        return card;
    }

    private UIElement BuildActivityCard()
    {
        var card = Theme.CardShell("≡", "Activity", "what AI Pause saw and did (newest first)", out var body, out _);
        body.Children.Add(_activityRows);
        card.Margin = new Thickness(0, 0, 12, 0);
        return card;
    }

    // ---------- per-second refresh ----------

    public void Tick()
    {
        var snap = _service.Snapshot;
        var color = snap.State switch
        {
            AiPauseState.Paused => Orange,
            AiPauseState.Busy => Theme.Warn,
            AiPauseState.Attention => Theme.Danger,
            _ => Theme.Accent,
        };
        _headline.Text = snap.Headline;
        _headline.Foreground = color;
        _stateDot.Background = color;
        _stateCard.BorderBrush = color;
        _hint.Text = snap.Hint;

        var games = snap.RunningGames.Select(g => g.Name).Distinct().ToList();
        var gameText = games.Count > 0 ? $"Running: {string.Join(", ", games)}" : "No game running";
        if (snap.FreedBytes is { } freed && snap.Holding)
        {
            gameText += $"   ·   about {AiPauseService.Gb(freed)} freed";
        }

        _gameLine.Text = gameText;

        _providerRows.Children.Clear();
        foreach (var line in snap.Providers)
        {
            var text = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new System.Windows.Documents.Run($"{line.Name}:  ") { FontWeight = FontWeights.SemiBold, Foreground = Theme.Text });
            text.Inlines.Add(new System.Windows.Documents.Run(line.Text) { Foreground = line.Error ? Theme.Danger : Theme.Dim });
            _providerRows.Children.Add(Theme.InsetRow(text));
        }

        if (snap.Providers.Count == 0)
        {
            _providerRows.Children.Add(new TextBlock
            {
                Text = "No local AI apps found yet — Ollama, LM Studio and apps you add below appear here.",
                FontSize = 12, Foreground = Theme.Faint, Margin = new Thickness(2, 0, 0, 8),
            });
        }

        var busy = snap.State == AiPauseState.Busy;
        SetEnabled(_pauseButton, !busy && !snap.Holding && !snap.ManualHold);
        SetEnabled(_resumeButton, !busy && (snap.Holding || snap.ManualHold));
        _resumeLabel.Text = busy ? "Loading…" : snap.State == AiPauseState.Attention && snap.Holding ? "▶  Retry resume" : "▶  Resume AI";

        _askCard.Visibility = snap.AskPending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _askText.Text = snap.AskPending.Count > 0
            ? $"{string.Join(", ", snap.AskPending.Select(g => g.Name).Distinct())} is running. AI stays loaded unless you pause it."
            : "";

        _suggestCard.Visibility = snap.Suggestion is null ? Visibility.Collapsed : Visibility.Visible;
        _suggestText.Text = snap.Suggestion is { } path
            ? $"{System.IO.Path.GetFileName(path)} ran fullscreen for a while, but it isn't in any launcher's library.\n{path}"
            : "";

        RefreshGames(snap);
        RefreshActivity(snap);
    }

    private void RefreshGames(AiPauseSnapshot snap)
    {
        var filter = _search.Text.Trim();
        var signature = filter + "|" + string.Join("|", snap.Library.Select(g => $"{g.Game.Path}:{g.Rule}:{g.Running}"));
        if (signature == _gamesSignature)
        {
            return;
        }

        _gamesSignature = signature;
        var errors = snap.DiscoveryErrors.Count > 0
            ? "   ·   couldn't read: " + string.Join(", ", snap.DiscoveryErrors.Select(e => $"{e.Key} ({e.Value})"))
            : "";
        _gamesSummary.Text = $"{snap.Library.Count} games{errors}";
        _gameRows.Children.Clear();
        var shown = snap.Library
            .Where(g => filter.Length == 0
                || g.Game.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || g.Game.Launcher.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Running)
            .ThenBy(g => g.Game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var game in shown.Take(300))
        {
            _gameRows.Children.Add(GameRow(game));
        }

        if (shown.Count == 0)
        {
            _gameRows.Children.Add(new TextBlock
            {
                Text = snap.Library.Count == 0 ? "No games found yet. Use Add game… or Running apps." : "No games match.",
                FontSize = 12.5, Foreground = Theme.Dim, Margin = new Thickness(2, 4, 0, 4),
            });
        }
    }

    private Border GameRow(LibraryGame entry)
    {
        var row = new DockPanel { LastChildFill = true };
        var rule = new DmDropdown(["On — pause AI", "Ask first", "Off — ignore"], (int)entry.Rule, 140);
        rule.SelectionChanged += index => _service.SetRule(entry.Game.Path, (GameRule)index);
        DockPanel.SetDock(rule, Dock.Right);
        row.Children.Add(rule);

        if (entry.Game.Launcher == "Custom")
        {
            var remove = Theme.Btn("Remove", primary: false, () => _service.RemoveCustomGame(entry.Game.Path));
            remove.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
        }

        var launcher = new Border
        {
            Width = 86,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = entry.Game.Launcher, FontSize = 11, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(launcher, Dock.Left);
        row.Children.Add(launcher);

        var name = new TextBlock
        {
            FontSize = 12.5,
            Foreground = entry.Rule == GameRule.Off ? Theme.Faint : Theme.Text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.Game.Path,
        };
        name.Inlines.Add(new System.Windows.Documents.Run(entry.Game.Name));
        if (entry.Running)
        {
            name.Inlines.Add(new System.Windows.Documents.Run("   ● running") { Foreground = Theme.Good, FontSize = 11.5 });
        }

        row.Children.Add(name);
        return Theme.InsetRow(row);
    }

    private void RefreshActivity(AiPauseSnapshot snap)
    {
        var signature = snap.Activity.Count > 0 ? $"{snap.Activity[0].At.Ticks}:{snap.Activity.Count}" : "";
        if (signature == _activitySignature)
        {
            return;
        }

        _activitySignature = signature;
        _activityRows.Children.Clear();
        foreach (var line in snap.Activity.Take(15))
        {
            var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 5) };
            text.Inlines.Add(new System.Windows.Documents.Run($"{line.At:HH:mm:ss}   ") { Foreground = Theme.Faint, FontFamily = Theme.Mono });
            text.Inlines.Add(new System.Windows.Documents.Run(line.Text) { Foreground = Theme.Dim });
            _activityRows.Children.Add(text);
        }

        if (snap.Activity.Count == 0)
        {
            _activityRows.Children.Add(new TextBlock { Text = "Nothing yet.", FontSize = 12, Foreground = Theme.Faint });
        }
    }

    // ---------- actions ----------

    private void ConfirmResume()
    {
        var snap = _service.Snapshot;
        var games = snap.RunningGames.Where(g => g.Rule != GameRule.Off).Select(g => g.Name).Distinct().ToList();
        if (games.Count > 0 && snap.Holding)
        {
            var choice = MessageBox.Show(
                $"{string.Join(", ", games)} is still running.\n\nRestoring AI now can compete with the game for GPU memory. "
                + "AI stays loaded alongside this game until it exits; a new game pauses it again.",
                "Resume AI while gaming?",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            if (choice != MessageBoxResult.OK)
            {
                return;
            }
        }

        _service.ResumeNow();
    }

    private void BrowseForGame()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose the game's executable (not its launcher)" };
        if (dialog.ShowDialog() == true)
        {
            _service.AddGame(dialog.FileName);
        }
    }

    private void BrowseForApp()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose the AI server's program file (e.g. llama-server.exe)" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FileName;
        _service.UpdateSettings(s =>
        {
            if (!s.ProcessApps.Any(a => PathUtil.Canonical(a.Path) == PathUtil.Canonical(path)))
            {
                s.ProcessApps.Add(new ProcessApp { Name = System.IO.Path.GetFileNameWithoutExtension(path), Path = path });
            }
        });
        RebuildApps();
    }

    private void RebuildApps()
    {
        _appRows.Children.Clear();
        foreach (var app in _service.Settings.ProcessApps.ToList())
        {
            var row = new DockPanel { LastChildFill = true };
            var remove = Theme.Btn("Remove", primary: false, () =>
            {
                _service.UpdateSettings(s => s.ProcessApps.RemoveAll(a => PathUtil.Canonical(a.Path) == PathUtil.Canonical(app.Path)));
                RebuildApps();
            });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var relaunch = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            relaunch.Children.Add(Theme.SmallLabel("Start again after gaming"));
            relaunch.Children.Add(Theme.Toggle(app.Relaunch, on => _service.UpdateSettings(_ => app.Relaunch = on)));
            DockPanel.SetDock(relaunch, Dock.Right);
            row.Children.Add(relaunch);

            row.Children.Add(new TextBlock
            {
                Text = app.Path,
                FontSize = 12.5,
                Foreground = Theme.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = app.Path,
            });
            _appRows.Children.Add(Theme.InsetRow(row));
        }

        if (_service.Settings.ProcessApps.Count == 0)
        {
            _appRows.Children.Add(new TextBlock
            {
                Text = "None. Ollama, LM Studio" + (_ninferPresent ? " and NInfer" : "") + " are handled on their own; add other servers here.",
                FontSize = 12, Foreground = Theme.Faint, Margin = new Thickness(2, 0, 0, 8),
            });
        }
    }

    private void ToggleRunningApps()
    {
        if (_runningApps.Visibility == Visibility.Visible)
        {
            _runningApps.Visibility = Visibility.Collapsed;
            return;
        }

        _runningApps.Children.Clear();
        _runningApps.Children.Add(Theme.SmallLabel("Running apps with a window — pick the game itself, not its launcher:"));
        foreach (var app in _service.WindowedApps())
        {
            var row = new DockPanel { LastChildFill = true };
            var add = Theme.Btn("Add as game", primary: false, () =>
            {
                _service.AddGame(app.Path);
                _runningApps.Visibility = Visibility.Collapsed;
            });
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Add(add);
            row.Children.Add(new TextBlock
            {
                Text = $"{app.Name}    {app.Path}",
                FontSize = 12,
                Foreground = Theme.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = app.Path,
            });
            _runningApps.Children.Add(Theme.InsetRow(row));
        }

        _runningApps.Visibility = Visibility.Visible;
    }

    // ---------- helpers ----------

    private static DockPanel SettingRow(string label, UIElement control)
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        return row;
    }

    private static TextBox DarkBox(string text, double width) => new()
    {
        Text = text,
        Width = width,
        FontSize = 12.5,
        Background = Theme.Inset,
        Foreground = Theme.Text,
        CaretBrush = Theme.Text,
        BorderBrush = Theme.Line2,
        Padding = new Thickness(7, 4, 7, 4),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static void SetEnabled(Border button, bool enabled)
    {
        button.IsHitTestVisible = enabled;
        button.Opacity = enabled ? 1.0 : 0.45;
    }

    public void Dispose() => _service.Dispose();
}
