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

    public const string VersionDate = "2026-07-11";

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
    private DmSlider _dutySlider = null!;
    private TextBlock _dutyLabel = null!;
    private readonly TextBlock _controlStatus = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(0, 14, 0, 10) };
    private readonly StackPanel _fanRows = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _fanWarnings = new();
    private readonly TextBlock _fanSummary = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
    private TextBlock _fanExpandLink = null!;
    private TextBlock _fanForgetLink = null!;
    private bool _fansExpanded;
    private Border _fanDot = null!;
    private TextBlock _fanBadge = null!;

    // pump card
    private DmSlider _pumpSlider = null!;
    private TextBlock _pumpLabel = null!;
    private Border _pumpDot = null!;
    private TextBlock _pumpBadge = null!;
    private readonly TextBlock _pumpCoolant = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(0, 14, 0, 10) };
    private readonly StackPanel _pumpRows = new();

    // hardware page — summary count up top, then pills auto-grouped by family, three per row
    private readonly StackPanel _hardwareRows = new();
    private readonly TextBlock _hwSummary = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
    private TextBlock _hwForgetLink = null!;
    private List<string> _lastHardwareKeys = [];
    private readonly TextBlock _conflictSummary = new() { FontSize = 12, Foreground = Theme.Warn, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private Border _rescanButton = null!;

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
        Grid.SetIsSharedSizeScope(_pumpRows, true);
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
            MaybeRebuildScreenList();
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
            await RefreshDevicesAsync();
            await CheckForUpdatesAsync(auto: true);
        });

        SourceInitialized += (_, _) =>
        {
            // dark title bar to match the theme
            var dark = 1;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- layout: left sidebar navigation + visibility-switched pages ----------

    private readonly Dictionary<string, UIElement> _pages = [];
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
        brand.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(11),
            Background = Theme.Card2,
            BorderBrush = Theme.Line2,
            BorderThickness = new Thickness(1),
            Child = BuildLogoGlyph(),
        });
        var brandText = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brandText.Children.Add(new TextBlock { Text = "DeviceMaster", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Theme.Text });
        brandText.Children.Add(new TextBlock { Text = "cooling · RGB · screens", FontSize = 10.5, Foreground = Theme.Faint });
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
        var nav = new StackPanel();
        foreach (var (key, glyph, label) in new[]
        {
            ("dashboard", "⌂", "Dashboard"),
            ("cooling", "❄", "Cooling"),
            ("lighting", "◈", "Lighting"),
            ("screens", "▣", "Screens"),
            ("turzx", "▤", "Turzx"),
            ("devices", "⚙", "Devices"),
        })
        {
            var button = NavButton(key, glyph, label);
            _navButtons[key] = button;
            nav.Children.Add(button);
        }

        side.Children.Add(nav);
        sidebar.Child = side;
        DockPanel.SetDock(sidebar, Dock.Left);
        root.Children.Add(sidebar);

        // ---- pages (visibility-switched so live updates keep flowing) ----
        var host = new Grid { Margin = new Thickness(26, 22, 26, 16) };
        _pages["dashboard"] = WrapPage(BuildDashboardPage());
        _pages["cooling"] = WrapPage(BuildCoolingPage());
        _pages["lighting"] = WrapPage(BuildLightingPage());
        _pages["screens"] = WrapPage(BuildScreensPage());
        _pages["turzx"] = WrapPage(BuildTurzxPage());
        _pages["devices"] = WrapPage(BuildDevicesPage());
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

    private Border NavButton(string key, string glyph, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontSize = 15, Width = 26, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center });
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
    private TextBlock _tileCoolant = null!, _tileCoolantSub = null!;
    private TextBlock _tileCpu = null!, _tileCpuSub = null!;
    private TextBlock _tileGpu = null!, _tileGpuSub = null!;
    private TextBlock _tilePump = null!, _tilePumpSub = null!;
    private TextBlock _tileFans = null!, _tileFansSub = null!;
    private TextBlock _tileDuty = null!, _tileDutySub = null!;
    private readonly StackPanel _dashWarnings = new() { Margin = new Thickness(2, 12, 0, 0) };

    private readonly System.Windows.Controls.Primitives.UniformGrid _dashFanGrid = new() { Columns = 4 };

    private UIElement BuildDashboardPage()
    {
        var page = new StackPanel { MaxWidth = 1460, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(PageTitle("Dashboard", "the whole loop at a glance"));
        page.Children.Add(_dashStatus);

        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6 };
        tiles.Children.Add(StatTile("COOLANT", out _tileCoolant, out _tileCoolantSub));
        tiles.Children.Add(StatTile("CPU", out _tileCpu, out _tileCpuSub));
        tiles.Children.Add(StatTile("GPU", out _tileGpu, out _tileGpuSub));
        tiles.Children.Add(StatTile("PUMP", out _tilePump, out _tilePumpSub));
        tiles.Children.Add(StatTile("FANS", out _tileFans, out _tileFansSub));
        tiles.Children.Add(StatTile("DUTY", out _tileDuty, out _tileDutySub));
        page.Children.Add(tiles);

        var fanCard = Theme.CardShell("✻", "Fans right now", "every fan with its live speed", out var fanBody, out _);
        fanCard.Margin = new Thickness(0, 4, 12, 0);
        fanBody.Children.Add(_dashFanGrid);
        page.Children.Add(fanCard);

        page.Children.Add(_dashWarnings);
        return page;
    }

    /// <summary>Compact name → rpm pill for the dashboard fan grid.</summary>
    private static Border FanPill(string name, string value, Brush valueBrush)
    {
        var row = new DockPanel { LastChildFill = false };
        var title = new TextBlock
        {
            Text = name,
            Foreground = Theme.Text,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 190,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(title, Dock.Left);
        row.Children.Add(title);
        var val = new TextBlock
        {
            Text = value,
            Foreground = valueBrush,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);
        return new Border
        {
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(9),
            Background = Theme.Inset,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    private static TextBlock PageTitle(string title, string subtitle)
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
        value = new TextBlock { Text = "—", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Theme.Text, Margin = new Thickness(0, 4, 0, 2) };
        stack.Children.Add(value);
        sub = new TextBlock { Text = "", FontSize = 10.5, Foreground = Theme.Faint };
        stack.Children.Add(sub);
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

    private UIElement BuildCoolingPage()
    {
        // two equal halves, equal heights (both cards stretch to the taller one)
        var page = new StackPanel();
        page.Children.Add(PageTitle("Cooling", "fixed fan duty and the pump"));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var fan = BuildFanCard();
        fan.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(fan, 0);
        grid.Children.Add(fan);
        var pump = BuildPumpCard();
        pump.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(pump, 1);
        grid.Children.Add(pump);
        page.Children.Add(grid);
        return page;
    }

    private UIElement BuildLightingPage()
    {
        var page = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(PageTitle("Lighting", "one static color across every LED"));
        page.Children.Add(BuildRgbCard());
        return page;
    }

    private Border _lcdControlCard = null!;

    private UIElement BuildScreensPage()
    {
        // one two-column grid: Screen Control occupies the first cell (same width and row
        // height as the group beside it), the group cards fill the rest
        var page = new StackPanel();
        page.Children.Add(PageTitle("Screens", "the pump LCD and every fan LCD"));
        _lcdControlCard = BuildLcdCard();
        _lcdControlCard.Margin = new Thickness(0, 0, 14, 14);
        _lcdControlCard.VerticalAlignment = VerticalAlignment.Stretch; // match the pump card's row height
        page.Children.Add(_screenList);
        RebuildScreenList();
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

    private UIElement BuildLogoGlyph()
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (icon is not null)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(30, 30));
                return new Image { Source = source, Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
        }
        catch
        {
            // fall through to the glyph
        }

        return new TextBlock { Text = "✻", FontSize = 22, Foreground = Theme.AccentGrad(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    private Border BuildFanCard()
    {
        var card = Theme.CardShell("✻", "Fan Control", "one fixed duty · every fan in the system", out var body, out var head);
        var badge = Theme.StatusBadge("Off", Theme.Faint, out _fanDot, out _fanBadge);
        badge.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);

        var controls = new WrapPanel { Orientation = Orientation.Horizontal };

        _dutySlider = new DmSlider(0, 100, _controlSettings.ManualDutyPercent, 460);
        _dutySlider.ValueChanged += _ => OnControlSettingChanged();
        _dutyLabel = new TextBlock { Text = $"{_controlSettings.ManualDutyPercent}%", FontSize = 13, Foreground = Theme.Accent2, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 40 };
        var dutyRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        dutyRow.Children.Add(_dutySlider);
        dutyRow.Children.Add(_dutyLabel);
        controls.Children.Add(LabelledInline("Fan duty", dutyRow));

        body.Children.Add(controls);
        body.Children.Add(_controlStatus);

        // rolled-up by default: one "N/N fans" line; details expand on demand
        var summaryRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _fanExpandLink = LinkText("▸  details", () =>
        {
            _fansExpanded = !_fansExpanded;
            _fanRows.Visibility = _fansExpanded ? Visibility.Visible : Visibility.Collapsed;
            _fanExpandLink.Text = _fansExpanded ? "▾  hide" : "▸  details";
        });
        DockPanel.SetDock(_fanExpandLink, Dock.Right);
        summaryRow.Children.Add(_fanExpandLink);
        _fanForgetLink = LinkText("forget missing", () =>
        {
            var status = _loop?.Status;
            var present = (status?.Devices ?? []).Where(d => !d.IsPump)
                .Select(f => f.Id ?? f.Name).ToHashSet();
            _controlSettings.SeenFanIds.RemoveAll(id => !present.Contains(id));
            TrySaveSettings();
        });
        _fanForgetLink.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_fanForgetLink, Dock.Right);
        summaryRow.Children.Add(_fanForgetLink);
        summaryRow.Children.Add(_fanSummary);
        body.Children.Add(summaryRow);

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
    private static Border BrightnessRow(int initial, Action<int> onChange)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Theme.SmallLabel("Brightness"));
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

    private Border BuildPumpCard()
    {
        var card = Theme.CardShell("≋", "Pump Control", "independent duty · never below 50%", out var body, out var head);
        var badge = Theme.StatusBadge("Off", Theme.Faint, out _pumpDot, out _pumpBadge);
        badge.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);

        var controls = new WrapPanel { Orientation = Orientation.Horizontal };
        _pumpSlider = new DmSlider(50, 100, Math.Clamp(_controlSettings.PumpDutyPercent, 50, 100), 460);
        _pumpSlider.ValueChanged += _ => OnControlSettingChanged();
        _pumpLabel = new TextBlock { Text = $"{Math.Clamp(_controlSettings.PumpDutyPercent, 50, 100)}%", FontSize = 13, Foreground = Theme.Accent2, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 40 };
        var pumpRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pumpRow.Children.Add(_pumpSlider);
        pumpRow.Children.Add(_pumpLabel);
        controls.Children.Add(LabelledInline("Pump duty", pumpRow));

        body.Children.Add(controls);
        body.Children.Add(_pumpCoolant);
        body.Children.Add(_pumpRows);
        return card;
    }

    // lighting card
    private readonly WrapPanel _swatchPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rgbStatus = new() { FontSize = 12, Foreground = Theme.Dim, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };

    // fully saturated primaries — pastel tones look washed out on LEDs
    private static readonly (byte R, byte G, byte B)[] SwatchColors =
    [
        (255, 255, 255), (255, 0, 0), (255, 70, 0), (255, 170, 0), (0, 255, 0),
        (0, 255, 255), (0, 60, 255), (150, 0, 255), (255, 0, 110),
    ];

    private Border BuildRgbCard()
    {
        var card = Theme.CardShell("◈", "Lighting Control", "static color · every LED in the system", out var body, out var head);

        var toggle = Theme.Toggle(_controlSettings.RgbEnabled, on =>
        {
            _controlSettings.RgbEnabled = on;
            OnControlSettingChanged();
        });
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(toggle, Dock.Right);
        head.Children.Add(toggle);

        RebuildSwatches();
        body.Children.Add(_swatchPanel);
        var rgbBrightness = BrightnessRow(_controlSettings.RgbBrightness, v =>
        {
            _controlSettings.RgbBrightness = v;
            OnControlSettingChanged(); // saves + re-applies the (now dimmed) color to every LED
        });
        rgbBrightness.Margin = new Thickness(0, 14, 0, 4); // separate it from the swatches above
        body.Children.Add(rgbBrightness);
        body.Children.Add(_rgbStatus);
        UpdateRgbStatusText();
        return card;
    }

    private void RebuildSwatches()
    {
        _swatchPanel.Children.Clear();

        // "lights out" tile — actively paints every LED black (≠ the toggle, which stops
        // writing and lets devices fall back to their own effects)
        var offSwatch = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 14)),
            BorderBrush = _controlSettings.RgbOff ? Theme.Accent : Theme.Line2,
            BorderThickness = new Thickness(_controlSettings.RgbOff ? 2 : 1),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Lights out — every LED off",
            Child = new TextBlock
            {
                Text = "⊘",
                FontSize = 13,
                Foreground = _controlSettings.RgbOff ? Theme.Accent2 : Theme.Dim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        offSwatch.MouseLeftButtonUp += (_, _) =>
        {
            _controlSettings.RgbOff = true;
            RebuildSwatches();
            OnControlSettingChanged();
        };
        _swatchPanel.Children.Add(offSwatch);

        foreach (var (r, g, b) in SwatchColors)
        {
            var selected = !_controlSettings.RgbOff
                && _controlSettings.RgbR == r && _controlSettings.RgbG == g && _controlSettings.RgbB == b;
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderBrush = selected ? Theme.Accent : Theme.Line2,
                BorderThickness = new Thickness(selected ? 2 : 1),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var (cr, cg, cb) = (r, g, b);
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _controlSettings.RgbOff = false;
                _controlSettings.RgbR = cr;
                _controlSettings.RgbG = cg;
                _controlSettings.RgbB = cb;
                RebuildSwatches();
                OnControlSettingChanged();
            };
            _swatchPanel.Children.Add(swatch);
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
            _rgbStatus.Text = "Waiting for fan control — set Mode to Manual or Curve to apply lighting.";
        }
        else if (_controlSettings.RgbOff)
        {
            _rgbStatus.Text = "Lights out — every LED on Corsair, Lian Li, motherboard ARGB, RAM and GPU is dark.";
        }
        else
        {
            _rgbStatus.Text = $"Static color #{_controlSettings.RgbR:X2}{_controlSettings.RgbG:X2}{_controlSettings.RgbB:X2} "
                + "on Corsair, Lian Li, motherboard ARGB, RAM and GPU LEDs.";
        }
    }

    // screens card
    private readonly WrapPanel _lcdButtons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _lcdStatus = new() { FontSize = 12, Foreground = Theme.Dim, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };

    // display order and names are independent of the enum (which persists by name)
    private static readonly (LcdMetric Metric, string Name)[] LcdMetricChoices =
    [
        (LcdMetric.Coolant, "Coolant °C"),
        (LcdMetric.CpuTemp, "CPU °C"),
        (LcdMetric.GpuTemp, "GPU °C"),
        (LcdMetric.VramTemp, "VRAM °C"),
        (LcdMetric.CpuLoad, "CPU %"),
        (LcdMetric.GpuLoad, "GPU %"),
        (LcdMetric.RamLoad, "RAM %"),
        (LcdMetric.VramLoad, "VRAM %"),
        (LcdMetric.CpuPower, "CPU W"),
        (LcdMetric.GpuPower, "GPU W"),
        (LcdMetric.GpuClock, "GPU MHz"),
        (LcdMetric.FanDuty, "Fan %"),
        (LcdMetric.FanRpm, "Fan RPM"),
        (LcdMetric.PumpDuty, "Pump %"),
        (LcdMetric.PumpRpm, "Pump RPM"),
        (LcdMetric.Clock, "Clock"),
        (LcdMetric.Date, "Date"),
    ];

    private static readonly string[] LcdMetricNames = LcdMetricChoices.Select(c => c.Name).ToArray();

    private static int LcdMetricChoiceIndex(LcdMetric metric) =>
        Math.Max(0, Array.FindIndex(LcdMetricChoices, c => c.Metric == metric));

    // per-screen editor list (Screens page)
    // two equal columns of group cards; rows size to their own content (a UniformGrid would
    // stretch every row to the tallest group, leaving giant gaps under short rows)
    private readonly Grid _screenList = new();
    private string _screenListSignature = "?";

    private static IReadOnlyList<(string Id, bool IsPump)> FakeScreens =>
        Environment.GetEnvironmentVariable("DEVICEMASTER_FAKE_SCREENS") is { Length: > 0 }
            ? [("pump-lcd", true), ("0B913822D5160A66", false), ("14E3F709651F17E6", false), ("522AEAB205160E66", false)]
            : [];

    private IReadOnlyList<(string Id, bool IsPump)> ScreenIds()
    {
        var ids = _loop?.LcdScreenIds ?? [];
        return ids.Count > 0 ? ids : FakeScreens;
    }

    private void MaybeRebuildScreenList()
    {
        var ids = ScreenIds();
        var signature = string.Join("|", ids.Select(s => s.Id));
        if (signature != _screenListSignature)
        {
            RebuildScreenList();
        }
    }

    private void RebuildScreenList()
    {
        var ids = ScreenIds();
        _screenListSignature = string.Join("|", ids.Select(s => s.Id));
        _screenList.Children.Clear();
        _screenList.RowDefinitions.Clear();
        if (_screenList.ColumnDefinitions.Count == 0)
        {
            _screenList.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _screenList.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var cell = 0;
        void Place(UIElement element)
        {
            var row = cell / 2;
            while (_screenList.RowDefinitions.Count <= row)
            {
                _screenList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow(element, row);
            Grid.SetColumn(element, cell % 2);
            _screenList.Children.Add(element);
            cell++;
        }

        Place(_lcdControlCard); // first cell of the grid

        if (ids.Count == 0)
        {
            Place(new TextBlock
            {
                Text = "Screens appear here once a screen mode is active (pick On above).",
                Foreground = Theme.Dim,
                FontSize = 12.5,
            });
            return;
        }

        // gather members with stable titles, then cluster by group name
        var members = new List<(string Id, string Title, LcdScreenConfig Config)>();
        var fanIndex = 0;
        foreach (var (id, isPump) in ids)
        {
            var config = _controlSettings.ScreenConfig(id, isPump);
            members.Add((id, isPump ? "Pump screen" : $"Fan screen {++fanIndex}", config));
        }

        // cluster related groups next to each other by sorting on the REVERSED words of the
        // name — "bottom intake"/"side intake" pair up, "rear exhaust"/"top exhaust" pair up,
        // and "pump" lands first (descending)
        static string ClusterKey(string name) => string.Join(" ", name.Split(' ').Reverse());
        var groups = members
            .GroupBy(m => m.Config.Group.Trim(), StringComparer.OrdinalIgnoreCase) // "Front" == "front"
            .OrderBy(g => g.Key.Length == 0) // named groups first, "Ungrouped" last
            .ThenByDescending(g => ClusterKey(g.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            Place(ScreenGroupCard(group.Key, group.ToList()));
        }
    }

    /// <summary>One group of screens: editable name, set-all controls, then the member rows.</summary>
    private Border ScreenGroupCard(string groupName, List<(string Id, string Title, LcdScreenConfig Config)> members)
    {
        var body = new StackPanel();

        // ---- header: name (editable), count, find-all ----
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        var nameBox = GroupNameBox(groupName.Length == 0 ? "" : groupName, newName =>
        {
            foreach (var member in members)
            {
                member.Config.Group = newName;
            }

            TrySaveSettings();
            RebuildScreenList();
        });
        DockPanel.SetDock(nameBox, Dock.Left);
        header.Children.Add(nameBox);

        var findAll = Theme.Btn("◎ Find all", primary: false, () =>
        {
            foreach (var member in members)
            {
                _loop?.IdentifyScreen(member.Id);
            }
        });
        DockPanel.SetDock(findAll, Dock.Right);
        header.Children.Add(findAll);
        body.Children.Add(header);

        // ---- table: column headers once, then the member rows ----
        body.Children.Add(ScreenTableHeader());
        foreach (var (id, title, config) in members)
        {
            body.Children.Add(ScreenRow(id, title, config));
        }

        return new Border
        {
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(16, 12, 16, 10),
            CornerRadius = new CornerRadius(14),
            Background = Theme.Card,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = body,
        };
    }

    private static readonly (byte R, byte G, byte B)[] FontSwatchColors =
    [
        (235, 235, 245), (255, 60, 60), (255, 170, 0), (80, 230, 120),
        (80, 200, 255), (90, 120, 255), (200, 90, 255), (255, 80, 170),
    ];

    private static readonly string[] LcdColorNames =
        ["White", "Red", "Orange", "Green", "Cyan", "Blue", "Purple", "Pink", "By temp"];

    private static int LcdColorIndex(LcdScreenConfig config)
    {
        if (config.ColorByValue)
        {
            return 8; // By temp — green/amber/red as the value climbs
        }

        if (config.FontR is not { } r || config.FontG is not { } g || config.FontB is not { } b)
        {
            return 0; // unset = White
        }

        for (var i = 0; i < FontSwatchColors.Length; i++)
        {
            if (FontSwatchColors[i] == ((byte)r, (byte)g, (byte)b))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Editable group name; commits on Enter or focus loss.</summary>
    private static TextBox GroupNameBox(string text, Action<string> commit)
    {
        var box = new TextBox
        {
            Text = text,
            Width = 190,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Background = Theme.Inset,
            Foreground = Theme.Text,
            CaretBrush = Theme.Text,
            BorderBrush = Theme.Line2,
            Padding = new Thickness(8, 5, 8, 5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var original = text;
        void Commit()
        {
            var value = box.Text.Trim();
            if (value != original)
            {
                commit(value);
            }
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Commit();
            }
        };
        if (text.Length == 0)
        {
            box.Text = "";
            box.ToolTip = "Type a group name (e.g. Front, Side, Top)";
        }

        return box;
    }

    // table columns shared by the header row and every screen row: Find, Name, ID, Metric,
    // Rotate, Color, Group (star — fills the rest, so the group box is never clipped)
    private static Grid ScreenTableGrid()
    {
        var grid = new Grid();
        foreach (var width in new[] { 88.0, 110.0, 140.0, 128.0, 90.0, 112.0 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static Grid ScreenTableHeader()
    {
        var grid = ScreenTableGrid();
        grid.Margin = new Thickness(14, 0, 14, 4);
        var column = 0;
        foreach (var text in new[] { "", "NAME", "ID", "METRIC", "ROTATE", "COLOR", "GROUP" })
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Faint,
                Margin = new Thickness(column >= 3 ? 10 : 2, 0, 0, 0),
            };
            Grid.SetColumn(block, column++);
            grid.Children.Add(block);
        }

        return grid;
    }

    private Border ScreenRow(string id, string title, LcdScreenConfig config)
    {
        var row = ScreenTableGrid();

        var find = Theme.Btn("◎ Find", primary: false, () => _loop?.IdentifyScreen(id));
        find.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(find, 0);
        row.Children.Add(find);

        var name = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            Foreground = Theme.Text,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var idBlock = new TextBlock
        {
            Text = id, // full serial, no truncation
            FontSize = 10,
            Foreground = Theme.Faint,
            FontFamily = Theme.Mono,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        Grid.SetColumn(idBlock, 2);
        row.Children.Add(idBlock);

        var metricDrop = new DmDropdown(LcdMetricNames, LcdMetricChoiceIndex(config.Metric), 116);
        metricDrop.SelectionChanged += index =>
        {
            config.Metric = LcdMetricChoices[index].Metric;
            TrySaveSettings();
            _loop?.Apply(_controlSettings);
        };
        metricDrop.Margin = new Thickness(10, 0, 0, 0);
        metricDrop.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(metricDrop, 3);
        row.Children.Add(metricDrop);

        var rotationDrop = new DmDropdown(["0°", "90°", "180°", "270°"], Math.Clamp(config.RotationDegrees / 90, 0, 3), 76);
        rotationDrop.SelectionChanged += index =>
        {
            config.RotationDegrees = index * 90;
            TrySaveSettings();
            _loop?.Apply(_controlSettings);
        };
        rotationDrop.Margin = new Thickness(10, 0, 0, 0);
        rotationDrop.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(rotationDrop, 4);
        row.Children.Add(rotationDrop);

        var colorDrop = new DmDropdown(LcdColorNames, LcdColorIndex(config), 98);
        colorDrop.SelectionChanged += index =>
        {
            config.ColorByValue = index == 8;
            (config.FontR, config.FontG, config.FontB) = index == 8
                ? (null, null, null)
                : ((int?)FontSwatchColors[index].R, (int?)FontSwatchColors[index].G, (int?)FontSwatchColors[index].B);
            TrySaveSettings();
            _loop?.Apply(_controlSettings);
        };
        colorDrop.Margin = new Thickness(10, 0, 0, 0);
        colorDrop.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(colorDrop, 5);
        row.Children.Add(colorDrop);

        // move the screen to another group by typing its name; stretches to the row edge
        var groupBox = new TextBox
        {
            Text = config.Group,
            FontSize = 11.5,
            Background = Theme.Inset,
            Foreground = Theme.Text,
            CaretBrush = Theme.Text,
            BorderBrush = Theme.Line2,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Group name — screens with the same name cluster together",
        };
        void CommitGroup()
        {
            var value = groupBox.Text.Trim();
            if (value != config.Group)
            {
                config.Group = value;
                TrySaveSettings();
                RebuildScreenList();
            }
        }

        groupBox.LostFocus += (_, _) => CommitGroup();
        groupBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                CommitGroup();
            }
        };
        Grid.SetColumn(groupBox, 6);
        row.Children.Add(groupBox);

        return Theme.InsetRow(row);
    }

    private Border BuildLcdCard()
    {
        var card = Theme.CardShell("▣", "Screen Control", "every screen on or off · what each shows is set per group below", out var body, out _);
        RebuildLcdButtons();
        body.Children.Add(_lcdButtons);
        var lcdBrightness = BrightnessRow(_controlSettings.LcdBrightness, v =>
        {
            _controlSettings.LcdBrightness = v; // pump + fan LCD backlight (Turzx has its own on its page)
            TrySaveSettings();
            _loop?.Apply(_controlSettings);
        });
        lcdBrightness.Margin = new Thickness(0, 14, 0, 4); // separate it from the On/Off buttons above
        body.Children.Add(lcdBrightness);
        UpdateLcdStatusText();
        return card;
    }

    private void RebuildLcdButtons()
    {
        _lcdButtons.Children.Clear();
        foreach (var (mode, label) in new[]
        {
            // just Off/On: On = live metrics; per-screen content is managed in the groups.
            // (Black/White survive in the enum for config compatibility only.)
            (LcdMode.Off, "Off"),
            (LcdMode.Metrics, "On"),
        })
        {
            var selected = _controlSettings.LcdScreens == mode;
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
                _controlSettings.LcdScreens = chosen;
                RebuildLcdButtons();
                UpdateLcdStatusText();
                OnControlSettingChanged();
            };
            _lcdButtons.Children.Add(button);
        }
    }

    private void UpdateLcdStatusText()
    {
        _lcdStatus.Text = _controlSettings.LcdScreens switch
        {
            LcdMode.Off => "All screens off (backlight dark).",
            LcdMode.Metrics => "Screens on — each shows the metric configured in its group below.",
            _ => "Screens untouched — pick On or Off to take control.",
        };
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

    private static StackPanel LabelledInline(string label, UIElement element)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 8), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Theme.SmallLabel(label));
        panel.Children.Add(element);
        return panel;
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
            // Link-chain devices (fans/pump) aren't USB devices — take them from the running
            // control loop, or do a one-shot chain scan while control is off.
            var chainRows = new List<(string Name, string? Id, string Tag)>();
            var chainPending = false;
            var snapshot = _loop?.Status;
            if (snapshot is { Running: true })
            {
                foreach (var device in snapshot.Devices)
                {
                    var tag = device.IsPump ? "pump" : device.Family switch
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
                                    channel.IsPump ? "pump" : channel.IsKnown ? "fan" : "?"));
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

            foreach (var lcd in scan.HidDevices.Where(d => d.Kind == DeviceKind.CorsairLcd))
            {
                AddPill("Corsair", "Corsair pump/res LCD", lcd.SerialNumber, "HID");
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

            var fanNodes = scan.UsbTree
                .Where(n => n.IsPhysicalDevice && n.UsbId is { } id
                    && KnownDeviceRegistry.Identify(id)?.Kind == DeviceKind.LianLiSlv3FanNode)
                .Select(n => n.PnpInstanceId.Split('\\').LastOrDefault() ?? "?")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < fanNodes.Count; i++)
            {
                AddPill("Lian Li", $"SL V3 fan LCD node {i + 1}/{fanNodes.Count}", fanNodes[i], "WinUSB", fanNodes[i]);
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

    private void OnControlSettingChanged()
    {
        if (!_uiReady)
        {
            return;
        }

        _controlSettings.ManualDutyPercent = (int)Math.Round(_dutySlider.Value);
        _controlSettings.PumpDutyPercent = (int)Math.Round(_pumpSlider.Value);
        _dutyLabel.Text = $"{_controlSettings.ManualDutyPercent}%";
        _pumpLabel.Text = $"{_controlSettings.PumpDutyPercent}%";

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
    /// stopped) and turns every screen backlight off; toggling back restores what was set.
    /// </summary>
    private void SetBlackout(bool on)
    {
        if (on == _controlSettings.BlackoutActive)
        {
            return;
        }

        if (on)
        {
            _controlSettings.BlackoutPrevRgbEnabled = _controlSettings.RgbEnabled;
            _controlSettings.BlackoutPrevRgbOff = _controlSettings.RgbOff;
            _controlSettings.BlackoutPrevLcd = _controlSettings.LcdScreens;
            _controlSettings.BlackoutPrevTurzx = _controlSettings.TurzxScreen;
            _controlSettings.RgbEnabled = true; // paint black, don't just stop controlling
            _controlSettings.RgbOff = true;
            _controlSettings.LcdScreens = LcdMode.Off;
            _controlSettings.TurzxScreen = LcdMode.Off;
        }
        else
        {
            _controlSettings.RgbEnabled = _controlSettings.BlackoutPrevRgbEnabled;
            _controlSettings.RgbOff = _controlSettings.BlackoutPrevRgbOff;
            _controlSettings.LcdScreens = _controlSettings.BlackoutPrevLcd;
            _controlSettings.TurzxScreen = _controlSettings.BlackoutPrevTurzx;
        }

        _controlSettings.BlackoutActive = on;
        TrySaveSettings();
        RebuildSwatches();
        RebuildLcdButtons();
        RebuildTurzxButtons();
        UpdateRgbStatusText();
        UpdateLcdStatusText();
        _loop?.Apply(_controlSettings);
    }

    /// <summary>Development sandbox: never start the control loop (screenshot iteration).</summary>
    private static readonly bool NoControl =
        Environment.GetEnvironmentVariable("DEVICEMASTER_NO_CONTROL") is { Length: > 0 };

    private void ApplyControlState()
    {
        // fan control is always on at a fixed duty — curve/off modes were removed from the
        // UI (the enum survives in settings for config compatibility)
        if (_controlSettings.Mode != ControlMode.Manual)
        {
            _controlSettings.Mode = ControlMode.Manual;
            TrySaveSettings();
        }

        // screens are Off or On (metrics) now — plain Black/White backgrounds retired
        if (_controlSettings.LcdScreens is LcdMode.Black or LcdMode.White)
        {
            _controlSettings.LcdScreens = LcdMode.Metrics;
            TrySaveSettings();
        }

        _dutySlider.Enabled = true;
        _pumpSlider.Enabled = true;

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
                    _pumpCoolant.Text = "Searching for the pump…";
                    _pumpCoolant.Foreground = Theme.Dim;
                    _pumpRows.Children.Clear();
                    _pumpRows.Children.Add(LoadingRow("Searching for the pump…"));
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
            _pumpCoolant.Text = "Control off — the pump follows the hub's own behaviour.";
            _pumpCoolant.Foreground = Theme.Dim;
            _pumpRows.Children.Clear();
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
            _controlStatus.Text =
                $"CPU {(status.CpuTemperatureC is { } ct ? $"{ct:F0} °C" : "—")}"
                + $"   ·   GPU {(status.GpuTemperatureC is { } gt ? $"{gt:F0} °C" : "—")}";
            _controlStatus.Foreground = Theme.Text;
        }

        var fans = status.Devices.Where(d => !d.IsPump).ToList();
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
            _fanWarnings.Children.Add(new TextBlock { Text = "⚠  " + warning, Foreground = Theme.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 4) });
        }

        _pumpCoolant.Text = status.CoolantTemperatureC is { } coolant ? $"Coolant {coolant:F1} °C" : "Coolant temperature unavailable";
        _pumpCoolant.Foreground = status.CoolantTemperatureC is null ? Theme.Dim : Theme.Text;

        _pumpRows.Children.Clear();
        foreach (var pump in status.Devices.Where(d => d.IsPump))
        {
            var rpm = pump.Rpm is { } r ? $"{r} rpm" : "no rpm";
            _pumpRows.Children.Add(DeviceRow($"{pump.Family} · {pump.Name}", pump.Id, $"{rpm} @ {pump.AppliedDutyPercent}%", Theme.Accent2));
        }

        if (_pumpRows.Children.Count == 0)
        {
            _pumpRows.Children.Add(new TextBlock { Text = "No pump detected on the Link chain.", Foreground = Theme.Dim, FontSize = 12.5 });
        }

        if (_trayIcon is not null)
        {
            var tip = status.FailsafeActive
                ? "DeviceMaster — FAILSAFE, fans 100%"
                : status.Mode == ControlMode.Manual
                    ? $"DeviceMaster — manual → {status.TargetDutyPercent}%"
                    : $"DeviceMaster — {status.SourceName} {temp} → {status.TargetDutyPercent}%";
            _trayIcon.Text = tip.Length > 63 ? tip[..63] : tip;
        }
    }

    private void SetBadge(string text, Brush color)
    {
        _fanBadge.Text = text;
        _fanBadge.Foreground = color;
        _fanDot.Background = color;

        // the pump runs under the same loop — its badge mirrors the fan card's
        _pumpBadge.Text = text;
        _pumpBadge.Foreground = color;
        _pumpDot.Background = color;
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
                (_tileCoolant, _tileCoolantSub), (_tileCpu, _tileCpuSub), (_tileGpu, _tileGpuSub),
                (_tilePump, _tilePumpSub), (_tileFans, _tileFansSub), (_tileDuty, _tileDutySub),
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
            : status.Mode == ControlMode.Manual
                ? $"Manual — all fans at {status.TargetDutyPercent}%"
                : $"Curve on {status.SourceName} → fans {status.TargetDutyPercent}%";
        _dashStatus.Foreground = status.FailsafeActive ? Theme.Warn : Theme.Text;

        _tileCoolant.Text = status.CoolantTemperatureC is { } cool ? $"{cool:F1}°" : "—";
        _tileCoolantSub.Text = "loop water";
        _tileCpu.Text = status.CpuTemperatureC is { } cpu ? $"{cpu:F0}°" : "—";
        _tileCpuSub.Text = "package";
        _tileGpu.Text = status.GpuTemperatureC is { } gpu ? $"{gpu:F0}°" : "—";
        _tileGpuSub.Text = "core";

        var pump = status.Devices.FirstOrDefault(d => d.IsPump);
        _tilePump.Text = pump?.Rpm is { } rpm ? rpm.ToString() : "—";
        _tilePumpSub.Text = pump is null ? "not detected" : $"rpm @ {pump.AppliedDutyPercent}%";

        var fans = status.Devices.Where(d => !d.IsPump).ToList();
        _tileFans.Text = $"{fans.Count}/{Math.Max(_controlSettings.SeenFanIds.Count, fans.Count)}";
        var silent = fans.Count(f => f.Rpm is null);
        _tileFansSub.Text = silent > 0 ? $"{silent} without rpm" : "all reporting";

        _tileDuty.Text = $"{status.TargetDutyPercent}%";
        _tileDutySub.Text = status.Mode == ControlMode.Manual ? "manual" : "from curve";

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

    // ---------- updates ----------

    private async Task CheckForUpdatesAsync(bool auto)
    {
        if (_checkBusy)
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
            // updates are hands-off: download and install silently, then relaunch in the
            // tray. Status shows INSIDE the pill — it keeps its place and look throughout.
            _pendingUpdate = info;
            _checkBusy = true;
            _checkButton.IsHitTestVisible = false;
            _checkLabel.Text = $"↻  Updating to {info.Tag}…";
            await StartUpdateAsync();
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

    private async Task StartUpdateAsync()
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
