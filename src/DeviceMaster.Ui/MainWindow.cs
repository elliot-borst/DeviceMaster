using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeviceMaster.Control;
using WinForms = System.Windows.Forms;
using DeviceMaster.Core.Conflicts;
using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;
using DeviceMaster.Core.Updating;

namespace DeviceMaster.Ui;

public sealed class MainWindow : Window
{
    /// <summary>Whole-number app version, derived from the assembly version major (csproj is the single source).</summary>
    public static string AppVersion { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version?.Major ?? 0).ToString();

    public const string VersionDate = "2026-07-06";

    private static readonly Brush Bg = Solid(0x0F, 0x11, 0x15);
    private static readonly Brush Card = Solid(0x18, 0x1B, 0x21);
    private static readonly Brush Fg = Solid(0xE6, 0xE9, 0xEE);
    private static readonly Brush Dim = Solid(0x96, 0x9C, 0xA5);
    private static readonly Brush Accent = Solid(0x56, 0x9C, 0xFF);
    private static readonly Brush Good = Solid(0x3F, 0xB9, 0x50);
    private static readonly Brush Warn = Solid(0xF0, 0xB4, 0x3C);

    private readonly TextBlock _status = new() { FontSize = 12, Foreground = Solid(0x96, 0x9C, 0xA5), Margin = new Thickness(2, 10, 2, 0) };
    private readonly TextBlock _deviceSummary = new() { FontSize = 13, Foreground = Solid(0xE6, 0xE9, 0xEE), TextWrapping = TextWrapping.Wrap, LineHeight = 24 };
    private readonly TextBlock _conflictSummary = new() { FontSize = 12, Foreground = Solid(0xF0, 0xB4, 0x3C), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };

    private Button _checkButton = null!;
    private Button _refreshButton = null!;
    private StackPanel _updateNotice = null!;
    private TextBlock _updateNoticeText = null!;
    private UpdateInfo? _pendingUpdate;

