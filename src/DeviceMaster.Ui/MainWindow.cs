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

    public const string VersionDate = "2026-07-06";

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
    private StackPanel _updateNotice = null!;
    private TextBlock _updateNoticeText = null!;
    private UpdateInfo? _pendingUpdate;
    private bool _checkBusy;
    private bool _downloading;
    private int _downloadDots;

    // fan control card
    private DmDropdown _modeDrop = null!;
    private DmDropdown _sourceDrop = null!;
    private DmSlider _dutySlider = null!;
    private TextBlock _dutyLabel = null!;
    private readonly TextBlock _controlStatus = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(0, 14, 0, 10) };
    private readonly StackPanel _fanRows = new();
    private Border _fanDot = null!;
    private TextBlock _fanBadge = null!;

    // pump card
    private DmSlider _pumpSlider = null!;
    private TextBlock _pumpLabel = null!;
    private readonly TextBlock _pumpCoolant = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(0, 14, 0, 10) };
    private readonly StackPanel _pumpRows = new();

    // hardware card — compact pills, two per row
    private readonly System.Windows.Controls.Primitives.UniformGrid _hardwareRows = new() { Columns = 2 };
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
        Background = Theme.Bg;
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
        _uiReady = true;

        CreateTrayIcon();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            UpdateControlStatus();
            if (_downloading)
            {
                _updateNoticeText.Text = $"Updating to {_pendingUpdate?.Tag} — downloading"
                    + new string('.', 1 + _downloadDots++ % 3);
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

    // ---------- layout ----------

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };

        // ---- header ----
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 20) };

        var logo = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            Background = Theme.Card2,
            BorderBrush = Theme.Line2,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = BuildLogoGlyph(),
        };
        DockPanel.SetDock(logo, Dock.Left);
        header.Children.Add(logo);

        var titles = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "DeviceMaster", FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Theme.Text });
        titles.Children.Add(new TextBlock { Text = "PC cooling & RGB toolkit", FontSize = 11.5, Foreground = Theme.Faint });
        DockPanel.SetDock(titles, Dock.Left);
        header.Children.Add(titles);

        var versionBox = new Border
        {
            Margin = new Thickness(26, 0, 0, 0),
            Padding = new Thickness(16, 9, 16, 9),
            CornerRadius = new CornerRadius(12),
            Background = Theme.Card,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var versionStack = new StackPanel();
        versionStack.Children.Add(new TextBlock { Text = $"Version {AppVersion}", Foreground = Theme.Accent2, FontSize = 13, FontWeight = FontWeights.SemiBold });
        versionStack.Children.Add(new TextBlock { Text = $"Released {VersionDate}", Foreground = Theme.Faint, FontSize = 11 });
        versionBox.Child = versionStack;
        DockPanel.SetDock(versionBox, Dock.Left);
        header.Children.Add(versionBox);

        _checkButton = Theme.Btn("↻  Check for Updates", primary: false, () => _ = CheckForUpdatesAsync(auto: false));
        _checkLabel = (TextBlock)_checkButton.Child;
        DockPanel.SetDock(_checkButton, Dock.Right);
        header.Children.Add(_checkButton);

        // updates install themselves — this is a status line, not a prompt
        _updateNotice = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _updateNotice.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = Theme.Good, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        _updateNoticeText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
        _updateNotice.Children.Add(_updateNoticeText);
        DockPanel.SetDock(_updateNotice, Dock.Right);
        header.Children.Add(_updateNotice);

        var startupToggle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        startupToggle.Children.Add(new TextBlock
        {
            Text = "Start with Windows",
            FontSize = 12,
            Foreground = Theme.Dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        });
        startupToggle.Children.Add(Theme.Toggle(_controlSettings.StartWithWindows, on =>
        {
            _controlSettings.StartWithWindows = on;
            try
            {
                _controlSettings.Save();
            }
            catch
            {
                // non-fatal — the task change below still applies for this install
            }

            ElevationBroker.SetStartWithWindows(on);
        }));
        DockPanel.SetDock(startupToggle, Dock.Right);
        header.Children.Add(startupToggle);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---- three-column card grid (everything on one screen) ----
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });

        var fanColumn = new StackPanel { Margin = new Thickness(0, 0, 7, 0) };
        fanColumn.Children.Add(BuildFanCard());
        Grid.SetColumn(fanColumn, 0);
        grid.Children.Add(fanColumn);

        var middleColumn = new StackPanel { Margin = new Thickness(7, 0, 7, 0) };
        middleColumn.Children.Add(BuildPumpCard());
        var rgbCard = BuildRgbCard();
        rgbCard.Margin = new Thickness(0, 14, 0, 0);
        middleColumn.Children.Add(rgbCard);
        var lcdCard = BuildLcdCard();
        lcdCard.Margin = new Thickness(0, 14, 0, 0);
        middleColumn.Children.Add(lcdCard);
        Grid.SetColumn(middleColumn, 1);
        grid.Children.Add(middleColumn);

        var hardwareColumn = new StackPanel { Margin = new Thickness(7, 0, 0, 0) };
        hardwareColumn.Children.Add(BuildHardwareCard());
        Grid.SetColumn(hardwareColumn, 2);
        grid.Children.Add(hardwareColumn);

        root.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
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
        var card = Theme.CardShell("✻", "Fan Control", "curves & manual duty · every fan in the system", out var body, out var head);
        var badge = Theme.StatusBadge("Off", Theme.Faint, out _fanDot, out _fanBadge);
        badge.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);

        var controls = new WrapPanel { Orientation = Orientation.Horizontal };

        _modeDrop = new DmDropdown(Enum.GetNames<ControlMode>(), (int)_controlSettings.Mode, 104);
        _modeDrop.SelectionChanged += _ => OnControlSettingChanged();
        controls.Children.Add(LabelledInline("Mode", _modeDrop));

        _sourceDrop = new DmDropdown(Enum.GetNames<CurveSource>(), (int)_controlSettings.Source, 104);
        _sourceDrop.SelectionChanged += _ => OnControlSettingChanged();
        controls.Children.Add(LabelledInline("Curve source", _sourceDrop));

        _dutySlider = new DmSlider(0, 100, _controlSettings.ManualDutyPercent, 170);
        _dutySlider.ValueChanged += _ => OnControlSettingChanged();
        _dutyLabel = new TextBlock { Text = $"{_controlSettings.ManualDutyPercent}%", FontSize = 13, Foreground = Theme.Accent2, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 40 };
        var dutyRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        dutyRow.Children.Add(_dutySlider);
        dutyRow.Children.Add(_dutyLabel);
        controls.Children.Add(LabelledInline("Manual duty", dutyRow));

        body.Children.Add(controls);
        body.Children.Add(_controlStatus);
        body.Children.Add(_fanRows);
        return card;
    }

    private Border BuildPumpCard()
    {
        var card = Theme.CardShell("≋", "Pump Control", "independent duty · never below 50%", out var body, out _);

        var controls = new WrapPanel { Orientation = Orientation.Horizontal };
        _pumpSlider = new DmSlider(50, 100, Math.Clamp(_controlSettings.PumpDutyPercent, 50, 100), 200);
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

    private Border BuildLcdCard()
    {
        var card = Theme.CardShell("▣", "Screen Control", "pump LCD + fan LCDs · off or a plain background", out var body, out _);
        RebuildLcdButtons();
        body.Children.Add(_lcdButtons);
        body.Children.Add(_lcdStatus);
        UpdateLcdStatusText();
        return card;
    }

    private void RebuildLcdButtons()
    {
        _lcdButtons.Children.Clear();
        foreach (var (mode, label) in new[]
        {
            (LcdMode.Unmanaged, "Leave alone"),
            (LcdMode.Off, "Off"),
            (LcdMode.Black, "Black"),
            (LcdMode.White, "White"),
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
            LcdMode.Off => "All screens off (backlight dark). Applies while fan control is running.",
            LcdMode.Black => "All screens on with a plain black background.",
            LcdMode.White => "All screens on with a plain white background.",
            _ => "Screens untouched — they keep showing whatever they show.",
        };
    }

    private Border BuildHardwareCard()
    {
        var card = Theme.CardShell("⚙", "Detected hardware", "every device with its unique id", out var body, out var head);
        _rescanButton = Theme.Btn("↻  Rescan", primary: false, () => _ = RefreshDevicesAsync());
        DockPanel.SetDock(_rescanButton, Dock.Right);
        head.Children.Add(_rescanButton);
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

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
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
            _hardwareRows.Children.Add(LoadingRow("Scanning for devices…"));
        }

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
                _hardwareRows.Children.Add(HardwarePill("Corsair iCUE LINK hub", Shorten(hub.SerialNumber), "HID"));
            }

            foreach (var lcd in scan.HidDevices.Where(d => d.Kind == DeviceKind.CorsairLcd))
            {
                _hardwareRows.Children.Add(HardwarePill("Corsair pump/res LCD", lcd.SerialNumber, "HID"));
            }

            foreach (var (name, id, tag) in chainRows)
            {
                _hardwareRows.Children.Add(HardwarePill(name, Shorten(id), tag));
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
                _hardwareRows.Children.Add(HardwarePill($"Lian Li SL V3 dongle ({role})", dongle.UsbId.ToString(), "WinUSB"));
            }

            var fanNodes = scan.UsbTree
                .Where(n => n.IsPhysicalDevice && n.UsbId is { } id
                    && KnownDeviceRegistry.Identify(id)?.Kind == DeviceKind.LianLiSlv3FanNode)
                .Select(n => n.PnpInstanceId.Split('\\').LastOrDefault() ?? "?")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < fanNodes.Count; i++)
            {
                _hardwareRows.Children.Add(HardwarePill($"SL V3 fan LCD node {i + 1}/{fanNodes.Count}", fanNodes[i], "WinUSB"));
            }

            foreach (var screen in scan.SerialPorts.Where(p => p.Identification?.Kind == DeviceKind.TurzxScreen))
            {
                _hardwareRows.Children.Add(HardwarePill("Turzx/Turing screen", screen.SerialHint, screen.ComPort));
            }

            foreach (var aura in scan.HidDevices.Where(d => d.Kind == DeviceKind.MotherboardRgbController)
                .DistinctBy(d => d.UsbId))
            {
                _hardwareRows.Children.Add(HardwarePill("ASUS Aura LED controller", aura.UsbId.ToString(), "HID"));
            }

            foreach (var gpu in _loop?.GpuInventory ?? [])
            {
                _hardwareRows.Children.Add(HardwarePill(
                    gpu.Gpu.Name,
                    $"{gpu.Gpu.SubVendor:X4}:{gpu.Gpu.SubDevice:X4} · {gpu.Partner}",
                    gpu.Ene is not null ? "RGB" : "GPU"));
            }

            foreach (var stick in _loop?.RamInventory ?? [])
            {
                _hardwareRows.Children.Add(HardwarePill(
                    $"RAM · {stick.Manufacturer} {stick.PartNumber}",
                    $"SPD 0x{stick.SpdAddress:X2} · {stick.BusName}",
                    "SMBus"));
            }

            if (_hardwareRows.Children.Count == 0)
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

        _controlSettings.Mode = (ControlMode)_modeDrop.SelectedIndex;
        _controlSettings.Source = (CurveSource)_sourceDrop.SelectedIndex;
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

    private void ApplyControlState()
    {
        _dutySlider.Enabled = _controlSettings.Mode == ControlMode.Manual;
        _sourceDrop.IsHitTestVisible = _controlSettings.Mode == ControlMode.Curve;
        _sourceDrop.Opacity = _controlSettings.Mode == ControlMode.Curve ? 1.0 : 0.45;
        _pumpSlider.Enabled = _controlSettings.Mode != ControlMode.Off;

        if (_controlSettings.Mode == ControlMode.Off)
        {
            if (_loop is not null)
            {
                var loop = _loop;
                _loop = null;
                Task.Run(loop.Stop); // hardware release can take a moment — keep the UI responsive
            }

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
                    _fanRows.Children.Clear();
                    _fanRows.Children.Add(LoadingRow("Searching for fans…"));
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
            _fanRows.Children.Clear();
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
            _controlStatus.Text = status.Mode == ControlMode.Manual
                ? $"Manual — all fans at {status.TargetDutyPercent}%"
                : $"{status.SourceName} {temp}  →  fans {status.TargetDutyPercent}%";
            _controlStatus.Foreground = Theme.Text;
        }

        _fanRows.Children.Clear();
        foreach (var device in status.Devices.Where(d => !d.IsPump))
        {
            var rpm = device.Rpm is { } r ? $"{r} rpm" : "no rpm";
            var brush = device.Rpm is null ? Theme.Faint : Theme.Accent2;
            _fanRows.Children.Add(DeviceRow($"{device.Family} · {device.Name}", device.Id, $"{rpm} @ {device.AppliedDutyPercent}%", brush));
        }

        foreach (var warning in status.Warnings)
        {
            _fanRows.Children.Add(new TextBlock { Text = "⚠  " + warning, Foreground = Theme.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 4) });
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
            // updates are hands-off: download and install silently, then relaunch in the tray
            _pendingUpdate = info;
            _checkButton.Visibility = Visibility.Collapsed;
            _updateNotice.Visibility = Visibility.Visible;
            _updateNoticeText.Text = $"Updating to {info.Tag}…";
            RestoreCheckButton();
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
            _updateNoticeText.Text = "Update download failed — opening the releases page";
            Process.Start(new ProcessStartInfo(_pendingUpdate.PageUrl) { UseShellExecute = true });
            return;
        }

        _downloading = false;
        _updateNoticeText.Text = "Installing…";
        LogLine($"auto-update: installing {_pendingUpdate.Tag}");
        PrepareExit(); // release devices and the tray icon before the installer replaces us
        Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
        });
        Application.Current.Shutdown();
    }
}
