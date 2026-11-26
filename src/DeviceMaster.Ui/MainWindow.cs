using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeviceMaster.Control;
using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Lighting;
using DeviceMaster.Core.Conflicts;
using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;
using DeviceMaster.Core.Updating;
using DeviceMaster.Devices.CorsairLink;
using WinForms = System.Windows.Forms;

namespace DeviceMaster.Ui;

public sealed class MainWindow : Window
{
    /// <summary>Whole-number app version, derived from the assembly version major (csproj is the single source).</summary>
    public static string AppVersion { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version?.Major ?? 0).ToString();

    public const string VersionDate = "2026-11-26";

    private readonly ControlSettings _controlSettings = ControlSettings.Load();
    private ControlLoop? _loop;
    private bool _uiReady;

    // ---------- diagnostics log (device-layer messages land here; readable post-mortem) ----------

    private static readonly object LogGate = new();
    private static readonly string LogPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(ControlSettings.ConfigPath)!, "logs", "app.log");

    internal static void LogLine(string message)
    {
        try
        {
            lock (LogGate)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                if (System.IO.File.Exists(LogPath) && new System.IO.FileInfo(LogPath).Length > 2_000_000)
                {
                    System.IO.File.Move(LogPath, LogPath + ".old", overwrite: true);
                }

                System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // diagnostics must never break the app
        }
    }

    // header / updater
    private Border _checkButton = null!;
    private TextBlock _checkLabel = null!;
    private UpdateInfo? _pendingUpdate;
    private bool _checkBusy;
    private bool _downloading;
    private int _downloadDots;

    // fan control card
    private readonly TextBlock _controlStatus = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(0, 14, 0, 10) };
    private readonly StackPanel _fanRows = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _fanWarnings = new();
    private readonly TextBlock _fanSummary = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
    private TextBlock _fanExpandLink = null!;
    private TextBlock _fanForgetLink = null!;
    private bool _fansExpanded;
    private Border _fanDot = null!;
    private TextBlock _fanBadge = null!;

    // hardware page — summary count up top, then pills auto-grouped by family, three per row
    private readonly StackPanel _hardwareRows = new();
    private readonly TextBlock _hwSummary = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
    private TextBlock _hwForgetLink = null!;
    private List<string> _lastHardwareKeys = [];
    private readonly TextBlock _conflictSummary = new() { FontSize = 12, Foreground = Theme.Warn, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private Border _rescanButton = null!;

    // Button Art tab
    private readonly ButtonArtPage _buttonArtPage = new();

    // tray
    private WinForms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private bool _trayHintShown;

    public MainWindow()
    {
        Title = $"DeviceMaster v{AppVersion}";
        Width = 1480;
        Height = 860;
        MinWidth = 1280;
        MinHeight = 720;
        WindowState = WindowState.Maximized; // full screen by default (owner preference)
        Background = Theme.Bg;
        if (LoadAppIcon(256) is { } appIcon)
        {
            Icon = appIcon; // taskbar + title bar use this instead of Windows' cached copy of the exe icon
        }
        // Snap element positions and text baselines to whole device pixels so borders and glyph
        // edges don't blur across half-pixel boundaries (inherits to every child control).
        UseLayoutRounding = true;
        try
        {
            Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), Theme.DarkScrollBarStyle());
        }
        catch
        {
            // worst case: default scrollbars
        }

        Grid.SetIsSharedSizeScope(_fanRows, true);
        Content = BuildLayout();
        if (Environment.GetEnvironmentVariable("DEVICEMASTER_PAGE") is { Length: > 0 } startPage
            && _pages.ContainsKey(startPage))
        {
            SelectPage(startPage); // development: screenshot a specific page directly
        }

        _uiReady = true;

        CreateTrayIcon();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            UpdateControlStatus();
            if (_downloading)
            {
                _checkLabel.Text = $"↻  Updating to {_pendingUpdate?.Tag}" + new string('.', 1 + _downloadDots++ % 3);
            }
        };
        timer.Start();

        // Not tied to Loaded — with --minimized the window is never shown, but fan
        // control and the update check must still start.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            ApplyControlState();
            StartAgentApi();
            StartFleetHub(); // only when this machine is configured as a failover hub (#68 step 4)
            await RefreshDevicesAsync();
            await CheckForUpdatesAsync(auto: true);

            // the app lives in the tray for days: keep checking, so a release lands without a restart
            var updateTimer = new DispatcherTimer { Interval = AutoUpdateInterval };
            updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(auto: true);
            updateTimer.Start();
        });

        SourceInitialized += (_, _) =>
        {
            // dark title bar to match the theme
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));

            // Windows 11: title bar + border in the app's own colours instead of the system accent
            // (COLORREF is 0x00BBGGRR). Earlier Windows ignores these attributes.
            var caption = 0x00151110; // Theme.Bg #0f1115
            var text = 0x00F2EEEC;    // Theme.Text #eceef2
            var border = 0x00392F2A;  // Theme.Line #2a2f39
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- layout: left sidebar navigation + visibility-switched pages ----------

    private readonly Dictionary<string, UIElement> _pages = [];
    private ServersPage _serversPage = null!;
    private readonly Dictionary<string, Border> _navButtons = [];
    private string _activePage = "dashboard";

    private UIElement BuildLayout()
    {
        var root = new DockPanel();

        // ---- sidebar ----
        var sidebar = new Border
        {
            Width = 218,
            Background = Theme.Card,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        var side = new DockPanel { Margin = new Thickness(14, 18, 14, 16) };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 22) };
        brand.Children.Add(BuildLogoGlyph()); // the icon is its own rounded tile — no frame around it
        var brandText = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brandText.Children.Add(new TextBlock { Text = "DeviceMaster", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Theme.Text });
        brandText.Children.Add(new TextBlock { Text = "FAN · RGB · LCD", FontSize = 10.5, Foreground = Theme.Faint });
        brand.Children.Add(brandText);
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);

        // ---- sidebar bottom: toggles (uniform styling), then updates at the very bottom ----
        var bottom = new StackPanel();

        static DockPanel ToggleRow(string text, UIElement toggle)
        {
            var row = new DockPanel { Margin = new Thickness(4, 0, 0, 14), LastChildFill = false };
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Text,
                VerticalAlignment = VerticalAlignment.Center,
            });
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            return row;
        }

        // movie mode: every LED and screen dark with one flick, restored on the way back
        bottom.Children.Add(ToggleRow("🌙  Blackout Mode", Theme.Toggle(_controlSettings.BlackoutActive, SetBlackout)));

        bottom.Children.Add(ToggleRow("Auto Start", Theme.Toggle(_controlSettings.StartWithWindows, on =>
        {
            _controlSettings.StartWithWindows = on;
            TrySaveSettings();
            ElevationBroker.SetStartWithWindows(on);
        })));

        bottom.Children.Add(ToggleRow("Start Hidden", Theme.Toggle(_controlSettings.StartHidden, on =>
        {
            _controlSettings.StartHidden = on;
            TrySaveSettings();
        })));

        _checkButton = Theme.Btn("↻  Check for Updates", primary: false, () => _ = CheckForUpdatesAsync(auto: false));
        _checkLabel = (TextBlock)_checkButton.Child;
        _checkButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        bottom.Children.Add(_checkButton);

        bottom.Children.Add(new TextBlock
        {
            Text = $"Version {AppVersion} · {VersionDate}",
            FontSize = 10.5,
            Foreground = Theme.Faint,
            Margin = new Thickness(4, 10, 0, 0),
        });
        DockPanel.SetDock(bottom, Dock.Bottom);
        side.Children.Add(bottom);

        // ---- nav ----
        var navItems = new List<(string Key, string Glyph, string Label)>
        {
            ("dashboard", "⌂", "Dashboard"),
            ("cooling", "❄", "Cooling"),
            ("lighting", "◈", "Lighting"),
            ("turzx", "▤", "Turzx"),
            ("buttonart", "⛶", "Button Art"),
        };

        navItems.Add(("devices", "⚙", "Devices"));

        var nav = new StackPanel();
        foreach (var (key, glyph, label) in navItems)
        {
            var button = NavButton(key, glyph, label);
            _navButtons[key] = button;
            nav.Children.Add(button);
        }

        // fleet (#33): every DeviceMaster machine, straight under the page buttons
        _serversPage = new ServersPage(
            () => _controlSettings.HubUrl,
            url => { _controlSettings.HubUrl = url; TrySaveSettings(); },
            () => SelectPage("servers"),
            Dispatcher);
        nav.Children.Add(_serversPage.SidebarList);

        side.Children.Add(nav);
        sidebar.Child = side;
        DockPanel.SetDock(sidebar, Dock.Left);
        root.Children.Add(sidebar);

        // ---- pages (visibility-switched so live updates keep flowing) ----
        var host = new Grid { Margin = new Thickness(26, 22, 26, 16) };
        _pages["dashboard"] = WrapPage(BuildDashboardPage());
        _pages["cooling"] = FitPage(BuildCoolingPage());
        _pages["lighting"] = WrapPage(BuildLightingPage());
        _pages["turzx"] = WrapPage(BuildTurzxPage());
        _pages["buttonart"] = WrapPage(_buttonArtPage.Build());

        _pages["devices"] = WrapPage(BuildDevicesPage());
        _pages["servers"] = WrapPage(_serversPage.Build());
        foreach (var (key, page) in _pages)
        {
            page.Visibility = key == _activePage ? Visibility.Visible : Visibility.Collapsed;
            host.Children.Add(page);
        }

        root.Children.Add(host);
        RefreshNavStyles();
        return root;

        static UIElement WrapPage(UIElement content) => new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// The Cooling page never scrolls (#118): its two charts share whatever height the window leaves
    /// after the title and the Fan Control row, so everything fits at any window size.
    /// </summary>
    private UIElement FitPage(FrameworkElement content)
    {
        var viewer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        viewer.SizeChanged += (_, _) => FitCoolingCharts(viewer, content);
        if (content is Panel panel)
        {
            content.Loaded += (_, _) =>
            {
                foreach (FrameworkElement child in panel.Children)
                {
                    child.SizeChanged += (_, _) => FitCoolingCharts(viewer, content); // "details" opened, the row wrapped
                }
            };
        }

        return viewer;
    }

    private void FitCoolingCharts(ScrollViewer viewer, FrameworkElement content)
    {
        const double MinChart = 150;
        if (viewer.ActualHeight <= 0 || content is not Panel panel)
        {
            return;
        }

        // everything on the page except the two charts keeps its size; the charts get the rest
        var used = panel.Children.OfType<FrameworkElement>().Sum(c => c.ActualHeight + c.Margin.Top + c.Margin.Bottom);
        var others = used - _curveEditor.ActualHeight - _tempChart.ActualHeight;
        var spare = Math.Max(viewer.ActualHeight - others - 2, 2 * MinChart);
        var curve = Math.Max(MinChart, Math.Floor(spare * 0.5));
        var history = Math.Max(MinChart, Math.Floor(spare - curve));
        if (Math.Abs(_curveEditor.Height - curve) >= 1)
        {
            _curveEditor.Height = curve;
        }

        if (Math.Abs(_tempChart.Height - history) >= 1)
        {
            _tempChart.Height = history;
        }
    }

    private static readonly FontFamily NavGlyphFont = new("Segoe UI Symbol, Segoe UI Emoji, Segoe UI");

    private Border NavButton(string key, string glyph, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        // glyphs come from different symbol fonts with different widths: centre each in a fixed
        // box (same metrics as the web dashboard) so the icon column lines up
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontSize = 16,
            Width = 24,
            Margin = new Thickness(0, 0, 8, 0),
            TextAlignment = TextAlignment.Center,
            FontFamily = NavGlyphFont,
            Foreground = Theme.Dim,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center });
        var button = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
        };
        button.MouseLeftButtonUp += (_, _) => SelectPage(key);
        return button;
    }

    private void SelectPage(string key)
    {
        _activePage = key;
        foreach (var (pageKey, page) in _pages)
        {
            page.Visibility = pageKey == key ? Visibility.Visible : Visibility.Collapsed;
        }

        RefreshNavStyles();
    }

    private void RefreshNavStyles()
    {
        foreach (var (key, button) in _navButtons)
        {
            var selected = key == _activePage;
            button.Background = selected ? Theme.Card2 : Brushes.Transparent;
            var row = (StackPanel)button.Child;
            ((TextBlock)row.Children[0]).Foreground = selected ? Theme.Accent2 : Theme.Dim;
            ((TextBlock)row.Children[1]).Foreground = selected ? Theme.Text : Theme.Dim;
            ((TextBlock)row.Children[1]).FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ---------- pages ----------

    private readonly TextBlock _dashStatus = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(2, 0, 0, 14) };

    private TextBlock _tileFans = null!, _tileFansSub = null!;
    private TextBlock _tileDuty = null!, _tileDutySub = null!;

    // CPU and GPU sections — the same four figures each as the headless web dashboard
    private TextBlock _cpuName = null!, _gpuName = null!;
    private TextBlock _cpuTemp = null!, _cpuTempSub = null!, _cpuLoad = null!, _cpuLoadSub = null!;
    private TextBlock _cpuPower = null!, _cpuPowerSub = null!, _cpuRam = null!, _cpuRamSub = null!;
    private TextBlock _gpuTemp = null!, _gpuTempSub = null!, _gpuLoad = null!, _gpuLoadSub = null!;
    private TextBlock _gpuPower = null!, _gpuPowerSub = null!, _gpuVram = null!, _gpuVramSub = null!;
    private readonly StackPanel _dashWarnings = new() { Margin = new Thickness(2, 12, 0, 0) };

    private readonly System.Windows.Controls.Primitives.UniformGrid _dashFanGrid = new() { Columns = 4 };

    private UIElement BuildDashboardPage()
    {
        var page = new StackPanel(); // full width — the dashboard fills the window
        page.Children.Add(PageTitle("Dashboard", "the whole machine at a glance"));
        page.Children.Add(_dashStatus);

        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        tiles.Children.Add(StatTile("FANS", out _tileFans, out _tileFansSub));
        tiles.Children.Add(StatTile("DUTY", out _tileDuty, out _tileDutySub));
        page.Children.Add(tiles);

        // CPU | GPU, side by side
        var processors = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        var cpuCard = Theme.CardShell("▦", "CPU", "—", out var cpuBody, out var cpuHead);
        _cpuName = CardSubtitle(cpuHead);
        cpuBody.Children.Add(MiniStatRow(
            MiniStat("TEMPERATURE", out _cpuTemp, out _cpuTempSub), MiniStat("LOAD", out _cpuLoad, out _cpuLoadSub),
            MiniStat("POWER", out _cpuPower, out _cpuPowerSub), MiniStat("RAM", out _cpuRam, out _cpuRamSub)));
        cpuCard.Margin = new Thickness(0, 0, 12, 16);
        processors.Children.Add(cpuCard);
        var gpuCard = Theme.CardShell("▤", "GPU", "—", out var gpuBody, out var gpuHead);
        _gpuName = CardSubtitle(gpuHead);
        gpuBody.Children.Add(MiniStatRow(
            MiniStat("TEMPERATURE", out _gpuTemp, out _gpuTempSub), MiniStat("LOAD", out _gpuLoad, out _gpuLoadSub),
            MiniStat("POWER", out _gpuPower, out _gpuPowerSub), MiniStat("VRAM", out _gpuVram, out _gpuVramSub)));
        gpuCard.Margin = new Thickness(0, 0, 12, 16);
        processors.Children.Add(gpuCard);
        page.Children.Add(processors);

        var fanCard = Theme.CardShell("✻", "Fans right now", "every fan with its live speed", out var fanBody, out _);
        fanCard.Margin = new Thickness(0, 4, 12, 0);
        fanBody.Children.Add(_dashFanGrid);
        page.Children.Add(fanCard);

        page.Children.Add(_dashWarnings);
        return page;
    }

    /// <summary>The subtitle line CardShell puts under the title (for live text such as the CPU name).</summary>
    private static TextBlock CardSubtitle(DockPanel head) => (TextBlock)((StackPanel)head.Children[1]).Children[1];

    private static System.Windows.Controls.Primitives.UniformGrid MiniStatRow(params Border[] stats)
    {
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = stats.Length };
        foreach (var stat in stats)
        {
            row.Children.Add(stat);
        }

        return row;
    }

    /// <summary>A stat inside a card: same type scale as StatTile, on the inset colour.</summary>
    private static Border MiniStat(string label, out TextBlock value, out TextBlock sub)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint });
        value = new TextBlock { Text = "—", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Theme.Text, Margin = new Thickness(0, 4, 0, 2) };
        stack.Children.Add(value);
        sub = new TextBlock { Text = "", FontSize = 11.5, Foreground = Theme.Faint };
        stack.Children.Add(sub);
        return new Border
        {
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(16, 13, 16, 13),
            CornerRadius = new CornerRadius(11),
            Background = Theme.Inset,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    /// <summary>Compact name → rpm pill for the dashboard fan grid.</summary>
    private static Border FanPill(string name, string value, Brush valueBrush)
    {
        var row = new DockPanel { LastChildFill = true };
        var val = new TextBlock
        {
            Text = value,
            Foreground = valueBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);
        row.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Theme.Text,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(9),
            Background = Theme.Inset,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    internal static TextBlock PageTitle(string title, string subtitle)
    {
        var block = new TextBlock { Margin = new Thickness(2, 0, 0, 18) };
        block.Inlines.Add(new System.Windows.Documents.Run(title) { FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Theme.Text });
        block.Inlines.Add(new System.Windows.Documents.Run($"   {subtitle}") { FontSize = 12.5, Foreground = Theme.Faint });
        return block;
    }

    private static Border StatTile(string label, out TextBlock value, out TextBlock sub)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint });
        value = new TextBlock { Text = "—", FontSize = 36, FontWeight = FontWeights.Bold, Foreground = Theme.Text, Margin = new Thickness(0, 4, 0, 2) };
        stack.Children.Add(value);
        sub = new TextBlock { Text = "", FontSize = 11.5, Foreground = Theme.Faint };
        stack.Children.Add(sub);
        return new Border
        {
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = new CornerRadius(14),
            Background = Theme.Card,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private FrameworkElement BuildCoolingPage()
    {
        var page = new StackPanel();
        page.Children.Add(PageTitle("Cooling", "fan duty or curve"));
        page.Children.Add(BuildFanCard());

        var curve = BuildCurveCard();
        curve.Margin = new Thickness(0, 16, 0, 0);
        page.Children.Add(curve);

        var history = BuildHistoryCard();
        history.Margin = new Thickness(0, 16, 0, 0);
        page.Children.Add(history);
        RefreshFanModeUi();
        return page;
    }

    private UIElement BuildLightingPage()
    {
        var page = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(PageTitle("Lighting", "static color or rainbow across every LED"));
        page.Children.Add(BuildRgbCard());
        return page;
    }

    // ---------- Turzx 8.8" screen page ----------

    private readonly WrapPanel _turzxButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
    private readonly TextBlock _turzxStatus = new()
    {
        FontSize = 12,
        Foreground = Theme.Dim,
        Margin = new Thickness(0, 14, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };
    private DmSlider _turzxBrightnessSlider = null!;
    private TextBlock _turzxBrightnessLabel = null!;

    private UIElement BuildTurzxPage()
    {
        var page = new StackPanel();
        page.Children.Add(PageTitle("Turzx Screen", "the 8.8\" ultrawide serial display"));

        var card = Theme.CardShell("▤", "Turzx 8.8\"", "backlight and brightness — On shows the CPU · FPS · GPU dashboard", out var body, out _);
        card.HorizontalAlignment = HorizontalAlignment.Left;
        card.MaxWidth = 560;

        RebuildTurzxButtons();
        body.Children.Add(_turzxButtons);

        var brightPanel = new StackPanel { Orientation = Orientation.Horizontal };
        brightPanel.Children.Add(Theme.SmallLabel("Brightness"));
        var initialBrightness = Math.Clamp(_controlSettings.TurzxBrightness, 0, 100);
        _turzxBrightnessSlider = new DmSlider(0, 100, initialBrightness, 300);
        _turzxBrightnessLabel = new TextBlock
        {
            Text = $"{initialBrightness}%",
            FontSize = 13,
            Foreground = Theme.Accent2,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 40,
        };
        _turzxBrightnessSlider.ValueChanged += _ =>
        {
            _controlSettings.TurzxBrightness = (int)Math.Round(_turzxBrightnessSlider.Value);
            _turzxBrightnessLabel.Text = $"{_controlSettings.TurzxBrightness}%";
            TrySaveSettings();
            _loop?.Apply(_controlSettings);
        };
        brightPanel.Children.Add(_turzxBrightnessSlider);
        brightPanel.Children.Add(_turzxBrightnessLabel);
        body.Children.Add(Theme.InsetRow(brightPanel));

        var orientDrop = new DmDropdown(new[] { "Landscape", "Landscape (flipped)" }, _controlSettings.TurzxRotation == 180 ? 1 : 0, 170);
        orientDrop.SelectionChanged += index =>
        {
            _controlSettings.TurzxRotation = index == 1 ? 180 : 0;
            TrySaveSettings();
            _loop?.Apply(_controlSettings);
        };
        body.Children.Add(TurzxLabeledRow("Orientation", orientDrop));

        body.Children.Add(_turzxStatus);
        UpdateTurzxStatus(_loop?.Status);

        page.Children.Add(card);
        return page;
    }

    private static Border TurzxLabeledRow(string label, UIElement control)
    {
        var panel = new DockPanel { LastChildFill = false };
        var text = Theme.SmallLabel(label);
        DockPanel.SetDock(text, Dock.Left);
        DockPanel.SetDock(control, Dock.Right);
        panel.Children.Add(text);
        panel.Children.Add(control);
        return Theme.InsetRow(panel);
    }

    private void RebuildTurzxButtons()
    {
        _turzxButtons.Children.Clear();
        foreach (var (mode, label) in new[]
        {
            (LcdMode.Off, "Off"),
            (LcdMode.Metrics, "On"),
        })
        {
            var selected = _controlSettings.TurzxScreen == mode;
            var button = new Border
            {
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(9),
                Background = selected ? Theme.Card2 : Theme.Inset,
                BorderBrush = selected ? Theme.Accent : Theme.Line2,
                BorderThickness = new Thickness(selected ? 2 : 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 12.5,
                    FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = selected ? Theme.Text : Theme.Dim,
                },
            };
            var chosen = mode;
            button.MouseLeftButtonUp += (_, _) =>
            {
                _controlSettings.TurzxScreen = chosen;
                RebuildTurzxButtons();
                UpdateTurzxStatus(_loop?.Status);
                TrySaveSettings();
                _loop?.Apply(_controlSettings);
            };
            _turzxButtons.Children.Add(button);
        }
    }

    private void UpdateTurzxStatus(ControlStatus? status)
    {
        if (status?.TurzxInfo is { } info)
        {
            _turzxStatus.Text = info;
            _turzxStatus.Foreground = info.StartsWith("Connected", StringComparison.Ordinal) ? Theme.Good : Theme.Dim;
        }
        else if (_controlSettings.TurzxScreen == LcdMode.Unmanaged)
        {
            _turzxStatus.Text = "Not managed — choose Off, On, Black or White to take control of the screen.";
            _turzxStatus.Foreground = Theme.Dim;
        }
        else
        {
            _turzxStatus.Text = "Searching for the Turzx screen…";
            _turzxStatus.Foreground = Theme.Dim;
        }
    }

    private UIElement BuildDevicesPage()
    {
        // full width: the inventory wraps into as many columns as the monitor fits
        var page = new StackPanel();
        page.Children.Add(PageTitle("Devices", "everything detected, with its unique id"));
        page.Children.Add(BuildHardwareCard());
        return page;
    }

    private const double LogoSize = 44;

    private UIElement BuildLogoGlyph()
    {
        if (LoadAppIcon((int)Math.Ceiling(LogoSize * 2)) is { } frame) // 2x so it stays sharp on high-DPI screens
        {
            var image = new Image { Source = frame, Width = LogoSize, Height = LogoSize, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        return new TextBlock { Text = "✻", FontSize = 30, Foreground = Theme.AccentGrad(), VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>
    /// The smallest frame of the embedded DeviceMaster.ico that is at least <paramref name="minPixels"/>
    /// wide (else the largest). Read from the app's own resources, not the shell — Windows' icon
    /// cache keeps showing an old icon after an in-place update.
    /// </summary>
    internal static BitmapFrame? LoadAppIcon(int minPixels)
    {
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/DeviceMaster.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frames = decoder.Frames.OrderBy(f => f.PixelWidth).ToList();
            return frames.FirstOrDefault(f => f.PixelWidth >= minPixels) ?? frames.LastOrDefault();
        }
        catch
        {
            return null; // fall back to the glyph / shell icon
        }
    }

    // the curve source and the curve editor — the web dashboard's Cooling page mirrors these. There is
    // no fixed mode (#116): a constant speed is a curve with both points at the same duty
    private TextBlock _fanSubtitle = null!;
    private StackPanel _sourceRow = null!;
    private readonly StackPanel _sourceChips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _curveReadout = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Accent2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly CurveEditor _curveEditor = new();
    private TextBlock _curveSubtitle = null!;

    private static readonly (CurveSource Source, string Label)[] CurveSources =
        [(CurveSource.Cpu, "CPU"), (CurveSource.Gpu, "GPU"), (CurveSource.Hottest, "CPU/GPU")];

    /// <summary>A row of pill buttons, the selected one filled (same look as the web's segmented control).</summary>
    private static void FillChips(StackPanel host, IReadOnlyList<string> labels, int selected, Action<int> onPick)
    {
        host.Children.Clear();
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var chip = Theme.Btn(labels[i], primary: i == selected, () => onPick(index));
            chip.Margin = new Thickness(0, 0, 6, 0);
            chip.Padding = new Thickness(14, 6, 14, 6);
            host.Children.Add(chip);
        }
    }

    /// <summary>"Cpu" → "CPU" etc. for display (status carries the enum name).</summary>
    private static string SourceLabel(string sourceName) =>
        CurveSources.FirstOrDefault(s => s.Source.ToString() == sourceName).Label ?? sourceName;

    private void RefreshFanModeUi()
    {
        FillChips(_sourceChips, CurveSources.Select(s => s.Label).ToList(),
            Array.FindIndex(CurveSources, s => s.Source == _controlSettings.Source), i =>
            {
                if (_controlSettings.SetSource(CurveSources[i].Source))
                {
                    RefreshFanModeUi();
                    OnControlSettingChanged();
                }
            });

        var sourceLabel = CurveSources.FirstOrDefault(s => s.Source == _controlSettings.Source).Label ?? "CPU/GPU";
        _fanSubtitle.Text = $"curve on {sourceLabel.ToLowerInvariant()} · every fan in the system";
        _curveSubtitle.Text = (_controlSettings.Source == CurveSource.Hottest ? "Hotter of CPU and GPU" : sourceLabel)
            + " temperature → fan duty · drag a point to change it";
        _curveEditor.SetCurve(_controlSettings.CurvePoints);
    }

    private Border BuildCurveCard()
    {
        var card = Theme.CardShell("〜", "Fan Curve", "", out var body, out var head);
        _curveSubtitle = CardSubtitle(head);
        var reset = Theme.Btn("↺  Reset", primary: false, () =>
        {
            _controlSettings.SetCurve(CurveRules.Default);
            RefreshFanModeUi();
            OnControlSettingChanged();
        });
        reset.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);

        _curveEditor.CurveEdited += points =>
        {
            if (_controlSettings.SetCurve(points))
            {
                OnControlSettingChanged();
            }
        };
        body.Children.Add(_curveEditor);
        return card;
    }

    // temperature history card (#113): ~10 minutes of CPU/GPU readings from the 1 Hz status refresh
    private readonly TemperatureHistory _tempHistory = TemperatureHistory.OneHour();
    private readonly TemperatureChart _tempChart = new();
    private readonly StackPanel _historyRangeChips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
    private static readonly (TimeSpan Window, string Label)[] HistoryRanges =
        [(TimeSpan.FromMinutes(1), "1m"), (TimeSpan.FromMinutes(5), "5m"), (TimeSpan.FromMinutes(10), "10m"),
         (TimeSpan.FromMinutes(15), "15m"), (TimeSpan.FromMinutes(30), "30m"), (TimeSpan.FromHours(1), "1h")];
    private TimeSpan _historyWindow = TimeSpan.FromMinutes(10);

    private Border BuildHistoryCard()
    {
        var card = Theme.CardShell("◔", "Temperature History", "CPU and GPU over the last hour", out var body, out var head);
        DockPanel.SetDock(_historyRangeChips, Dock.Right);
        head.Children.Add(_historyRangeChips);
        // legend centred across the whole header row: overlay the head in a Grid so the title and the
        // range chips (different widths) can't pull it off-centre
        var headParent = (Panel)head.Parent;
        var headIndex = headParent.Children.IndexOf(head);
        headParent.Children.RemoveAt(headIndex);
        var headRow = new Grid { Margin = head.Margin };
        head.Margin = new Thickness(0);
        _tempChart.Legend.Margin = new Thickness(0);
        _tempChart.Legend.HorizontalAlignment = HorizontalAlignment.Center;
        headRow.Children.Add(head);
        headRow.Children.Add(_tempChart.Legend);
        headParent.Children.Insert(headIndex, headRow);
        RefreshHistoryRangeChips();
        _tempChart.IsVisibleChanged += (_, _) => RedrawTemperatureHistory();
        body.Children.Add(_tempChart);
        return card;
    }

    private void RefreshHistoryRangeChips()
    {
        FillChips(_historyRangeChips, HistoryRanges.Select(r => r.Label).ToList(),
            Array.FindIndex(HistoryRanges, r => r.Window == _historyWindow), i =>
            {
                _historyWindow = HistoryRanges[i].Window;
                RefreshHistoryRangeChips();
                RedrawTemperatureHistory();
            });
    }

    private void RedrawTemperatureHistory()
    {
        if (_tempChart.IsVisible)
        {
            _tempChart.Show(_tempHistory, DateTimeOffset.Now, _historyWindow);
        }
    }

    private Border BuildFanCard()
    {
        // one row (#113): title, Follows + live readout, CPU/GPU, fans reporting + links, status
        // badge at the right; wraps on a narrow window
        var card = Theme.CardShell("✻", "Fan Control", "every fan in the system", out var body, out var head);
        _fanSubtitle = CardSubtitle(head);
        head.Margin = new Thickness(0);
        head.LastChildFill = true;
        var badge = Theme.StatusBadge("Off", Theme.Faint, out _fanDot, out _fanBadge);
        badge.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);

        var controls = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 0, 0) };
        var itemMargin = new Thickness(0, 4, 28, 4);

        _sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = itemMargin, VerticalAlignment = VerticalAlignment.Center };
        _sourceRow.Children.Add(Theme.SmallLabel("Follows"));
        _sourceRow.Children.Add(_sourceChips);
        _sourceRow.Children.Add(_curveReadout);
        controls.Children.Add(_sourceRow);

        _controlStatus.Margin = itemMargin;
        _controlStatus.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Add(_controlStatus);

        // rolled-up by default: one "N/N fans" line; details expand below the row on demand
        var summaryRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = itemMargin, VerticalAlignment = VerticalAlignment.Center };
        summaryRow.Children.Add(_fanSummary);
        _fanForgetLink = LinkText("forget missing", () =>
        {
            var status = _loop?.Status;
            var present = (status?.Devices ?? [])
                .Select(f => f.Id ?? f.Name).ToHashSet();
            _controlSettings.SeenFanIds.RemoveAll(id => !present.Contains(id));
            TrySaveSettings();
        });
        _fanForgetLink.Visibility = Visibility.Collapsed;
        summaryRow.Children.Add(_fanForgetLink);
        _fanExpandLink = LinkText("▸  details", () =>
        {
            _fansExpanded = !_fansExpanded;
            _fanRows.Visibility = _fansExpanded ? Visibility.Visible : Visibility.Collapsed;
            _fanExpandLink.Text = _fansExpanded ? "▾  hide" : "▸  details";
        });
        summaryRow.Children.Add(_fanExpandLink);
        controls.Children.Add(summaryRow);
        head.Children.Add(controls); // fills the space between the title and the badge

        _fanRows.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(_fanRows);
        body.Children.Add(_fanWarnings); // warnings stay visible even when collapsed
        return card;
    }

    private static TextBlock LinkText(string text, Action onClick)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Theme.Accent2,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.MouseLeftButtonUp += (_, _) => onClick();
        return block;
    }

    /// <summary>A 0–100 brightness slider with a live "%" label, in an inset row.</summary>
    /// <summary>Label column width shared by stacked slider rows, so their bars line up end to end.</summary>
    private const double SliderLabelWidth = 76;

    private static Border BrightnessRow(int initial, Action<int> onChange, double? labelWidth = null, Action<Action<int>>? exposeSetter = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var caption = Theme.SmallLabel("Brightness");
        if (labelWidth is { } w)
        {
            caption.Width = w;
        }

        panel.Children.Add(caption);
        var value = Math.Clamp(initial, 0, 100);
        var slider = new DmSlider(0, 100, value, 300);
        var label = new TextBlock
        {
            Text = $"{value}%",
            FontSize = 13,
            Foreground = Theme.Accent2,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 40,
        };
        slider.ValueChanged += _ =>
        {
            var v = (int)Math.Round(slider.Value);
            label.Text = $"{v}%";
            onChange(v);
        };
        panel.Children.Add(slider);
        panel.Children.Add(label);
        exposeSetter?.Invoke(v => { slider.Value = v; label.Text = $"{Math.Clamp(v, 0, 100)}%"; });
        return Theme.InsetRow(panel);
    }

    private void TrySaveSettings()
    {
        try
        {
            _controlSettings.Save();
        }
        catch
        {
            // non-fatal — settings just won't persist
        }
    }

    // lighting card
    private readonly WrapPanel _swatchPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rgbStatus = new() { FontSize = 12, Foreground = Theme.Dim, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };

    // fully saturated primaries — pastel tones look washed out on LEDs
    // the one shared colour list (Core ColorPalette): the same colours on every DeviceMaster surface
    private static readonly IReadOnlyList<DeviceMaster.Core.Lighting.NamedColor> SwatchColors =
        DeviceMaster.Core.Lighting.ColorPalette.All;

    /// <summary>
    /// One lighting swatch. Selected = a tick in contrasting ink plus a white ring with a gap, so it
    /// shows on every colour (a coloured border vanished on red). Same footprint either way, so the
    /// row doesn't shift when the selection moves.
    /// </summary>
    private static Border Swatch(Brush fill, bool selected, Brush ink, string idleGlyph, string tooltip, Action onPick)
    {
        var tile = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = fill,
            BorderBrush = selected ? Brushes.Transparent : Theme.Line2,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = selected ? "✓" : idleGlyph,
                FontSize = selected ? 16 : 13,
                FontWeight = selected ? FontWeights.Black : FontWeights.Normal,
                Foreground = selected ? ink : Theme.Dim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var ring = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            BorderBrush = selected ? Theme.Text : Brushes.Transparent,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tooltip,
            Child = tile,
        };
        ring.MouseLeftButtonUp += (_, _) => onPick();
        return ring;
    }

    private Border BuildRgbCard()
    {
        var card = Theme.CardShell("◈", "Lighting Control", "static color or rainbow · every LED in the system", out var body, out var head);

        var toggle = Theme.Toggle(_controlSettings.RgbEnabled, on =>
        {
            _controlSettings.RgbEnabled = on;
            OnControlSettingChanged();
        });
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(toggle, Dock.Right);
        head.Children.Add(toggle);

        var effectRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        effectRow.Children.Add(Theme.SmallLabel("Effect"));
        effectRow.Children.Add(_effectChips);
        body.Children.Add(effectRow);

        RebuildSwatches();
        body.Children.Add(_swatchPanel);

        // rainbow speed 1–5, shown only while Rainbow is picked
        _rgbSpeedSlider = new DmSlider(RainbowEffect.MinSpeed, RainbowEffect.MaxSpeed, _controlSettings.RgbSpeed, 300, step: 1);
        var speedLabel = new TextBlock { FontSize = 13, Foreground = Theme.Accent2, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 60 };
        void ShowSpeed() => speedLabel.Text = SpeedName(_controlSettings.RgbSpeed);
        ShowSpeed();
        _rgbSpeedSlider.ValueChanged += _ =>
        {
            _controlSettings.RgbSpeed = (int)Math.Round(_rgbSpeedSlider.Value);
            ShowSpeed();
            OnControlSettingChanged();
        };
        var speedPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var speedCaption = Theme.SmallLabel("Speed");
        speedCaption.Width = SliderLabelWidth; // same label column as Brightness: the bars line up
        speedPanel.Children.Add(speedCaption);
        speedPanel.Children.Add(_rgbSpeedSlider);
        speedPanel.Children.Add(speedLabel);
        _rgbSpeedRow = Theme.InsetRow(speedPanel);
        _rgbSpeedRow.Margin = new Thickness(0, 14, 0, 4); // clear of the swatches above
        body.Children.Add(_rgbSpeedRow);
        RefreshRgbEffectUi();

        var rgbBrightness = BrightnessRow(_controlSettings.RgbBrightness, v =>
        {
            _controlSettings.RgbBrightness = v;
            OnControlSettingChanged(); // saves + re-applies the (now dimmed) color to every LED
        }, SliderLabelWidth, set => _setRgbBrightness = set);
        rgbBrightness.Margin = new Thickness(0, 14, 0, 4); // separate it from the swatches above
        body.Children.Add(rgbBrightness);
        body.Children.Add(_rgbStatus);
        UpdateRgbStatusText();
        return card;
    }

    private readonly StackPanel _effectChips = new() { Orientation = Orientation.Horizontal };
    private DmSlider _rgbSpeedSlider = null!;
    private Border _rgbSpeedRow = null!;

    private static string SpeedName(int speed) => Math.Clamp(speed, 1, 5) switch
    {
        1 => "Slowest",
        2 => "Slow",
        3 => "Medium",
        4 => "Fast",
        _ => "Fastest",
    };

    /// <summary>Static shows the colour swatches; Rainbow and GTA 6 dim them and show the speed (the web page does the same).</summary>
    private void RefreshRgbEffectUi()
    {
        var rainbow = _controlSettings.RgbEffect != RgbEffect.Static; // any moving effect
        FillChips(_effectChips, ["Static", "Rainbow", "GTA 6"], (int)_controlSettings.RgbEffect, i =>
        {
            _controlSettings.RgbEffect = (RgbEffect)i;
            if (i != 0)
            {
                _controlSettings.RgbOff = false; // "lights out" and an effect are opposites
            }

            RefreshRgbEffectUi();
            RebuildSwatches();
            OnControlSettingChanged();
        });
        _rgbSpeedRow.Visibility = rainbow ? Visibility.Visible : Visibility.Collapsed;
        _swatchPanel.Opacity = rainbow ? 0.45 : 1.0;
    }

    private void RebuildSwatches()
    {
        _swatchPanel.Children.Clear();

        // "lights out" tile — actively paints every LED black (≠ the toggle, which stops
        // writing and lets devices fall back to their own effects)
        _swatchPanel.Children.Add(Swatch(
            new SolidColorBrush(Color.FromRgb(10, 10, 14)), _controlSettings.RgbOff, Theme.Text, "⊘",
            "Lights out — every LED off", () =>
            {
                _controlSettings.RgbOff = true;
                _controlSettings.RgbEffect = RgbEffect.Static;
                RefreshRgbEffectUi();
                RebuildSwatches();
                OnControlSettingChanged();
            }));

        foreach (var c in SwatchColors)
        {
            var selected = !_controlSettings.RgbOff && _controlSettings.RgbEffect == RgbEffect.Static
                && _controlSettings.RgbR == c.R && _controlSettings.RgbG == c.G && _controlSettings.RgbB == c.B;
            _swatchPanel.Children.Add(Swatch(
                new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), selected, c.DarkInk ? Brushes.Black : Brushes.White, "",
                c.Name, () =>
                {
                    _controlSettings.RgbOff = false;
                    _controlSettings.RgbEffect = RgbEffect.Static; // picking a colour means a static colour
                    RefreshRgbEffectUi();
                    _controlSettings.RgbR = c.R;
                    _controlSettings.RgbG = c.G;
                    _controlSettings.RgbB = c.B;
                    RebuildSwatches();
                    OnControlSettingChanged();
                }));
        }
    }

    private void UpdateRgbStatusText()
    {
        if (!_controlSettings.RgbEnabled)
        {
            _rgbStatus.Text = "Lighting control off — devices keep their own colors.";
        }
        else if (_controlSettings.Mode == ControlMode.Off)
        {
            _rgbStatus.Text = "Waiting for fan control to start before applying lighting.";
        }
        else if (_controlSettings.RgbOff)
        {
            _rgbStatus.Text = "Lights out — every LED on Corsair, Lian Li, motherboard ARGB, RAM and GPU is dark.";
        }
        else if (_controlSettings.RgbEffect == RgbEffect.Rainbow)
        {
            _rgbStatus.Text = $"Rainbow ({SpeedName(_controlSettings.RgbSpeed).ToLowerInvariant()}) on Corsair, Lian Li, motherboard ARGB, RAM and GPU LEDs.";
        }
        else if (_controlSettings.RgbEffect == RgbEffect.Gta6)
        {
            _rgbStatus.Text = $"GTA 6 ({SpeedName(_controlSettings.RgbSpeed).ToLowerInvariant()}): the GTA VI logo's stripes, orange to blue, on Corsair, "
                + "Lian Li, motherboard ARGB, RAM and GPU LEDs.";
        }
        else
        {
            _rgbStatus.Text = $"Static color #{_controlSettings.RgbR:X2}{_controlSettings.RgbG:X2}{_controlSettings.RgbB:X2} "
                + "on Corsair, Lian Li, motherboard ARGB, RAM and GPU LEDs.";
        }
    }

    private Border BuildHardwareCard()
    {
        var card = Theme.CardShell("⚙", "Detected hardware", "every device with its unique id", out var body, out var head);
        _rescanButton = Theme.Btn("↻  Rescan", primary: false, () => _ = RefreshDevicesAsync());
        DockPanel.SetDock(_rescanButton, Dock.Right);
        head.Children.Add(_rescanButton);

        var summaryRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _hwForgetLink = LinkText("forget missing", () =>
        {
            _controlSettings.SeenDeviceIds.RemoveAll(id => !_lastHardwareKeys.Contains(id));
            TrySaveSettings();
            _ = RefreshDevicesAsync();
        });
        _hwForgetLink.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_hwForgetLink, Dock.Right);
        summaryRow.Children.Add(_hwForgetLink);
        summaryRow.Children.Add(_hwSummary);
        body.Children.Add(summaryRow);

        body.Children.Add(_hardwareRows);
        body.Children.Add(_conflictSummary);
        return card;
    }

    /// <summary>Compact two-per-row chip for the hardware inventory.</summary>
    private static Border HardwarePill(string name, string? id, string tag)
    {
        var stack = new StackPanel();
        var top = new DockPanel { LastChildFill = false };
        var title = new TextBlock
        {
            Text = name,
            Foreground = Theme.Text,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 210,
        };
        DockPanel.SetDock(title, Dock.Left);
        top.Children.Add(title);
        var badge = new TextBlock
        {
            Text = tag,
            Foreground = Theme.Dim,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(badge, Dock.Right);
        top.Children.Add(badge);
        stack.Children.Add(top);
        stack.Children.Add(new TextBlock
        {
            Text = id ?? "?",
            Foreground = Theme.Faint,
            FontFamily = Theme.Mono,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return new Border
        {
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(9),
            Background = Theme.Inset,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    /// <summary>A softly pulsing placeholder row shown while a scan/startup is in flight.</summary>
    private static TextBlock LoadingRow(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = Theme.Dim,
            FontSize = 12.5,
            Margin = new Thickness(2, 6, 0, 6),
        };
        block.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        });
        return block;
    }

    // Rows are 3-column grids; the id and value columns share size groups (scoped per card
    // panel) so every row's full device id lines up in one central column.
    private static Border DeviceRow(string name, string? id, string value, Brush valueBrush)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "DeviceId" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "DeviceValue" });

        row.Children.Add(new TextBlock { Text = name, Foreground = Theme.Text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });

        if (id is { Length: > 0 })
        {
            var idBlock = new TextBlock
            {
                Text = id,
                Foreground = Theme.Faint,
                FontFamily = Theme.Mono,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 1, 14, 0),
            };
            Grid.SetColumn(idBlock, 1);
            row.Children.Add(idBlock);
        }

        var right = new TextBlock
        {
            Text = value,
            Foreground = valueBrush,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return Theme.InsetRow(row);
    }

    // ---------- system tray ----------

    private void CreateTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon { Text = "DeviceMaster", Visible = true };
        try
        {
            _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        }
        catch
        {
            // no icon in odd hosting scenarios — the tray entry still works
        }

        // single left-click opens the window; right-click keeps the context menu
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                ShowFromTray();
            }
        };
        var menu = new WinForms.ContextMenuStrip();
        var open = menu.Items.Add("Open DeviceMaster", null, (_, _) => ShowFromTray());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
    }

    internal void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Maximized;
        Activate();
    }

    /// <summary>The window close button hides to the tray — fan control keeps running.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown && _trayIcon is not null)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(2500, "DeviceMaster",
                    "Still running in the tray — fan control stays active. Right-click the icon to exit.",
                    WinForms.ToolTipIcon.Info);
            }

            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Releases the tray icon and hardware; the caller decides what happens next.</summary>
    private void PrepareExit()
    {
        _exitRequested = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        StopFleetHub(); // a standby hub elsewhere takes over
        _agentServer?.Dispose(); // no fleet writes while shutting down
        _agentServer = null;
        _loop?.Stop(); // Corsair hubs back to hardware mode; SL V3 reverts on its own
        _loop = null;
    }

    private void ExitApplication()
    {
        PrepareExit();
        Application.Current.Shutdown();
    }

    // ---------- device scan ----------

    private async Task RefreshDevicesAsync()
    {
        _rescanButton.IsHitTestVisible = false;
        _rescanButton.Opacity = 0.5;
        if (_hardwareRows.Children.Count == 0)
        {
            _hwSummary.Text = "Scanning for devices…";
            _hwSummary.Foreground = Theme.Dim;
            _hardwareRows.Children.Add(LoadingRow("Scanning for devices…"));
        }

        var hardwareKeys = new List<string>();
        var pills = new List<(string Group, Border Pill)>();

        void AddPill(string group, string name, string? displayId, string tag, string? key = null)
        {
            hardwareKeys.Add(key ?? displayId ?? name);
            pills.Add((group, HardwarePill(name, displayId, tag)));
        }

        static string ChainGroup(string name) =>
            name.StartsWith("Corsair", StringComparison.OrdinalIgnoreCase) ? "Corsair"
            : name.StartsWith("Lian Li", StringComparison.OrdinalIgnoreCase) ? "Lian Li"
            : name.StartsWith("Motherboard", StringComparison.OrdinalIgnoreCase) ? "Motherboard"
            : name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) ? "GPU"
            : "Other";

        try
        {
            // Link-chain devices (fans) aren't USB devices — take them from the running
            // control loop, or do a one-shot chain scan while control is off.
            var chainRows = new List<(string Name, string? Id, string Tag)>();
            var chainPending = false;
            var snapshot = _loop?.Status;
            if (snapshot is { Running: true })
            {
                foreach (var device in snapshot.Devices)
                {
                    var tag = device.Family switch
                    {
                        "Corsair" => "fan",
                        "Lian Li" => "wireless",
                        "Motherboard" => "header",
                        "GPU" => "fan",
                        _ => "?",
                    };
                    chainRows.Add(($"{device.Family} · {device.Name}", device.Id, tag));
                }
            }
            else if (_controlSettings.Mode != ControlMode.Off)
            {
                chainPending = true; // the loop is still starting — rows appear automatically
            }
            else if (Environment.GetEnvironmentVariable("DEVICEMASTER_NO_CHAINSCAN") is { Length: > 0 })
            {
                chainPending = true; // development: never touch the hubs from a second instance
            }

            var (scan, conflicts) = await Task.Run(() =>
            {
                var result = (Scan: DeviceScanner.ScanAll(), Conflicts: ConflictingSoftwareChecker.FindConflicts());
                if (chainRows.Count == 0 && !chainPending)
                {
                    // control is off — safe to open the hubs briefly for a chain scan
                    foreach (var hidDevice in LinkHub.FindHubDevices())
                    {
                        try
                        {
                            using var hub = LinkHub.Open(hidDevice);
                            hub.EnumerateChannels(allowEnterSoftwareMode: true);
                            foreach (var channel in hub.Channels)
                            {
                                chainRows.Add(($"Corsair · {channel.Name} (ch{channel.Channel})", channel.Id,
                                    channel.IsKnown ? "fan" : "?"));
                            }

                            if (hub.InSoftwareMode)
                            {
                                hub.EnterHardwareMode();
                            }
                        }
                        catch
                        {
                            // hub busy or unplugged — USB-level rows still show it
                        }
                    }
                }

                return result;
            });

            _hardwareRows.Children.Clear();

            foreach (var hub in scan.HidDevices
                .Where(d => d.Kind == DeviceKind.CorsairLinkHub && d.MaxOutputReportLength > 0)
                .OrderBy(d => d.SerialNumber, StringComparer.Ordinal))
            {
                AddPill("Corsair", "Corsair iCUE LINK hub", Shorten(hub.SerialNumber), "HID", hub.SerialNumber);
            }

            foreach (var (name, id, tag) in chainRows)
            {
                AddPill(ChainGroup(name), name, Shorten(id), tag, id ?? name);
            }

            if (chainPending)
            {
                _hardwareRows.Children.Add(new TextBlock
                {
                    Text = "Link chain devices appear once fan control has started…",
                    Foreground = Theme.Faint,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 4, 0, 7),
                });
            }

            foreach (var dongle in scan.UsbTree
                .Where(n => n.IsPhysicalDevice && n.UsbId is { } id
                    && KnownDeviceRegistry.Identify(id)?.Kind == DeviceKind.LianLiSlv3Controller)
                .OrderBy(n => n.UsbId!.Value.Pid))
            {
                var role = dongle.UsbId!.Value.Pid == 0x8040 ? "TX · control" : "RX · telemetry";
                AddPill("Lian Li", $"Lian Li SL V3 dongle ({role})", dongle.UsbId.ToString(), "WinUSB");
            }

            foreach (var screen in scan.SerialPorts.Where(p => p.Identification?.Kind == DeviceKind.TurzxScreen))
            {
                AddPill("Other", "Turzx/Turing screen", screen.SerialHint, screen.ComPort);
            }

            foreach (var aura in scan.HidDevices.Where(d => d.Kind == DeviceKind.MotherboardRgbController)
                .DistinctBy(d => d.UsbId))
            {
                AddPill("Motherboard", "ASUS Aura LED controller", aura.UsbId.ToString(), "HID");
            }

            foreach (var gpu in _loop?.GpuInventory ?? [])
            {
                AddPill("GPU",
                    gpu.Gpu.Name,
                    $"{gpu.Gpu.SubVendor:X4}:{gpu.Gpu.SubDevice:X4} · {gpu.Partner}",
                    gpu.Ene is not null ? "RGB" : "GPU",
                    $"{gpu.Gpu.SubVendor:X4}:{gpu.Gpu.SubDevice:X4}");
            }

            foreach (var stick in _loop?.RamInventory ?? [])
            {
                AddPill("RAM",
                    $"RAM · {stick.Manufacturer} {stick.PartNumber}",
                    $"SPD 0x{stick.SpdAddress:X2} · {stick.BusName}",
                    "SMBus");
            }

            // roll-up: learn new identities, flag remembered ones that vanished
            _lastHardwareKeys = hardwareKeys;
            var learned = false;
            foreach (var key in hardwareKeys)
            {
                if (!_controlSettings.SeenDeviceIds.Contains(key))
                {
                    _controlSettings.SeenDeviceIds.Add(key);
                    learned = true;
                }
            }

            if (learned)
            {
                TrySaveSettings();
            }

            var missingDevices = _controlSettings.SeenDeviceIds.Except(hardwareKeys).ToList();
            _hwSummary.Text = $"{hardwareKeys.Count}/{_controlSettings.SeenDeviceIds.Count} devices detected"
                + (chainPending ? " · fan chain still starting" : "");
            _hwSummary.Foreground = missingDevices.Count > 0 ? Theme.Warn : Theme.Text;
            _hwForgetLink.Visibility = missingDevices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var key in missingDevices)
            {
                pills.Add(("Missing", HardwarePill("Remembered device — not detected", Shorten(key), "missing")));
            }

            // auto-grouped rendering: family headers with counts, three pills per row
            foreach (var group in new[] { "Missing", "Corsair", "Lian Li", "Motherboard", "RAM", "GPU", "Other" })
            {
                var members = pills.Where(p => p.Group == group).Select(p => p.Pill).ToList();
                if (members.Count == 0)
                {
                    continue;
                }

                _hardwareRows.Children.Add(new TextBlock
                {
                    Text = $"{group}  ·  {members.Count}",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = group == "Missing" ? Theme.Warn : Theme.Dim,
                    Margin = new Thickness(2, 10, 0, 6),
                });
                var wrap = new WrapPanel();
                foreach (var pill in members)
                {
                    pill.Width = 306; // fixed pill width — the panel wraps to fill the window
                    wrap.Children.Add(pill);
                }

                _hardwareRows.Children.Add(wrap);
            }

            if (pills.Count == 0)
            {
                _hardwareRows.Children.Add(new TextBlock { Text = "No supported devices found.", Foreground = Theme.Dim, FontSize = 12.5 });
            }

            if (conflicts.Count > 0)
            {
                _conflictSummary.Text = "⚠  Vendor software is running and may fight over devices: "
                    + string.Join(", ", conflicts.Select(c => c.Name).Distinct());
                _conflictSummary.Visibility = Visibility.Visible;
            }
            else
            {
                _conflictSummary.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _hardwareRows.Children.Clear();
            _hardwareRows.Children.Add(new TextBlock { Text = $"Scan failed: {ex.Message}", Foreground = Theme.Danger, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        }
        finally
        {
            _rescanButton.IsHitTestVisible = true;
            _rescanButton.Opacity = 1.0;
            FitMinSizeToContent();
        }
    }

    /// <summary>
    /// Raises the window minimum height until the card grid — the detected-hardware list is
    /// the tallest column — fits without the outer ScrollViewer scrolling, capped to the
    /// monitor work area. Re-run after every hardware refresh because the list length
    /// depends on what's plugged in.
    /// </summary>
    private void FitMinSizeToContent()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Content is not FrameworkElement root)
            {
                return;
            }

            var contentWidth = root.ActualWidth > 0 ? root.ActualWidth : Width;
            root.Measure(new Size(contentWidth, double.PositiveInfinity));

            // window chrome (title bar + resize frame) from the current arrangement;
            // before first show fall back to the standard caption height
            var chrome = ActualHeight > 0 && root.ActualHeight > 0
                ? Math.Max(ActualHeight - root.ActualHeight, 0)
                : SystemParameters.WindowCaptionHeight + 2 * SystemParameters.ResizeFrameHorizontalBorderHeight;

            MinHeight = Math.Min(root.DesiredSize.Height + chrome, SystemParameters.WorkArea.Height);
            if (WindowState == WindowState.Normal && Height < MinHeight)
            {
                Height = MinHeight;
            }
        }, DispatcherPriority.Loaded);
    }

    private static string? Shorten(string? id) =>
        id is { Length: > 14 } ? id[..14] + "…" : id;

    // ---------- fan control wiring ----------

    // ---------- fleet agent API (#33) ----------

    private AgentServer? _agentServer;

    /// <summary>The window as the agent API's host: every read and write hops onto the UI thread.</summary>
    private sealed class AgentHost(MainWindow window) : IAgentHost
    {
        public ControlStatus Status => window._loop?.Status ?? new ControlStatus();

        public FleetProfile ReadProfile() => window.Dispatcher.Invoke(() => FleetProfile.From(window._controlSettings));

        public bool ApplyProfile(FleetProfile profile) => window.Dispatcher.Invoke(() => window.ApplyRemoteProfile(profile));

        // a fleet "power off": Windows' own clean shutdown after a 60 s grace (cancellable)
        public string? RequestShutdown(string reason)
        {
            LogLine($"fleet: shutdown requested ({reason}) — Windows shuts down in 60 s");
            return RunShutdown($"/s /t 60 /c \"DeviceMaster fleet: shutting down in 60 seconds ({reason.Replace("\"", "'")})\"") ? "Windows shutdown in 60 s" : null;
        }

        // a fleet "reboot": Windows' own clean restart after the same 60 s grace (cancellable)
        public string? RequestReboot(string reason)
        {
            LogLine($"fleet: reboot requested ({reason}) — Windows restarts in 60 s");
            return RunShutdown($"/r /t 60 /c \"DeviceMaster fleet: restarting in 60 seconds ({reason.Replace("\"", "'")})\"") ? "Windows restart in 60 s" : null;
        }

        public bool CancelShutdown() => RunShutdown("/a");

        private static bool RunShutdown(string args)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", args) { CreateNoWindow = true, UseShellExecute = false })!;
                p.WaitForExit(5_000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public string? ReadSettingsJson() =>
            window.Dispatcher.Invoke(() => System.Text.Json.JsonSerializer.Serialize(window._controlSettings, AgentApi.Json));

        // the full per-server page on the hub edits this machine through here; the window re-syncs
        public IReadOnlyList<string>? PatchSettings(string patchJson, out bool invalid)
        {
            var bad = false;
            var applied = window.Dispatcher.Invoke(() =>
            {
                var changes = ConfigPatcher.ApplyPatch(window._controlSettings, patchJson, out bad);
                if (changes.Count > 0)
                {
                    window.TrySaveSettings();
                    window.ReloadControlsFromSettings();
                    window._loop?.Apply(window._controlSettings);
                }

                return changes;
            });
            invalid = bad;
            return applied;
        }

        // now = false: the 15-minute check's path (installs silently, but waits while a game runs);
        // now = true: exactly the Check for Updates button, pressed from the fleet hub (#110)
        public bool RequestUpdateCheck(bool now)
        {
            if (now)
            {
                LogLine("update: Check for Updates pressed from the fleet hub");
            }

            _ = window.Dispatcher.InvokeAsync(() => window.CheckForUpdatesAsync(auto: !now));
            return true;
        }
    }

    private void StartAgentApi()
    {
        if (NoControl || _agentServer is not null)
        {
            return;
        }

        try
        {
            var tokenPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ControlSettings.ConfigPath)!, "agent-token");
            var api = new AgentApi(new AgentHost(this), AgentToken.LoadOrCreate(tokenPath, LogLine), LogLine);
            _agentServer = new AgentServer(api, LogLine);
            if (!_agentServer.Start())
            {
                _agentServer.Dispose();
                _agentServer = null;
            }
            else if (_agentServer.Listening.Count > 1)
            {
                EnsureAgentFirewallRule();
            }
        }
        catch (Exception ex)
        {
            LogLine($"agent api: not started ({ex.Message})"); // fleet control unavailable; local control unaffected
        }
    }

    // ---- fleet hub in the failover group (#68 step 4) ----
    // Runs only when %LOCALAPPDATA%\DeviceMaster\hub\hub.json exists (same format as the Linux
    // hubs': Self, Priority, Peers, PeerTokenFile, PeerPort, Agents with absolute TokenFile paths).
    // Lowest in the failover order, so it only takes over when every Linux hub is down.
    private FleetHub? _fleetHub;
    private HubFailover? _hubFailover;
    private DeviceMaster.Hub.HubWebServer? _hubWeb;
    private const string HubFirewallRule = "DeviceMaster fleet hub peers";

    private void StartFleetHub()
    {
        if (NoControl || _fleetHub is not null)
        {
            return;
        }

        var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ControlSettings.ConfigPath)!, "hub");
        var configPath = System.IO.Path.Combine(dir, "hub.json");
        if (!System.IO.File.Exists(configPath))
        {
            return; // no hub on this machine
        }

        try
        {
            var config = HubConfig.Load(configPath);
            string? Resolve(string? path) => path is null ? null : System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(dir, path);
            var tokenFile = Resolve(config.PeerTokenFile);
            var peerToken = tokenFile is not null && System.IO.File.Exists(tokenFile) ? System.IO.File.ReadAllText(tokenFile).Trim() : null;
            if (config.Peers.Count > 0 && (config.Self is null || peerToken is null))
            {
                LogLine("fleet hub: hub.json has Peers but no Self or readable PeerTokenFile — not started");
                return;
            }

            var store = new FleetStore(System.IO.Path.Combine(dir, "fleet-state.json"));
            _fleetHub = new FleetHub(config with { Agents = config.Agents.Select(a => a with { TokenFile = Resolve(a.TokenFile)! }).ToList() }, LogLine, store: store);
            _hubFailover = new HubFailover(config.Self ?? Environment.MachineName, config.Priority, config.Peers, store, peerToken, LogLine);
            var hub = _fleetHub;
            hub.IsActive = () => _hubFailover.IsActive;
            _hubFailover.RoleChanged += role =>
            {
                if (role == HubRole.Active)
                {
                    hub.ForgetPushes(); // re-assert the fleet settings: the previous hub's pushes aren't ours to trust
                    hub.Nudge();
                }
            };

            // automations (#83): bearer tokens that may change settings/power, audited under their own names
            var automations = HubAutomations.Load(config.Automations.Select(x => x with { TokenFile = Resolve(x.TokenFile) ?? "" }), LogLine);
            _hubWeb = new DeviceMaster.Hub.HubWebServer(hub, 27080, LogLine, ["127.0.0.1"], _hubFailover, config.PeerPort, automations);
            hub.Start();
            _hubFailover.Start();
            _hubWeb.Start();
            if (_hubFailover.HasPeers)
            {
                EnsureFirewallRule(HubFirewallRule, config.PeerPort, "fleet hub peers");
            }
        }
        catch (Exception ex)
        {
            LogLine($"fleet hub: not started ({ex.Message})");
            StopFleetHub();
        }
    }

    private void StopFleetHub()
    {
        _hubWeb?.Dispose();
        _hubFailover?.Dispose();
        _fleetHub?.Dispose();
        _hubWeb = null;
        _hubFailover = null;
        _fleetHub = null;
    }

    private const string AgentFirewallRule = "DeviceMaster fleet agent";

    /// <summary>
    /// Inbound allow for the agent port from Tailscale addresses only (100.64.0.0/10) — never the
    /// LAN. The app runs elevated; the installer does not, so the rule is added here, once.
    /// </summary>
    private void EnsureAgentFirewallRule() => EnsureFirewallRule(AgentFirewallRule, AgentServer.DefaultPort, "agent api");

    /// <summary>Inbound allow on one TCP port from Tailscale addresses only (100.64.0.0/10), added once.</summary>
    private void EnsureFirewallRule(string ruleName, int port, string what)
    {
        static (int Code, string Output) Netsh(string args)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, output);
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (Netsh($"advfirewall firewall show rule name=\"{ruleName}\"").Code == 0)
                {
                    return; // already there
                }

                var (code, output) = Netsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow "
                    + $"protocol=TCP localport={port} remoteip=100.64.0.0/10 profile=any");
                LogLine(code == 0
                    ? $"{what}: firewall rule added (TCP {port} from Tailscale addresses only)"
                    : $"{what}: could not add the firewall rule ({output.Trim()}) — other machines may not reach this one");
            }
            catch (Exception ex)
            {
                LogLine($"{what}: firewall rule check failed: {ex.Message}");
            }
        });
    }

    // slider setters for the brightness rows (built by BrightnessRow, which keeps its own slider)
    private Action<int>? _setRgbBrightness;

    /// <summary>
    /// Fleet (#33): applies a profile pushed by the hub through the agent API. Runs on the UI
    /// thread: the profile lands in the same settings object the pages edit, is saved, goes to the
    /// loop, and every control is re-synced so the window shows what the machine now runs.
    /// </summary>
    internal bool ApplyRemoteProfile(FleetProfile profile)
    {
        if (!profile.ApplyTo(_controlSettings))
        {
            return false;
        }

        TrySaveSettings();
        ReloadControlsFromSettings();
        _loop?.Apply(_controlSettings);
        return true;
    }

    private void ReloadControlsFromSettings()
    {
        _rgbSpeedSlider.Value = _controlSettings.RgbSpeed;
        _turzxBrightnessSlider.Value = Math.Clamp(_controlSettings.TurzxBrightness, 0, 100);
        _turzxBrightnessLabel.Text = $"{Math.Clamp(_controlSettings.TurzxBrightness, 0, 100)}%";
        _setRgbBrightness?.Invoke(_controlSettings.RgbBrightness);
        RefreshFanModeUi();
        RefreshRgbEffectUi();
        RebuildSwatches();
        RebuildTurzxButtons();
        UpdateRgbStatusText();
    }

    private void OnControlSettingChanged()
    {
        if (!_uiReady)
        {
            return;
        }

        try
        {
            _controlSettings.Save();
        }
        catch
        {
            // non-fatal — settings just won't persist
        }

        UpdateRgbStatusText();
        ApplyControlState();
    }

    /// <summary>
    /// Movie mode: blacks out every LED (actively — Lian Li fans would rainbow if we merely
    /// stopped) and turns the Turzx screen's backlight off; toggling back restores what was set.
    /// </summary>
    private void SetBlackout(bool on)
    {
        if (!_controlSettings.SetBlackout(on))
        {
            return;
        }

        TrySaveSettings();
        RebuildSwatches();
        RebuildTurzxButtons();
        UpdateRgbStatusText();
        _loop?.Apply(_controlSettings);
    }

    /// <summary>Development sandbox: never start the control loop (screenshot iteration).</summary>
    private static readonly bool NoControl =
        Environment.GetEnvironmentVariable("DEVICEMASTER_NO_CONTROL") is { Length: > 0 };

    private void ApplyControlState()
    {
        // fan control is always on, following the curve (a constant speed is a flat curve, #116).
        // Off stays retired from the UI (the enum survives in settings for config compatibility).
        if (_controlSettings.Mode == ControlMode.Off)
        {
            _controlSettings.Mode = ControlMode.Curve;
            TrySaveSettings();
        }

        if (NoControl)
        {
            UpdateControlStatus();
            return;
        }

        if (_loop is null)
        {
            _loop = new ControlLoop(_controlSettings, LogLine);
            _loop.Start();
        }
        else
        {
            _loop.Apply(_controlSettings);
        }
    }

    private bool _chainRowsLive;
    private bool _showingStartupSearch;

    private void UpdateControlStatus()
    {
        var status = _loop?.Status;
        if (status is { Running: true } && !_chainRowsLive)
        {
            // first tick after the loop came up — refresh the hardware list so chain devices appear
            _chainRowsLive = true;
            _ = RefreshDevicesAsync();
        }

        UpdateDashboard(status);
        UpdateTurzxStatus(status);

        if (status is { Running: true })
        {
            _tempHistory.Add(DateTimeOffset.Now, status.CpuTemperatureC, status.GpuTemperatureC);
        }

        RedrawTemperatureHistory(); // every tick, so the time axis keeps moving through a gap

        if (status is null || !status.Running)
        {
            _chainRowsLive = false;
            if (_loop is not null && _controlSettings.Mode != ControlMode.Off)
            {
                // the loop is starting: hubs, dongles and sensors take a few seconds to open
                if (!_showingStartupSearch)
                {
                    _showingStartupSearch = true;
                    SetBadge("Starting", Theme.Accent2);
                    _controlStatus.Text = "Starting fan control…";
                    _controlStatus.Foreground = Theme.Dim;
                    _fanSummary.Text = "Searching for fans…";
                    _fanSummary.Foreground = Theme.Dim;
                    _fanForgetLink.Visibility = Visibility.Collapsed;
                    _fanRows.Children.Clear();
                    _fanRows.Children.Add(LoadingRow("Searching for fans…"));
                    _fanWarnings.Children.Clear();
                }

                return;
            }

            _showingStartupSearch = false;
            SetBadge("Off", Theme.Faint);
            _controlStatus.Text = "Control off — devices follow their own hardware/firmware curves.";
            _controlStatus.Foreground = Theme.Dim;
            _fanSummary.Text = "";
            _fanForgetLink.Visibility = Visibility.Collapsed;
            _fanRows.Children.Clear();
            _fanWarnings.Children.Clear();
            return;
        }

        _showingStartupSearch = false;

        var temp = status.SourceTemperatureC is { } t ? $"{t:F1} °C" : "—";
        if (status.FailsafeActive)
        {
            SetBadge("FAILSAFE", Theme.Danger);
            _controlStatus.Text = $"{status.SourceName} unavailable — all fans at 100%";
            _controlStatus.Foreground = Theme.Warn;
        }
        else
        {
            SetBadge("Running", Theme.Good);
            _curveReadout.Text = $"{temp} → {status.TargetDutyPercent}%";
            _controlStatus.Text =
                $"CPU {(status.CpuTemperatureC is { } ct ? $"{ct:F0} °C" : "—")}"
                + $"   ·   GPU {(status.GpuTemperatureC is { } gt ? $"{gt:F0} °C" : "—")}";
            _controlStatus.Foreground = Theme.Text;
        }

        // the "now" marker: where the curve puts the fans right now
        var curveTemp = _controlSettings.Source switch
        {
            CurveSource.Cpu => status.CpuTemperatureC,
            CurveSource.Gpu => status.GpuTemperatureC,
            CurveSource.Hottest => status.SourceTemperatureC, // the loop's smoothed value
            _ => null,
        };
        _curveEditor.SetLive(status.FailsafeActive ? null : curveTemp, status.TargetDutyPercent);
        _curveEditor.SetSensors(status.CpuTemperatureC, status.GpuTemperatureC);

        var fans = status.Devices.ToList();
        var presentIds = fans.Select(f => f.Id ?? f.Name).Distinct().ToList();
        var learnedNew = false;
        foreach (var id in presentIds)
        {
            if (!_controlSettings.SeenFanIds.Contains(id))
            {
                _controlSettings.SeenFanIds.Add(id);
                learnedNew = true;
            }
        }

        if (learnedNew)
        {
            TrySaveSettings();
        }

        var missingIds = _controlSettings.SeenFanIds.Except(presentIds).ToList();
        var noRpm = fans.Count(f => f.Rpm is null);
        _fanSummary.Text = $"{presentIds.Count}/{_controlSettings.SeenFanIds.Count} fan devices reporting"
            + (noRpm > 0 ? $" · {noRpm} without rpm" : "");
        _fanSummary.Foreground = missingIds.Count > 0 ? Theme.Warn : Theme.Text;
        _fanForgetLink.Visibility = missingIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _fanRows.Children.Clear();
        foreach (var device in fans)
        {
            var rpm = device.Rpm is { } r ? $"{r} rpm" : "no rpm";
            var brush = device.Rpm is null ? Theme.Faint : Theme.Accent2;
            _fanRows.Children.Add(DeviceRow($"{device.Family} · {device.Name}", device.Id, $"{rpm} @ {device.AppliedDutyPercent}%", brush));
        }

        foreach (var id in missingIds)
        {
            _fanRows.Children.Add(DeviceRow("Remembered fan device — not detected", Shorten(id), "missing", Theme.Warn));
        }

        _fanWarnings.Children.Clear();
        foreach (var warning in status.Warnings)
        {
            _fanWarnings.Children.Add(new TextBlock { Text = "⚠  " + warning, Foreground = Theme.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0) });
        }

        if (_trayIcon is not null)
        {
            var tip = status.FailsafeActive
                ? "DeviceMaster — FAILSAFE, fans 100%"
                : $"DeviceMaster — {status.SourceName} {temp} → {status.TargetDutyPercent}%";
            _trayIcon.Text = tip.Length > 63 ? tip[..63] : tip;
        }
    }

    private void SetBadge(string text, Brush color)
    {
        _fanBadge.Text = text;
        _fanBadge.Foreground = color;
        _fanDot.Background = color;
    }

    private void UpdateDashboard(ControlStatus? status)
    {
        if (status is null || !status.Running)
        {
            _dashStatus.Text = _loop is not null && _controlSettings.Mode != ControlMode.Off
                ? "Starting fan control…"
                : "Control off — devices follow their own hardware/firmware curves.";
            _dashStatus.Foreground = Theme.Dim;
            foreach (var (tile, sub) in new[]
            {
                (_tileFans, _tileFansSub), (_tileDuty, _tileDutySub),
                (_cpuTemp, _cpuTempSub), (_cpuLoad, _cpuLoadSub), (_cpuPower, _cpuPowerSub), (_cpuRam, _cpuRamSub),
                (_gpuTemp, _gpuTempSub), (_gpuLoad, _gpuLoadSub), (_gpuPower, _gpuPowerSub), (_gpuVram, _gpuVramSub),
            })
            {
                tile.Text = "—";
                sub.Text = "";
            }

            _dashFanGrid.Children.Clear();
            _dashWarnings.Children.Clear();
            return;
        }

        _dashStatus.Text = status.FailsafeActive
            ? $"{status.SourceName} unavailable — FAILSAFE, all fans at 100%"
            : $"Curve on {SourceLabel(status.SourceName)} → fans {status.TargetDutyPercent}%";
        _dashStatus.Foreground = status.FailsafeActive ? Theme.Warn : Theme.Text;

        UpdateProcessorSections(status);

        var fans = status.Devices.ToList();
        _tileFans.Text = $"{fans.Count}/{Math.Max(_controlSettings.SeenFanIds.Count, fans.Count)}";
        var silent = fans.Count(f => f.Rpm is null);
        _tileFansSub.Text = silent > 0 ? $"{silent} without rpm" : "all reporting";

        _tileDuty.Text = $"{status.TargetDutyPercent}%";
        _tileDutySub.Text = "from curve";

        _dashFanGrid.Children.Clear();
        foreach (var fan in fans)
        {
            var name = fan.Name.Contains('(') ? fan.Name[..fan.Name.IndexOf('(')].Trim() : fan.Name;
            _dashFanGrid.Children.Add(FanPill(
                $"{fan.Family} · {name}",
                fan.Rpm is { } r ? $"{r} rpm" : "no rpm",
                fan.Rpm is null ? Theme.Faint : Theme.Accent2));
        }

        _dashWarnings.Children.Clear();
        foreach (var warning in status.Warnings)
        {
            _dashWarnings.Children.Add(new TextBlock { Text = "⚠  " + warning, Foreground = Theme.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        }
    }

    /// <summary>CPU and GPU sections; the headless web dashboard renders the same figures the same way.</summary>
    private void UpdateProcessorSections(ControlStatus status)
    {
        static string Deg(double? v) => v is { } t ? $"{t:F0}°" : "—";
        static string Pct(double? v) => v is { } p ? $"{p:F0}%" : "—";
        static string Watts(double? v) => v is { } w ? $"{w:F0} W" : "—";
        static string Gb(double? v) => v is { } g ? $"{g:F1} GB" : "—";

        _cpuName.Text = status.CpuName ?? "processor";
        _cpuTemp.Text = Deg(status.CpuTemperatureC);
        _cpuTempSub.Text = "package";
        _cpuLoad.Text = Pct(status.CpuLoadPercent);
        _cpuLoadSub.Text = "all cores";
        _cpuPower.Text = Watts(status.CpuPowerW);
        _cpuPowerSub.Text = "package";
        _cpuRam.Text = Gb(status.RamUsedGb);
        _cpuRamSub.Text = status.RamTotalGb is { } ram ? $"of {ram:F0} GB" : "in use";

        _gpuName.Text = status.GpuName ?? "graphics card";
        _gpuTemp.Text = Deg(status.GpuTemperatureC);
        _gpuTempSub.Text = "core";
        _gpuLoad.Text = Pct(status.GpuLoadPercent);
        _gpuLoadSub.Text = "utilization";
        _gpuPower.Text = Watts(status.GpuPowerW);
        _gpuPowerSub.Text = "board";
        _gpuVram.Text = Gb(status.VramUsedGb);
        _gpuVramSub.Text = status.VramTotalGb is { } vram ? $"of {vram:F0} GB" : "in use";
    }

    // ---------- updates ----------

    /// <summary>How often the running app looks for a new release (it installs them by itself).</summary>
    private static readonly TimeSpan AutoUpdateInterval = TimeSpan.FromMinutes(15);

    /// <summary>GPU load above which an automatic update waits (a game is running): the restart blips fans and lighting.</summary>
    private const double AutoUpdateBusyGpuLoadPercent = 40;

    private string? _deferredUpdateLogged;

    private async Task CheckForUpdatesAsync(bool auto)
    {
        if (_checkBusy || _downloading)
        {
            return;
        }

        if (!auto)
        {
            _checkBusy = true;
            _checkButton.IsHitTestVisible = false;
            _checkButton.Opacity = 0.7;
            _checkLabel.Text = "↻  Checking…";
        }

        var info = await Updater.CheckLatestAsync();

        var current = WholeVersion.Parse(AppVersion);
        if (info is not null && WholeVersion.Compare(info.Version, current) > 0 && info.SetupUrl is not null)
        {
            // an automatic install restarts the app (fans briefly to hardware control, lighting
            // blips): never in the middle of a game — wait for the next check when the GPU is idle
            if (auto && _loop?.Status.GpuLoadPercent is { } gpuLoad && gpuLoad >= AutoUpdateBusyGpuLoadPercent)
            {
                if (_deferredUpdateLogged != info.Tag)
                {
                    _deferredUpdateLogged = info.Tag;
                    LogLine($"auto-update: {info.Tag} is available — waiting until the GPU is idle (now {gpuLoad:F0} %)");
                }

                return;
            }

            // updates are hands-off: download and install silently, then relaunch in the
            // tray. Status shows INSIDE the pill — it keeps its place and look throughout.
            _pendingUpdate = info;
            _checkBusy = true;
            _checkButton.IsHitTestVisible = false;
            _checkLabel.Text = $"↻  Updating to {info.Tag}…";
            await StartUpdateAsync(auto);
            return;
        }

        if (!auto)
        {
            // brief feedback in the button itself, then revert
            _checkLabel.Text = info is null ? $"⚠  {Updater.LastError}" : "✓  Up to date";
            await Task.Delay(3000);
            RestoreCheckButton();
        }
    }

    private void RestoreCheckButton()
    {
        _checkBusy = false;
        _checkButton.IsHitTestVisible = true;
        _checkButton.Opacity = 1.0;
        _checkLabel.Text = "↻  Check for Updates";
    }

    private async Task StartUpdateAsync(bool auto = false)
    {
        if (_downloading || _pendingUpdate?.SetupUrl is not { } setupUrl)
        {
            return;
        }

        _downloading = true;
        var installerPath = await Updater.DownloadInstallerAsync(setupUrl);
        if (installerPath is null)
        {
            _downloading = false;
            if (auto)
            {
                // background check: no browser popping up — just try again at the next one
                LogLine($"auto-update: download of {_pendingUpdate.Tag} failed — retrying at the next check");
                RestoreCheckButton();
                return;
            }

            _checkLabel.Text = "⚠  Download failed — opening releases page";
            Process.Start(new ProcessStartInfo(_pendingUpdate.PageUrl) { UseShellExecute = true });
            await Task.Delay(3000);
            RestoreCheckButton();
            return;
        }

        _downloading = false;
        _checkLabel.Text = $"↻  Installing {_pendingUpdate.Tag}…";
        LogLine($"auto-update: installing {_pendingUpdate.Tag}");
        PrepareExit(); // release devices and the tray icon before the installer replaces us
        Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
        });
        Application.Current.Shutdown();
    }
}
