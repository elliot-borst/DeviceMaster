using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private TextBox _hubBox = null!;
    private TextBlock _hubStatus = null!;

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
        var page = new StackPanel { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(MainWindow.PageTitle("Servers", "every DeviceMaster machine on your tailnet"));

        var card = Theme.CardShell("⇄", "Fleet hub", "where the Servers list comes from", out var body, out _);
        var row = new DockPanel { LastChildFill = true };
        var save = Theme.Btn("Save", primary: true, () =>
        {
            _setHubUrl(_hubBox.Text.Trim());
            _ = RefreshAsync();
        });
        save.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(save, Dock.Right);
        row.Children.Add(save);
        _hubBox = new TextBox
        {
            Text = _getHubUrl(),
            FontSize = 12.5,
            Background = Theme.Inset,
            Foreground = Theme.Text,
            BorderBrush = Theme.Line2,
            CaretBrush = Theme.Text,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(_hubBox);
        body.Children.Add(Theme.InsetRow(row));
        _hubStatus = new TextBlock { FontSize = 12, Foreground = Theme.Faint, Margin = new Thickness(2, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        body.Children.Add(_hubStatus);
        page.Children.Add(card);
        page.Children.Add(_content);

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

    private async Task RefreshAsync()
    {
        var hub = _getHubUrl().TrimEnd('/');
        if (hub.Length == 0)
        {
            _machines = [];
            _error = "Enter the fleet hub's address, e.g. https://devicemaster.<your-tailnet>.ts.net";
            Render();
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

    private void Render()
    {
        _hubStatus.Text = _error ?? $"{_machines.Count(m => m.Online)} of {_machines.Count} online";
        _hubStatus.Foreground = _error is null ? Theme.Faint : Theme.Warn;
        RenderSidebar();
        _content.Children.Clear();
        if (_machines.FirstOrDefault(m => m.Name == _selected) is { } machine)
        {
            _content.Children.Add(MachineCard(machine, detailed: true));
            return;
        }

        var grid = new WrapPanel();
        foreach (var m in _machines)
        {
            var card = MachineCard(m, detailed: false);
            card.Width = 330;
            card.Cursor = System.Windows.Input.Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => Select(m.Name);
            grid.Children.Add(card);
        }

        _content.Children.Add(grid);
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
        var meta = string.Join(" · ", new[] { Seen(m), m.Platform, m.Version?.Split('+')[0] is { Length: > 0 } v ? $"v{v.Split('.')[0]}" : null }
            .Where(s => !string.IsNullOrEmpty(s)));
        var card = Theme.CardShell("●", m.Name, meta, out var body, out var head);
        card.Margin = new Thickness(0, 0, 12, 12);
        var dot = Dot(StateBrush(m));
        dot.Margin = new Thickness(0, 0, 4, 0);
        DockPanel.SetDock(dot, Dock.Right);
        head.Children.Add(dot);

        var stats = new WrapPanel();
        void Stat(string k, string v)
        {
            var tile = new StackPanel { Margin = new Thickness(0, 0, 18, 6), MinWidth = detailed ? 110 : 80 };
            tile.Children.Add(new TextBlock { Text = k, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint });
            tile.Children.Add(new TextBlock { Text = v, FontSize = detailed ? 28 : 20, FontWeight = FontWeights.Bold, Foreground = Theme.Text });
            stats.Children.Add(tile);
        }

        Stat("CPU", Deg(m.CpuTemperatureC));
        Stat("GPU", Deg(m.GpuTemperatureC));
        Stat("FANS", m.FanDutyPercent is { } d ? $"{d}%" : "—");
        if (m.CoolantTemperatureC is not null)
        {
            Stat("COOLANT", Deg(m.CoolantTemperatureC));
        }

        if (m.PumpDutyPercent is { } p)
        {
            Stat("PUMP", $"{p}%");
        }

        body.Children.Add(stats);

        if (!m.Online)
        {
            body.Children.Add(Note($"Not answering{(m.Error is null ? "" : $" — {m.Error}")}. It keeps running on its own settings.", Theme.Dim));
        }
        else if (m.Failsafe)
        {
            body.Children.Add(Note("Failsafe: fans at 100 % (temperature unreadable)", Theme.Danger));
        }

        if (!detailed)
        {
            return card;
        }

        body.Children.Add(Note($"Fans follow {m.Source ?? "—"}{(m.SourceTemperatureC is { } st ? $" at {st:F1} °C" : "")} · {m.Mode}", Theme.Faint));
        if (m.Capabilities is { } c)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (on, label) in new[]
            {
                (c.Pump, "Pump"), (c.CorsairHubs, "Corsair"), (c.LianLiFans, "Lian Li"), (c.MotherboardFans, "Board fans"),
                (c.MotherboardRgb, "Board RGB"), (c.RamRgb, "RAM RGB"), (c.GpuRgb, "GPU RGB"), (c.PumpLcd, "Pump LCD"),
                (c.FanLcds, "Fan LCDs"), (c.Turzx, "Turzx"),
            }.Where(x => x.Item1))
            {
                chips.Children.Add(new Border
                {
                    Background = Theme.Card2,
                    BorderBrush = Theme.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(99),
                    Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(0, 0, 6, 6),
                    Child = new TextBlock { Text = label, FontSize = 11.5, Foreground = Theme.Dim },
                });
            }

            body.Children.Add(chips);
        }

        if (_detail is { } status)
        {
            foreach (var warning in status.Warnings)
            {
                body.Children.Add(Note("⚠ " + warning, Theme.Warn));
            }

            foreach (var device in status.Devices.Where(d => !d.IsScreen))
            {
                body.Children.Add(Note($"{device.Name}{(device.IsPump ? " (pump)" : "")} · {device.Family} · {(device.Rpm is { } rpm ? $"{rpm} rpm" : "—")} · {device.AppliedDutyPercent}%", Theme.Dim));
            }
        }

        return card;
    }

    private static TextBlock Note(string text, Brush brush) =>
        new() { Text = text, FontSize = 12.5, Foreground = brush, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
}
