using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    public MainWindow()
    {
        Title = $"DeviceMaster v{AppVersion}";
        Width = 820;
        Height = 560;
        Background = Bg;
        Content = BuildLayout();
        Loaded += async (_, _) =>
        {
            await RefreshDevicesAsync();
            await CheckForUpdatesAsync(auto: true);
        };
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
