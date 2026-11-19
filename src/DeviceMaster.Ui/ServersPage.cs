using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeviceMaster.Control;

namespace DeviceMaster.Ui;

/// <summary>
/// Fleet (#33 phase 3): the Servers section — a list of every machine in the sidebar, and a page
/// with the fleet overview or one machine's detail. Everything comes from the fleet hub's
/// read-only JSON (<c>/api/fleet/machines</c>); the hub holds the agent tokens, this app holds none.
/// </summary>
public sealed class ServersPage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private readonly Func<string> _getHubUrl;
    private readonly Action<string> _setHubUrl;
    private readonly Action _showPage;
    private readonly Dispatcher _dispatcher;

    private readonly StackPanel _sidebarList = new();
    private readonly StackPanel _content = new();
    private TextBlock _hubAddress = null!;
    private TextBlock _hubStatus = null!;

    /// <summary>Overview tiles: one column per this many pixels of page width, 1–3 columns.</summary>
    private const double TileTargetWidth = 520;
    private UniformGrid? _tiles;

    private List<MachineSummary> _machines = [];
    private ControlStatus? _detail;
    private string? _selected;
    private string? _error;

    public ServersPage(Func<string> getHubUrl, Action<string> setHubUrl, Action showPage, Dispatcher dispatcher)
    {
        _getHubUrl = getHubUrl;
        _setHubUrl = setHubUrl;
        _showPage = showPage;
        _dispatcher = dispatcher;
    }

    /// <summary>The machines list that sits in the left-hand menu under the page buttons.</summary>
    public UIElement SidebarList
    {
        get
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            wrap.Children.Add(new TextBlock
            {
                Text = "SERVERS",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Faint,
                Margin = new Thickness(12, 0, 0, 4),
            });
            wrap.Children.Add(_sidebarList);
            return wrap;
        }
    }

    public UIElement Build()
    {
        // fills the window: the overview tiles size themselves from the page width
        var page = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        page.Children.Add(MainWindow.PageTitle("Servers", "every DeviceMaster machine on your tailnet"));

        // the hub is found automatically on the tailnet (an address set in the settings file still wins)
        var card = Theme.CardShell("⇄", "Fleet hub", "found automatically on your tailnet", out var body, out var head);
        card.Margin = new Thickness(0, 0, 0, 16);
        head.Margin = new Thickness(0);
        _hubStatus = new TextBlock { FontSize = 12.5, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), MaxWidth = 560, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right };
        DockPanel.SetDock(_hubStatus, Dock.Right);
        head.Children.Add(_hubStatus);
        _hubAddress = new TextBlock
        {
            FontSize = 13,
            FontFamily = Theme.Mono,
            Foreground = Theme.Dim,
            Margin = new Thickness(24, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(_hubAddress, Dock.Left);
        head.Children.Add(_hubAddress);
        page.Children.Add(card);
        page.Children.Add(_content);
        _content.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && _tiles is not null)
            {
                _tiles.Columns = Columns(e.NewSize.Width);
            }
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => _ = RefreshAsync();
        timer.Start();
        _ = RefreshAsync();
        return page;
    }

    /// <summary>Shows one machine (null = the overview of all of them).</summary>
    public void Select(string? name)
    {
        _selected = name;
        _detail = null;
        Render();
        _showPage();
        _ = RefreshAsync();
    }

    private bool _busy;
    private long _discoverNotBefore;

    /// <summary>The fleet hub's conventional name on a tailnet: devicemaster.&lt;tailnet&gt;.ts.net.</summary>
    public const string HubHostName = "devicemaster";

    /// <summary>
    /// This machine's tailnet DNS suffix (e.g. "example-tailnet.ts.net"): from the Tailscale
    /// adapter's connection-specific DNS suffix, or failing that from <c>tailscale status --json</c>.
    /// </summary>
    public static string? TailnetSuffix()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                if (props.UnicastAddresses.Any(a => AgentServer.IsTailnet(a.Address))
                    && props.DnsSuffix is { Length: > 0 } suffix && suffix.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
                {
                    return suffix.Trim('.');
                }
            }
        }
        catch
        {
            // fall through to the CLI
        }

        try
        {
            var exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            if (!System.IO.File.Exists(exe))
            {
                return null;
            }

            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "status --json")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5_000);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("MagicDNSSuffix", out var s) && s.GetString() is { Length: > 0 } suffix ? suffix.Trim('.') : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds the hub at devicemaster.&lt;tailnet&gt; and checks it really is one. Null when not found.</summary>
    private static async Task<(string? Url, string? Suffix)> DiscoverHubAsync()
    {
        var suffix = await Task.Run(TailnetSuffix);
        if (suffix is null)
        {
            return (null, null);
        }

        var url = $"https://{HubHostName}.{suffix}";
        try
        {
            using var response = await Http.GetAsync(url + "/api/fleet/machines");
            return (response.IsSuccessStatusCode ? url : null, suffix);
        }
        catch
        {
            return (null, suffix);
        }
    }

    private async Task RefreshAsync()
    {
        var hub = _getHubUrl().TrimEnd('/');
        if (hub.Length == 0)
        {
            _machines = [];
            if (Environment.TickCount64 < _discoverNotBefore)
            {
                return;
            }

            _discoverNotBefore = Environment.TickCount64 + 60_000; // don't probe every 2 s when there's no hub
            _error = "Looking for the fleet hub on your tailnet…";
            Render();
            var (found, suffix) = await DiscoverHubAsync();
            if (found is not null)
            {
                _setHubUrl(found); // remembered in the settings file
                _ = RefreshAsync();
                return;
            }

            _error = suffix is null
                ? "Tailscale isn't running here, so the fleet hub can't be found. Start Tailscale."
                : $"No fleet hub answered at {HubHostName}.{suffix}.";
            _ = _dispatcher.InvokeAsync(Render);
            return;
        }

        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var json = await Http.GetStringAsync(hub + "/api/fleet/machines");
            _machines = JsonSerializer.Deserialize<List<MachineSummary>>(json, AgentApi.Json) ?? [];
            if (_selected is { } name)
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(hub + "/api/fleet/machines/" + Uri.EscapeDataString(name)));
                _detail = doc.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object
                    ? s.Deserialize<ControlStatus>(AgentApi.Json)
                    : null;
            }

            _error = null;
        }
        catch (Exception ex)
        {
            _error = $"Can't reach the hub ({ex.Message})";
        }
        finally
        {
            _busy = false;
        }

        _ = _dispatcher.InvokeAsync(Render);
    }

    // ---------- rendering ----------

    private static string State(MachineSummary m) => !m.Online || m.Failsafe ? "bad" : m.WarningCount > 0 ? "warn" : "ok";

    private static Brush StateBrush(MachineSummary m) => State(m) switch
    {
        "bad" => Theme.Danger,
        "warn" => Theme.Warn,
        _ => Theme.Good,
    };

    private static string Deg(double? v) => v is { } d ? $"{d:F0} °C" : "—";

    private static double? Hottest(MachineSummary m) =>
        new[] { m.CpuTemperatureC, m.GpuTemperatureC }.Where(v => v is not null).Max();

    private static string Seen(MachineSummary m) => m.Online
        ? "online"
        : m.SecondsSinceSeen is { } s ? $"offline · seen {(s < 90 ? $"{s} s" : s < 5400 ? $"{s / 60} min" : $"{s / 3600} h")} ago" : "never seen";

    private static Ellipse Dot(Brush brush) => new() { Width = 8, Height = 8, Fill = brush, VerticalAlignment = VerticalAlignment.Center };

    private static int Columns(double width) => Math.Clamp((int)(width / TileTargetWidth), 1, 3);

    private void Render()
    {
        var hub = _getHubUrl().TrimEnd('/');
        _hubAddress.Text = hub.Length > 0 ? hub : "searching…";
        _hubStatus.Text = _error ?? $"{_machines.Count(m => m.Online)} of {_machines.Count} online";
        _hubStatus.Foreground = _error is null ? Theme.Faint : Theme.Warn;
        RenderSidebar();
        _content.Children.Clear();
        _tiles = null;
        if (_machines.FirstOrDefault(m => m.Name == _selected) is { } machine)
        {
            var detail = MachineCard(machine, detailed: true);
            detail.Margin = new Thickness(0);
            _content.Children.Add(detail);
            return;
        }

        _tiles = new UniformGrid { Columns = Columns(_content.ActualWidth), VerticalAlignment = VerticalAlignment.Top };
        foreach (var m in _machines)
        {
            var card = MachineCard(m, detailed: false);
            card.Cursor = System.Windows.Input.Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => Select(m.Name);
            _tiles.Children.Add(card);
        }

        _content.Children.Add(_tiles);
    }

    private void RenderSidebar()
    {
        _sidebarList.Children.Clear();
        AddRow("All servers", _machines.Count == 0 ? Theme.Faint : _machines.Any(m => State(m) == "bad") ? Theme.Danger
            : _machines.Any(m => State(m) == "warn") ? Theme.Warn : Theme.Good, null, null);
        foreach (var m in _machines)
        {
            AddRow(m.Name, StateBrush(m), m.Online ? Deg(Hottest(m)) : "off", m.Name);
        }

        void AddRow(string label, Brush dot, string? right, string? name)
        {
            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(Dot(dot));
            if (right is not null)
            {
                var r = new TextBlock { Text = right, FontSize = 11.5, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(r, Dock.Right);
                row.Children.Add(r);
            }

            var selected = name == _selected;
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Margin = new Thickness(10, 0, 6, 0),
                Foreground = selected ? Theme.Text : Theme.Dim,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var button = new Border
            {
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(10),
                Background = selected ? Theme.Card2 : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = row,
            };
            button.MouseLeftButtonUp += (_, _) => Select(name);
            _sidebarList.Children.Add(button);
        }
    }

    private Border MachineCard(MachineSummary m, bool detailed)
    {
        var meta = string.Join(" · ", new[]
            {
                m.Online && m.UptimeSeconds is { } up ? $"up {Uptime(up)}" : Seen(m),
                m.Platform,
                m.Version?.Split('+')[0] is { Length: > 0 } v ? $"v{v.Split('.')[0]}" : null,
            }
            .Where(s => !string.IsNullOrEmpty(s)));
        var card = Theme.CardShell("●", m.Name, meta, out var body, out var head);
        card.Margin = new Thickness(0, 0, 16, 16);
        card.Padding = new Thickness(24, 20, 24, 22);
        card.VerticalAlignment = VerticalAlignment.Stretch;
        head.Margin = new Thickness(0, 0, 0, 18);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (m.Online && m.WarningCount > 0)
        {
            badges.Children.Add(Chip($"{m.WarningCount} warning{(m.WarningCount == 1 ? "" : "s")}", Theme.Warn));
        }

        if (m.Online && m.InSync is { } inSync)
        {
            badges.Children.Add(Chip(inSync ? "in sync" : "differs from fleet", inSync ? Theme.Good : Theme.Warn));
        }

        var dot = Dot(StateBrush(m));
        dot.Width = dot.Height = 10;
        dot.Margin = new Thickness(6, 0, 2, 0);
        badges.Children.Add(dot);
        DockPanel.SetDock(badges, Dock.Right);
        head.Children.Add(badges);

        // headline figures: temperatures with their load underneath, then the fans
        var figures = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 6) };
        figures.Children.Add(Figure("CPU", Deg(m.CpuTemperatureC), Load(m.CpuLoadPercent), detailed));
        figures.Children.Add(Figure("GPU", Deg(m.GpuTemperatureC), Load(m.GpuLoadPercent), detailed));
        figures.Children.Add(Figure("FANS", m.FanDutyPercent is { } d ? $"{d}%" : "—",
            m.FanCount > 0 ? $"{m.FanCount} fan{(m.FanCount == 1 ? "" : "s")}" : "no fans", detailed));
        body.Children.Add(figures);

        if (!m.Online)
        {
            body.Children.Add(Note($"Not answering{(m.Error is null ? "" : $" — {m.Error}")}. It keeps running on its own settings.", Theme.Dim));
            return card;
        }

        if (m.Failsafe)
        {
            body.Children.Add(Note("Failsafe: fans at 100 % (temperature unreadable)", Theme.Danger));
        }

        // secondary facts in an inset strip: memory, containers, uptime
        var facts = new UniformGrid { Columns = 3 };
        facts.Children.Add(Fact("MEMORY", m.RamUsedGb is { } used && m.RamTotalGb is { } total ? $"{used:F1} / {total:F0} GB" : "—"));
        facts.Children.Add(Fact("CONTAINERS", m.Docker is { } dk ? $"{dk.Running} / {dk.Total} running" : "—"));
        facts.Children.Add(Fact("UPTIME", m.UptimeSeconds is { } secs ? Uptime(secs) : "—"));
        var strip = Theme.InsetRow(facts);
        strip.Margin = new Thickness(0, 12, 0, 0);
        strip.Padding = new Thickness(14, 10, 14, 10);
        body.Children.Add(strip);

        // storage: the biggest few filesystems with a bar each
        var disks = m.Storage.Where(s => s.TotalBytes > 0).OrderByDescending(s => s.TotalBytes).Take(detailed ? 12 : 3).ToList();
        foreach (var disk in disks)
        {
            body.Children.Add(DiskRow(disk));
        }

        if (m.Docker is { Stopped.Count: > 0 } docker)
        {
            body.Children.Add(Note($"Stopped: {string.Join(", ", docker.Stopped.Take(detailed ? 50 : 4))}{(!detailed && docker.Stopped.Count > 4 ? " …" : "")}", Theme.Warn));
        }

        body.Children.Add(Note($"Fans follow {m.Source ?? "—"}{(m.SourceTemperatureC is { } st ? $" at {st:F1} °C" : "")} · {m.Mode ?? "—"}", Theme.Faint));
        if (m.PushError is { Length: > 0 } pushError)
        {
            body.Children.Add(Note("Fleet settings not delivered: " + pushError, Theme.Warn));
        }

        if (!detailed)
        {
            return card;
        }

        if (m.Capabilities is { } c)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var (on, label) in new[]
            {
                (c.CorsairHubs, "Corsair"), (c.LianLiFans, "Lian Li"), (c.MotherboardFans, "Board fans"),
                (c.MotherboardRgb, "Board RGB"), (c.RamRgb, "RAM RGB"), (c.GpuRgb, "GPU RGB"), (c.Turzx, "Turzx"),
            }.Where(x => x.Item1))
            {
                chips.Children.Add(Chip(label, Theme.Dim, new Thickness(0, 0, 6, 6)));
            }

            body.Children.Add(chips);
        }

        if (_detail is { } status)
        {
            foreach (var warning in status.Warnings)
            {
                body.Children.Add(Note("⚠ " + warning, Theme.Warn));
            }

            foreach (var device in status.Devices)
            {
                body.Children.Add(Note($"{device.Name} · {device.Family} · {(device.Rpm is { } rpm ? $"{rpm} rpm" : "—")} · {device.AppliedDutyPercent}%", Theme.Dim));
            }
        }

        return card;
    }

    private static string Load(double? percent) => percent is { } p ? $"{p:F0}% load" : "load —";

    internal static string Uptime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return $"{(value >= 100 ? value.ToString("F0") : value.ToString("F1"))} {units[i]}";
    }

    private static StackPanel Figure(string label, string value, string sub, bool detailed)
    {
        var tile = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        tile.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint });
        tile.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = detailed ? 40 : 32,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Text,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        tile.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Theme.Dim });
        return tile;
    }

    private static StackPanel Fact(string label, string value)
    {
        var tile = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        tile.Children.Add(new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint });
        tile.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return tile;
    }

    private static StackPanel DiskRow(StorageInfo disk)
    {
        var used = Math.Clamp(disk.UsedPercent, 0, 100);
        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var label = new DockPanel();
        var right = new TextBlock { Text = $"{Bytes(disk.UsedBytes)} / {Bytes(disk.TotalBytes)} · {used:F0}%", FontSize = 12, Foreground = Theme.Dim };
        DockPanel.SetDock(right, Dock.Right);
        label.Children.Add(right);
        label.Children.Add(new TextBlock { Text = disk.Name, FontSize = 12.5, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(label);

        var bar = new Grid { Height = 7, Margin = new Thickness(0, 5, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - used, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(4), Background = Theme.Inset, BorderBrush = Theme.Line, BorderThickness = new Thickness(1) };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        var fill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = used >= 95 ? Theme.Danger : used >= 85 ? Theme.Warn : Theme.AccentGrad(),
        };
        bar.Children.Add(fill);
        row.Children.Add(bar);
        return row;
    }

    private static Border Chip(string text, Brush foreground, Thickness? margin = null) => new()
    {
        Background = Theme.Card2,
        BorderBrush = Theme.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(99),
        Padding = new Thickness(10, 2, 10, 3),
        Margin = margin ?? new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = foreground },
    };

    private static TextBlock Note(string text, Brush brush) =>
        new() { Text = text, FontSize = 12.5, Foreground = brush, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
}
