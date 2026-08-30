using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeviceMaster.Control;

namespace DeviceMaster.Ui;

/// <summary>
/// The NInfer tab: state badge + start/stop/restart for the local WSL2 LLM server, live GPU
/// tiles from the control loop's existing telemetry, and inference stats parsed from the
/// server's journal stream. All process/state logic lives in <see cref="NInferService"/>;
/// this class only renders its snapshot once a second.
/// </summary>
public sealed class NInferPage : IDisposable
{
    private readonly NInferService _service;

    private Border _statusDot = null!;
    private TextBlock _statusBadge = null!;
    private readonly TextBlock _headerInfo = new()
    {
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = Theme.Text,
        Margin = new Thickness(0, 2, 0, 12),
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly TextBlock _actionStatus = new()
    {
        FontSize = 12,
        Foreground = Theme.Dim,
        Margin = new Thickness(0, 10, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    private Border _startButton = null!, _stopButton = null!, _restartButton = null!;
    private TextBlock _logLabel = null!;
    private Border _logPanel = null!;
    private readonly TextBlock _logText = new()
    {
        FontSize = 11.5,
        Foreground = Theme.Dim,
        FontFamily = Theme.Mono,
        TextWrapping = TextWrapping.Wrap,
    };
    private bool _logOpen;

    private TextBlock _tileDecode = null!, _tileDecodeSub = null!;
    private TextBlock _tilePrefill = null!, _tilePrefillSub = null!;
    private TextBlock _tileRequests = null!, _tileRequestsSub = null!;
    private TextBlock _tileVram = null!, _tileVramSub = null!;
    private TextBlock _tileGpuLoad = null!, _tileGpuLoadSub = null!;
    private TextBlock _tileGpuTemp = null!, _tileGpuTempSub = null!;
    private Polyline _decodeSpark = null!, _prefillSpark = null!;

    private readonly StackPanel _requestRows = new();
    private string _requestsSignature = "?";

    public NInferPage(NInferService service) => _service = service;

    public UIElement Build()
    {
        var page = new StackPanel { MaxWidth = 1460, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(MainWindow.PageTitle("NInfer", "local LLM inference server · WSL2"));

        // ---- header: state badge, model/uptime, start/stop/restart ----
        var card = Theme.CardShell("λ", "NInfer server", "systemd unit in WSL2 — Stop frees the VRAM for gaming",
            out var body, out var head);
        var badge = Theme.StatusBadge("…", Theme.Faint, out _statusDot, out _statusBadge);
        badge.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);

        body.Children.Add(_headerInfo);

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        _startButton = Theme.Btn("▶  Start", primary: true, () => _ = _service.RunActionAsync("start"));
        _restartButton = Theme.Btn("↻  Restart", primary: false, () => _ = _service.RunActionAsync("restart"));
        _stopButton = Theme.Btn("■  Stop", primary: false, ConfirmStop);
        var logButton = Theme.Btn("▸  View log", primary: false, ToggleLog);
        _logLabel = (TextBlock)logButton.Child;
        foreach (var button in new[] { _startButton, _restartButton, _stopButton, logButton })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(button);
        }

        body.Children.Add(buttons);
        body.Children.Add(_actionStatus);

        _logPanel = Theme.InsetRow(new ScrollViewer
        {
            Content = _logText,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        _logPanel.Margin = new Thickness(0, 12, 0, 0);
        _logPanel.Visibility = Visibility.Collapsed;
        body.Children.Add(_logPanel);

        card.Margin = new Thickness(0, 0, 12, 16);
        page.Children.Add(card);

        // ---- tiles: inference throughput (journal) + GPU (control loop telemetry) ----
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6 };
        tiles.Children.Add(SparkTile("DECODE", out _tileDecode, out _tileDecodeSub, out _decodeSpark));
        tiles.Children.Add(SparkTile("PREFILL", out _tilePrefill, out _tilePrefillSub, out _prefillSpark));
        tiles.Children.Add(Tile("REQUESTS", out _tileRequests, out _tileRequestsSub));
        tiles.Children.Add(Tile("VRAM", out _tileVram, out _tileVramSub));
        tiles.Children.Add(Tile("GPU LOAD", out _tileGpuLoad, out _tileGpuLoadSub));
        tiles.Children.Add(Tile("GPU TEMP", out _tileGpuTemp, out _tileGpuTempSub));
        page.Children.Add(tiles);

        // ---- recent requests ----
        var requests = Theme.CardShell("≡", "Recent requests", "per-request summaries from the server journal",
            out var requestsBody, out _);
        requests.Margin = new Thickness(0, 4, 12, 0);
        requestsBody.Children.Add(RequestTableHeader());
        requestsBody.Children.Add(_requestRows);
        page.Children.Add(requests);

        Tick(null);
        return page;
    }

    // ---------- per-second refresh (driven by MainWindow's UI timer) ----------

    public void Tick(ControlStatus? status)
    {
        var snap = _service.Snapshot;

        var (badgeText, badgeBrush) = snap.State switch
        {
            NInferState.Running => ("Running", Theme.Good),
            NInferState.Starting => ("Starting", Theme.Accent2),
            NInferState.Unhealthy => ("Unhealthy", Theme.Warn),
            _ => ("Stopped", Theme.Faint),
        };
        _statusBadge.Text = badgeText;
        _statusBadge.Foreground = badgeBrush;
        _statusDot.Background = badgeBrush;

        _headerInfo.Text = snap.State switch
        {
            NInferState.Running =>
                $"{snap.ModelName ?? "model —"}   ·   up {FormatUptime(snap.RunningSince)}",
            NInferState.Starting => "Starting — distro boot + model load takes ~15–30 s…",
            NInferState.Unhealthy => "WSL distro is up but /health isn't answering — check the log.",
            _ => "Stopped — VRAM is free. Start loads the model back into GPU memory.",
        };
        _headerInfo.Foreground = snap.State == NInferState.Unhealthy ? Theme.Warn : Theme.Text;

        _actionStatus.Text = snap.ActionInfo
            ?? "Stop tears down the whole WSL distro so Windows reclaims RAM and VRAM.";

        SetEnabled(_startButton, !snap.ActionBusy && snap.State is NInferState.Stopped or NInferState.Unhealthy);
        SetEnabled(_restartButton, !snap.ActionBusy && snap.State != NInferState.Stopped);
        SetEnabled(_stopButton, !snap.ActionBusy && snap.State != NInferState.Stopped);

        // inference tiles — only meaningful while the server is up
        var throughput = snap.State == NInferState.Running ? snap.Throughput : null;
        _tileDecode.Text = throughput is { } t ? $"{t.DecodeTokPerS:F1}" : "—";
        _tileDecodeSub.Text = "tok/s";
        _tilePrefill.Text = throughput is { } p ? $"{p.PrefillTokPerS:F1}" : "—";
        _tilePrefillSub.Text = "tok/s";
        _tileRequests.Text = throughput is { } r ? r.RunningRequests.ToString() : "—";
        _tileRequestsSub.Text = throughput is { } w ? $"{w.WaitingRequests} queued" : "active · queued";
        UpdateSpark(_decodeSpark, snap.State == NInferState.Running ? snap.DecodeHistory : []);
        UpdateSpark(_prefillSpark, snap.State == NInferState.Running ? snap.PrefillHistory : []);

        // GPU tiles — the control loop's existing LHM telemetry (no second NVML reader)
        _tileVram.Text = status?.VramUsedGb is { } used ? $"{used:F1}" : "—";
        _tileVramSub.Text = status?.VramTotalGb is { } total ? $"of {total:F1} GB" : "GB used";
        _tileGpuLoad.Text = status?.GpuLoadPercent is { } load ? $"{load:F0}%" : "—";
        _tileGpuLoadSub.Text = status?.GpuPowerW is { } power ? $"{power:F0} W" : "utilization";
        _tileGpuTemp.Text = status?.GpuTemperatureC is { } temp ? $"{temp:F0}°" : "—";
        _tileGpuTempSub.Text = "core";

        RefreshRequests(snap);
    }

    private void RefreshRequests(NInferSnapshot snap)
    {
        var signature = string.Join("|", snap.RecentRequests.Select(r => $"{r.Summary.Id}@{r.At.Ticks}"));
        if (signature == _requestsSignature)
        {
            return;
        }

        _requestsSignature = signature;
        _requestRows.Children.Clear();
        if (snap.RecentRequests.Count == 0)
        {
            _requestRows.Children.Add(new TextBlock
            {
                Text = "No completed requests seen yet — summaries appear here as requests finish.",
                Foreground = Theme.Dim,
                FontSize = 12.5,
                Margin = new Thickness(2, 4, 0, 2),
            });
            return;
        }

        foreach (var row in snap.RecentRequests)
        {
            _requestRows.Children.Add(RequestRow(row));
        }
    }

    // table columns shared by the header and every request row
    private static Grid RequestTableGrid()
    {
        var grid = new Grid();
        foreach (var width in new[] { 76.0, 64.0, 120.0, 100.0, 80.0, 90.0, 100.0 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static Grid RequestTableHeader()
    {
        var grid = RequestTableGrid();
        grid.Margin = new Thickness(14, 0, 14, 4);
        var column = 0;
        foreach (var text in new[] { "TIME", "REQ", "FINISH", "PROMPT", "GEN", "TTFT", "DECODE", "SPECULATIVE" })
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Faint,
                Margin = new Thickness(2, 0, 0, 0),
            };
            Grid.SetColumn(block, column++);
            grid.Children.Add(block);
        }

        return grid;
    }

    private static Border RequestRow(NInferRequestRow row)
    {
        var summary = row.Summary;
        var grid = RequestTableGrid();
        var column = 0;
        foreach (var (text, brush) in new (string, Brush)[]
        {
            (row.At.ToString("HH:mm:ss"), Theme.Faint),
            ($"#{summary.Id}", Theme.Text),
            (summary.Finish, summary.Finish == "stop" ? Theme.Good : Theme.Dim),
            (summary.PromptTokens is { } prompt ? $"{prompt:N0} tok" : "—", Theme.Text),
            (summary.GenTokens is { } gen ? $"{gen:N0} tok" : "—", Theme.Text),
            (summary.TtftMs is { } ttft ? ttft >= 1000 ? $"{ttft / 1000.0:F1} s" : $"{ttft} ms" : "—", Theme.Text),
            (summary.DecodeTokPerS is { } decode ? $"{decode:F1} tok/s" : "—", Theme.Accent2),
            (summary.SpeculativeAcceptPercent is { } spec ? $"{spec:F0}% accepted" : "—", Theme.Dim),
        })
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontFamily = Theme.Mono,
                Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            Grid.SetColumn(block, column++);
            grid.Children.Add(block);
        }

        return Theme.InsetRow(grid);
    }

    // ---------- actions ----------

    /// <summary>Stop is the one destructive action: it also terminates the whole WSL distro.</summary>
    private void ConfirmStop()
    {
        var choice = MessageBox.Show(
            "Stop NInfer?\n\nThis stops the service AND terminates the entire WSL distro "
            + "(wsl --terminate) — anything else running inside WSL dies with it. "
            + "RAM and VRAM are released back to Windows.",
            "Stop NInfer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Yes)
        {
            _ = _service.RunActionAsync("stop");
        }
    }

    private async void ToggleLog()
    {
        _logOpen = !_logOpen;
        _logPanel.Visibility = _logOpen ? Visibility.Visible : Visibility.Collapsed;
        _logLabel.Text = _logOpen ? "▾  Hide log" : "▸  View log";
        if (_logOpen)
        {
            _logText.Text = "Loading…";
            _logText.Text = await _service.ReadLogAsync(); // never throws — errors come back as text
        }
    }

    // ---------- small helpers ----------

    private static void SetEnabled(Border button, bool enabled)
    {
        button.IsHitTestVisible = enabled;
        button.Opacity = enabled ? 1.0 : 0.45;
    }

    private static string FormatUptime(DateTimeOffset? since)
    {
        if (since is not { } start)
        {
            return "—";
        }

        var up = DateTimeOffset.Now - start;
        return up.TotalHours >= 1
            ? $"{(int)up.TotalHours} h {up.Minutes:D2} m"
            : up.TotalMinutes >= 1 ? $"{up.Minutes} m {up.Seconds:D2} s" : $"{up.Seconds} s";
    }

    private static Border Tile(string label, out TextBlock value, out TextBlock sub) =>
        SparkTileCore(label, out value, out sub, null);

    private static Border SparkTile(string label, out TextBlock value, out TextBlock sub, out Polyline spark)
    {
        spark = new Polyline
        {
            Stroke = Theme.Accent,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Height = SparkHeight,
            Width = SparkWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        };
        return SparkTileCore(label, out value, out sub, spark);
    }

    /// <summary>Mirrors the dashboard's StatTile, with an optional sparkline under the subtitle.</summary>
    private static Border SparkTileCore(string label, out TextBlock value, out TextBlock sub, Polyline? spark)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint });
        value = new TextBlock { Text = "—", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Theme.Text, Margin = new Thickness(0, 4, 0, 2) };
        stack.Children.Add(value);
        sub = new TextBlock { Text = "", FontSize = 10.5, Foreground = Theme.Faint };
        stack.Children.Add(sub);
        if (spark is not null)
        {
            stack.Children.Add(spark);
        }

        return new Border
        {
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(18, 14, 18, 14),
            CornerRadius = new CornerRadius(14),
            Background = Theme.Card,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private const double SparkWidth = 150;
    private const double SparkHeight = 26;

    /// <summary>Fixed-box sparkline scaled to the history's own maximum (baseline at 0).</summary>
    private static void UpdateSpark(Polyline spark, IReadOnlyList<double> history)
    {
        if (history.Count < 2 || history.Max() <= 0)
        {
            spark.Points = [];
            return;
        }

        var max = history.Max();
        var points = new PointCollection(history.Count);
        var step = SparkWidth / (history.Count - 1);
        for (var i = 0; i < history.Count; i++)
        {
            points.Add(new Point(i * step, SparkHeight - Math.Clamp(history[i] / max, 0, 1) * SparkHeight));
        }

        spark.Points = points;
    }

    public void Dispose() => _service.Dispose();
}
