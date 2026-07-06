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

    // header / updater
    private Border _checkButton = null!;
    private StackPanel _updateNotice = null!;
    private TextBlock _updateNoticeText = null!;
    private UpdateInfo? _pendingUpdate;
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = Theme.Faint, Margin = new Thickness(4, 12, 4, 0) };

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

    // hardware card
    private readonly StackPanel _hardwareRows = new();
    private readonly TextBlock _conflictSummary = new() { FontSize = 12, Foreground = Theme.Warn, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private Border _rescanButton = null!;

    // tray
    private WinForms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private bool _trayHintShown;

    public MainWindow()
    {
        Title = $"DeviceMaster v{AppVersion}";
        Width = 1000;
        Height = 760;
        MinWidth = 940;
        MinHeight = 620;
        Background = Theme.Bg;
        try
        {
            Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), Theme.DarkScrollBarStyle());
        }
        catch
        {
            // worst case: default scrollbars
        }

        Content = BuildLayout();
        _uiReady = true;

        CreateTrayIcon();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateControlStatus();
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

        _checkButton = Theme.Btn("↻  Check for updates", primary: false, () => _ = CheckForUpdatesAsync(auto: false));
        DockPanel.SetDock(_checkButton, Dock.Right);
        header.Children.Add(_checkButton);

        _updateNotice = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _updateNotice.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = Theme.Good, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        _updateNoticeText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
        _updateNotice.Children.Add(_updateNoticeText);
        var install = Theme.Btn("↓  Download & install", primary: true, () => _ = StartUpdateAsync());
        install.Margin = new Thickness(14, 0, 0, 0);
        _updateNotice.Children.Add(install);
        var later = Theme.Btn("Later", primary: false, DismissUpdateNotice);
        later.Margin = new Thickness(8, 0, 0, 0);
        _updateNotice.Children.Add(later);
        DockPanel.SetDock(_updateNotice, Dock.Right);
        header.Children.Add(_updateNotice);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        // ---- two-column card grid ----
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftColumn = new StackPanel { Margin = new Thickness(0, 0, 9, 0) };
        leftColumn.Children.Add(BuildFanCard());
        var pumpCard = BuildPumpCard();
        pumpCard.Margin = new Thickness(0, 16, 0, 0);
        leftColumn.Children.Add(pumpCard);
        var rgbCard = BuildRgbCard();
        rgbCard.Margin = new Thickness(0, 16, 0, 0);
        leftColumn.Children.Add(rgbCard);
        Grid.SetColumn(leftColumn, 0);
        grid.Children.Add(leftColumn);

        var rightColumn = new StackPanel { Margin = new Thickness(9, 0, 0, 0) };
        rightColumn.Children.Add(BuildHardwareCard());
        Grid.SetColumn(rightColumn, 1);
        grid.Children.Add(rightColumn);

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
        var card = Theme.CardShell("✻", "Fan control", "curves & manual duty · both ecosystems", out var body, out var head);
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
        var card = Theme.CardShell("≋", "Pump", "independent duty · never below 50%", out var body, out _);

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

    private static readonly (byte R, byte G, byte B)[] SwatchColors =
    [
        (255, 255, 255), (255, 64, 64), (255, 140, 0), (255, 220, 0), (64, 220, 100),
        (34, 211, 238), (86, 130, 255), (168, 85, 247), (255, 105, 180),
    ];

    private Border BuildRgbCard()
    {
        var card = Theme.CardShell("◈", "Lighting", "static color · both ecosystems", out var body, out var head);

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
        foreach (var (r, g, b) in SwatchColors)
        {
            var selected = _controlSettings.RgbR == r && _controlSettings.RgbG == g && _controlSettings.RgbB == b;
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
            _rgbStatus.Text = "Lighting off — devices keep their own colors.";
        }
        else if (_controlSettings.Mode == ControlMode.Off)
        {
            _rgbStatus.Text = "Waiting for fan control — set Mode to Manual or Curve to apply lighting.";
        }
        else
        {
            _rgbStatus.Text = $"Static color #{_controlSettings.RgbR:X2}{_controlSettings.RgbG:X2}{_controlSettings.RgbB:X2} on all Corsair and Lian Li LEDs.";
        }
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

    private static Border DeviceRow(string name, string? id, string value, Brush valueBrush)
    {
        var row = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = name, Foreground = Theme.Text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        if (id is { Length: > 0 })
        {
            left.Children.Add(new TextBlock
            {
                Text = id,
                Foreground = Theme.Faint,
                FontFamily = Theme.Mono,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 1, 0, 0),
            });
        }

        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);
        var right = new TextBlock { Text = value, Foreground = valueBrush, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
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
                    chainRows.Add(($"{device.Family} · {device.Name}", device.Id,
                        device.IsPump ? "pump" : device.Family == "Corsair" ? "fan" : "wireless"));
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
                _hardwareRows.Children.Add(DeviceRow("Corsair iCUE LINK hub", Shorten(hub.SerialNumber), "HID", Theme.Dim));
            }

            foreach (var lcd in scan.HidDevices.Where(d => d.Kind == DeviceKind.CorsairLcd))
            {
                _hardwareRows.Children.Add(DeviceRow("Corsair pump/res LCD", lcd.SerialNumber, "HID", Theme.Dim));
            }

            foreach (var (name, id, tag) in chainRows)
            {
                _hardwareRows.Children.Add(DeviceRow(name, Shorten(id), tag, tag == "pump" ? Theme.Accent2 : Theme.Dim));
            }

            if (chainPending)
            {
                _hardwareRows.Children.Add(new TextBlock
                {
                    Text = "Link chain devices appear here once fan control has started…",
                    Foreground = Theme.Faint,
                    FontSize = 12,
                    Margin = new Thickness(2, 0, 0, 7),
                });
            }

            foreach (var dongle in scan.UsbTree
                .Where(n => n.IsPhysicalDevice && n.UsbId is { } id
                    && KnownDeviceRegistry.Identify(id)?.Kind == DeviceKind.LianLiSlv3Controller)
                .OrderBy(n => n.UsbId!.Value.Pid))
            {
                var role = dongle.UsbId!.Value.Pid == 0x8040 ? "TX · control" : "RX · telemetry";
                _hardwareRows.Children.Add(DeviceRow($"Lian Li SL V3 dongle ({role})", dongle.UsbId.ToString(), "WinUSB", Theme.Dim));
            }

            var fanNodes = scan.UsbTree
                .Where(n => n.IsPhysicalDevice && n.UsbId is { } id
                    && KnownDeviceRegistry.Identify(id)?.Kind == DeviceKind.LianLiSlv3FanNode)
                .Select(n => n.PnpInstanceId.Split('\\').LastOrDefault() ?? "?")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < fanNodes.Count; i++)
            {
                _hardwareRows.Children.Add(DeviceRow($"Lian Li SL V3 fan LCD node {i + 1}/{fanNodes.Count}", fanNodes[i], "WinUSB", Theme.Dim));
            }

            foreach (var screen in scan.SerialPorts.Where(p => p.Identification?.Kind == DeviceKind.TurzxScreen))
            {
                _hardwareRows.Children.Add(DeviceRow("Turzx/Turing screen", screen.SerialHint, screen.ComPort, Theme.Accent2));
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
        }
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
            _loop = new ControlLoop(_controlSettings);
            _loop.Start();
        }
        else
        {
            _loop.Apply(_controlSettings);
        }
    }

    private bool _chainRowsLive;

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
            SetBadge("Off", Theme.Faint);
            _controlStatus.Text = "Control off — devices follow their own hardware/firmware curves.";
            _controlStatus.Foreground = Theme.Dim;
            _fanRows.Children.Clear();
            _pumpCoolant.Text = "Control off — the pump follows the hub's own behaviour.";
            _pumpCoolant.Foreground = Theme.Dim;
            _pumpRows.Children.Clear();
            return;
        }

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
            var row = DeviceRow($"{device.Family} · {device.Name}", ShortId(device.Id), $"{rpm} @ {device.AppliedDutyPercent}%", brush);

            // Corsair channels get an "identify" pulse (100% for a few seconds) to map
            // channels to physical fans
            if (device is { Family: "Corsair", HubSerial: not null, Channel: not null } && row.Child is DockPanel rowPanel)
            {
                var identify = new TextBlock
                {
                    Text = "◉ identify",
                    Foreground = Theme.Faint,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Run this fan at 100% for 6 seconds",
                };
                identify.MouseEnter += (_, _) => identify.Foreground = Theme.Accent;
                identify.MouseLeave += (_, _) => identify.Foreground = Theme.Faint;
                var (hubSerial, channel) = (device.HubSerial, device.Channel.Value);
                identify.MouseLeftButtonUp += (_, _) => _loop?.PulseChannel(hubSerial, channel);
                DockPanel.SetDock(identify, Dock.Right);
                rowPanel.Children.Add(identify);
            }

            _fanRows.Children.Add(row);
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
            _pumpRows.Children.Add(DeviceRow($"{pump.Family} · {pump.Name}", ShortId(pump.Id), $"{rpm} @ {pump.AppliedDutyPercent}%", Theme.Accent2));
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

    private static string? ShortId(string? id) =>
        id is { Length: > 8 } ? "…" + id[^8..] : id;

    private void SetBadge(string text, Brush color)
    {
        _fanBadge.Text = text;
        _fanBadge.Foreground = color;
        _fanDot.Background = color;
    }

    // ---------- updates ----------

    private async Task CheckForUpdatesAsync(bool auto)
    {
        if (!auto)
        {
            SetStatus("Checking for updates…", Theme.Dim);
        }

        var info = await Updater.CheckLatestAsync();
        if (info is null)
        {
            if (!auto)
            {
                SetStatus($"Update check failed: {Updater.LastError}", Theme.Warn);
            }

            return;
        }

        var current = WholeVersion.Parse(AppVersion);
        if (WholeVersion.Compare(info.Version, current) > 0 && info.SetupUrl is not null)
        {
            _pendingUpdate = info;
            _updateNoticeText.Text = $"Version {info.Tag.TrimStart('v', 'V')} is available";
            _checkButton.Visibility = Visibility.Collapsed;
            _updateNotice.Visibility = Visibility.Visible;
            SetStatus($"Update available: {info.Tag} (you have v{AppVersion})", Theme.Good);
        }
        else
        {
            SetStatus($"Up to date — v{AppVersion} is the latest version.", Theme.Faint);
        }
    }

    private async Task StartUpdateAsync()
    {
        if (_pendingUpdate?.SetupUrl is not { } setupUrl)
        {
            return;
        }

        SetStatus("Downloading update…", Theme.Dim);
        var installerPath = await Updater.DownloadInstallerAsync(setupUrl);
        if (installerPath is null)
        {
            SetStatus("Download failed — opening the releases page instead.", Theme.Warn);
            Process.Start(new ProcessStartInfo(_pendingUpdate.PageUrl) { UseShellExecute = true });
            return;
        }

        SetStatus("Starting installer…", Theme.Dim);
        PrepareExit(); // release devices and the tray icon before the installer replaces us
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private void DismissUpdateNotice()
    {
        _updateNotice.Visibility = Visibility.Collapsed;
        _checkButton.Visibility = Visibility.Visible;
        SetStatus(_pendingUpdate is { } p ? $"Update {p.Tag} postponed — use Check for updates any time." : "", Theme.Dim);
    }

    private void SetStatus(string text, Brush brush)
    {
        _status.Text = text;
        _status.Foreground = brush;
    }
}