    private readonly ControlSettings _controlSettings = ControlSettings.Load();
    private ControlLoop? _loop;
    private ComboBox _modeBox = null!;
    private ComboBox _sourceBox = null!;
    private Slider _dutySlider = null!;
    private TextBlock _dutyLabel = null!;
    private readonly TextBlock _controlStatus = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Solid(0xE6, 0xE9, 0xEE), Margin = new Thickness(0, 12, 0, 4) };
    private readonly TextBlock _controlDevices = new() { FontSize = 12, Foreground = Solid(0x96, 0x9C, 0xA5), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
    private bool _uiReady;
    private WinForms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private bool _trayHintShown;

    public MainWindow()
    {
        Title = $"DeviceMaster v{AppVersion}";
        Width = 820;
        Height = 680;
        Background = Bg;
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
    }

    // ---- system tray ----

    private void CreateTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "DeviceMaster",
            Visible = true,
        };
        try
        {
            _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        }
        catch
        {
            // no icon in odd hosting scenarios — the tray entry still works
        }

        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
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

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(20) };

        // ---- header: name + version on the left, update controls on the right ----
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 16) };

        var titleBlock = new StackPanel();
        titleBlock.Children.Add(new TextBlock
        {
            Text = "DeviceMaster",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = Fg,
        });
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 4, 0, 0) };
        versionRow.Children.Add(new TextBlock
        {
            Text = $"Version {AppVersion}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Accent,
        });
        versionRow.Children.Add(new TextBlock
        {
            Text = $"   released {VersionDate}",
            FontSize = 12,
            Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        titleBlock.Children.Add(versionRow);
        DockPanel.SetDock(titleBlock, Dock.Left);
        header.Children.Add(titleBlock);

        _checkButton = MakeButton("↻  Check for updates", primary: false);
        _checkButton.Click += async (_, _) => await CheckForUpdatesAsync(auto: false);
        _checkButton.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_checkButton, Dock.Right);
        header.Children.Add(_checkButton);

        // inline "update available" notice — replaces the check button when an update is pending
        _updateNotice = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _updateNotice.Children.Add(new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = Good,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        _updateNoticeText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _updateNotice.Children.Add(_updateNoticeText);
        var install = MakeButton("↓  Download && install", primary: true);
        install.Margin = new Thickness(14, 0, 0, 0);
        install.Click += async (_, _) => await StartUpdateAsync();
        _updateNotice.Children.Add(install);
        var later = MakeButton("Later", primary: false);
        later.Margin = new Thickness(8, 0, 0, 0);
        later.Click += (_, _) => DismissUpdateNotice();
        _updateNotice.Children.Add(later);
        DockPanel.SetDock(_updateNotice, Dock.Right);
        header.Children.Add(_updateNotice);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---- fan control card ----
        var controlCard = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 14),
        };
        var controlPanel = new StackPanel();

        var controlRow = new StackPanel { Orientation = Orientation.Horizontal };
        controlRow.Children.Add(new TextBlock
        {
            Text = "Fan control",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
        });

        _modeBox = MakeCombo(Enum.GetNames<ControlMode>());
        _modeBox.SelectedIndex = (int)_controlSettings.Mode;
        _modeBox.SelectionChanged += (_, _) => OnControlSettingChanged();
        controlRow.Children.Add(Labelled("Mode", _modeBox));

        _sourceBox = MakeCombo(Enum.GetNames<CurveSource>());
        _sourceBox.SelectedIndex = (int)_controlSettings.Source;
        _sourceBox.SelectionChanged += (_, _) => OnControlSettingChanged();
        controlRow.Children.Add(Labelled("Curve source", _sourceBox));

        _dutySlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = _controlSettings.ManualDutyPercent,
            Width = 180,
            IsSnapToTickEnabled = true,
            TickFrequency = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dutySlider.ValueChanged += (_, _) => OnControlSettingChanged();
        _dutyLabel = new TextBlock
        {
            Text = $"{_controlSettings.ManualDutyPercent}%",
            FontSize = 13,
            Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 38,
        };
        var sliderRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sliderRow.Children.Add(_dutySlider);
        sliderRow.Children.Add(_dutyLabel);
        controlRow.Children.Add(Labelled("Manual duty", sliderRow));

        controlPanel.Children.Add(controlRow);
        controlPanel.Children.Add(_controlStatus);
        controlPanel.Children.Add(_controlDevices);
        controlCard.Child = controlPanel;
        DockPanel.SetDock(controlCard, Dock.Top);
        root.Children.Add(controlCard);

        // ---- status bar at the bottom ----
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        // ---- device card ----
        var card = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
        };
        var cardContent = new DockPanel();

        var cardHeader = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 12) };
        var cardTitle = new TextBlock
        {
            Text = "Detected hardware",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
        };
        DockPanel.SetDock(cardTitle, Dock.Left);
        cardHeader.Children.Add(cardTitle);
        _refreshButton = MakeButton("↻  Rescan", primary: false);
        _refreshButton.Click += async (_, _) => await RefreshDevicesAsync();
        DockPanel.SetDock(_refreshButton, Dock.Right);
        cardHeader.Children.Add(_refreshButton);
        DockPanel.SetDock(cardHeader, Dock.Top);
        cardContent.Children.Add(cardHeader);

        DockPanel.SetDock(_conflictSummary, Dock.Bottom);
        cardContent.Children.Add(_conflictSummary);
        cardContent.Children.Add(new ScrollViewer
        {
            Content = _deviceSummary,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        card.Child = cardContent;
        root.Children.Add(card);
        return root;
    }

    private static Button MakeButton(string label, bool primary) => new()
    {
        Content = label,
        Padding = new Thickness(14, 7, 14, 7),
        FontSize = 13,
        FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = primary ? Solid(0x10, 0x12, 0x16) : Fg,
        Background = primary ? Accent : Solid(0x24, 0x28, 0x30),
        BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    private static SolidColorBrush Solid(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private static ComboBox MakeCombo(IEnumerable<string> items)
    {
        var box = new ComboBox { MinWidth = 96, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items)
        {
            box.Items.Add(item);
        }

        return box;
    }

    private static StackPanel Labelled(string label, UIElement element)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Solid(0x96, 0x9C, 0xA5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        panel.Children.Add(element);
        return panel;
    }

    // ---- fan control wiring ----

    private void OnControlSettingChanged()
    {
        if (!_uiReady)
        {
            return;
        }

        _controlSettings.Mode = (ControlMode)Math.Max(_modeBox.SelectedIndex, 0);
        _controlSettings.Source = (CurveSource)Math.Max(_sourceBox.SelectedIndex, 0);
        _controlSettings.ManualDutyPercent = (int)Math.Round(_dutySlider.Value);
        _dutyLabel.Text = $"{_controlSettings.ManualDutyPercent}%";

        try
        {
            _controlSettings.Save();
        }
        catch
        {
            // non-fatal — settings just won't persist
        }

        ApplyControlState();
    }

    private void ApplyControlState()
    {
        _dutySlider.IsEnabled = _controlSettings.Mode == ControlMode.Manual;
        _sourceBox.IsEnabled = _controlSettings.Mode == ControlMode.Curve;

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

    private void UpdateControlStatus()
    {
        var status = _loop?.Status;
        if (status is null || !status.Running)
        {
            _controlStatus.Text = "Control off — devices follow their own hardware/firmware curves.";
            _controlStatus.Foreground = Dim;
            _controlDevices.Text = "";
            return;
        }

        var temp = status.SourceTemperatureC is { } t ? $"{t:F1} °C" : "—";
        _controlStatus.Text = status.FailsafeActive
            ? $"FAILSAFE: {status.SourceName} unavailable — all fans at 100%"
            : status.Mode == ControlMode.Manual
                ? $"Manual: all fans at {status.TargetDutyPercent}%"
                : $"{status.SourceName} {temp}  →  fans {status.TargetDutyPercent}%";
        _controlStatus.Foreground = status.FailsafeActive ? Warn : Fg;

        var lines = status.Devices
            .GroupBy(d => d.Family)
            .Select(g =>
            {
                var fans = g.Where(d => !d.IsPump).ToList();
                var rpms = fans.Where(d => d.Rpm is not null).Select(d => d.Rpm!.Value).ToList();
                var pumps = g.Where(d => d.IsPump && d.Rpm is not null).Select(d => $"{d.Rpm} rpm").ToList();
                return $"{g.Key}: {fans.Count} fan target(s)"
                    + (rpms.Count > 0 ? $", ~{rpms.Average():F0} rpm" : "")
                    + (pumps.Count > 0 ? $"  ·  pump {string.Join(", ", pumps)} @100%" : "");
            })
            .ToList();
        lines.AddRange(status.Warnings.Select(w => "⚠ " + w));
        _controlDevices.Text = string.Join(Environment.NewLine, lines);

        if (_trayIcon is not null)
        {
            var tip = status.FailsafeActive
                ? "DeviceMaster — FAILSAFE, fans 100%"
                : $"DeviceMaster — {status.SourceName} {temp} → {status.TargetDutyPercent}%";
            _trayIcon.Text = tip.Length > 63 ? tip[..63] : tip;
        }
    }

    // ---- device scan ----

    private async Task RefreshDevicesAsync()
    {
        _refreshButton.IsEnabled = false;
        _deviceSummary.Text = "Scanning…";
        try
        {
            var (scan, conflicts) = await Task.Run(() =>
                (DeviceScanner.ScanAll(), ConflictingSoftwareChecker.FindConflicts()));

            var hubs = scan.HidDevices.Count(d => d.Kind == DeviceKind.CorsairLinkHub && d.MaxOutputReportLength > 0);
            var lcds = scan.HidDevices.Count(d => d.Kind == DeviceKind.CorsairLcd);
            var slv3Fans = scan.UsbTree.Count(n => n.IsPhysicalDevice
                && n.UsbId is { } id && KnownDeviceRegistry.Identify(id)?.Kind == DeviceKind.LianLiSlv3FanNode);
            var slv3Dongles = scan.UsbTree.Count(n => n.IsPhysicalDevice
                && n.UsbId is { } id && KnownDeviceRegistry.Identify(id)?.Kind == DeviceKind.LianLiSlv3Controller);
            var screens = scan.SerialPorts.Where(p => p.Identification?.Kind == DeviceKind.TurzxScreen).ToList();

            var lines = new List<string>
            {
                $"Corsair iCUE LINK hubs:  {hubs}",
                $"Corsair pump/res LCD modules:  {lcds}",
                $"Lian Li SL V3 wireless dongles:  {slv3Dongles} (TX/RX)",
                $"Lian Li SL V3 fans:  {slv3Fans}",
                $"Turzx/Turing screens:  {screens.Count}"
                    + (screens.Count > 0 ? $"  ({string.Join(", ", screens.Select(s => s.ComPort))})" : ""),
            };
            _deviceSummary.Text = string.Join(Environment.NewLine, lines);

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
            _deviceSummary.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _refreshButton.IsEnabled = true;
        }
    }

    // ---- updates ----

    private async Task CheckForUpdatesAsync(bool auto)
    {
        _checkButton.IsEnabled = false;
        if (!auto)
        {
            SetStatus("Checking for updates…", Dim);
        }

        var info = await Updater.CheckLatestAsync();
        _checkButton.IsEnabled = true;

        if (info is null)
        {
            if (!auto)
            {
                SetStatus($"Update check failed: {Updater.LastError}", Warn);
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
            SetStatus($"Update available: {info.Tag} (you have v{AppVersion})", Good);
        }
        else
        {
            SetStatus($"Up to date — v{AppVersion} is the latest version.", Dim);
        }
    }

    private async Task StartUpdateAsync()
    {
        if (_pendingUpdate?.SetupUrl is not { } setupUrl)
        {
            return;
        }

        SetStatus("Downloading update…", Dim);
        var installerPath = await Updater.DownloadInstallerAsync(setupUrl);
        if (installerPath is null)
        {
            SetStatus("Download failed — opening the releases page instead.", Warn);
            Process.Start(new ProcessStartInfo(_pendingUpdate.PageUrl) { UseShellExecute = true });
            return;
        }

        SetStatus("Starting installer…", Dim);
        PrepareExit(); // release devices and the tray icon before the installer replaces us
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private void DismissUpdateNotice()
    {
        _updateNotice.Visibility = Visibility.Collapsed;
        _checkButton.Visibility = Visibility.Visible;
        SetStatus(_pendingUpdate is { } p ? $"Update {p.Tag} postponed — use Check for updates any time." : "", Dim);
    }

    private void SetStatus(string text, Brush brush)
    {
        _status.Text = text;
        _status.Foreground = brush;
    }
}
