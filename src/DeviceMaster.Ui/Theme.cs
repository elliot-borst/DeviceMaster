using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DeviceMaster.Ui;

/// <summary>Dark theme shared with StarMaster: palette, card shells, buttons, dropdowns, sliders.</summary>
public static class Theme
{
    public static SolidColorBrush B(string hex)
    {
        hex = hex.TrimStart('#');
        var brush = new SolidColorBrush(Color.FromRgb(
            Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16)));
        brush.Freeze();
        return brush;
    }

    public static readonly Brush Bg = B("#0b0e16");
    public static readonly Brush Card = B("#141a2b");
    public static readonly Brush Card2 = B("#171d33");
    public static readonly Brush Line = B("#232a45");
    public static readonly Brush Line2 = B("#323c5e");
    public static readonly Brush Text = B("#e6ecfb");
    public static readonly Brush Dim = B("#94a0c2");
    public static readonly Brush Faint = B("#646f93");
    public static readonly Brush Accent = B("#79b0ff");
    public static readonly Brush Accent2 = B("#a9c8ff");
    public static readonly Brush Good = B("#5fe0c0");
    public static readonly Brush Warn = B("#ffd34d");
    public static readonly Brush Danger = B("#ff5d5d");
    public static readonly Brush Ink = B("#0a1228");
    public static readonly Brush Inset = B("#0f1322");
    public static readonly Brush TileBg = B("#16203a");
    public static readonly FontFamily Mono = new("Consolas");

    public static LinearGradientBrush AccentGrad()
    {
        var gradient = new LinearGradientBrush(Color.FromRgb(0x22, 0xd3, 0xee), Color.FromRgb(0xa8, 0x55, 0xf7), 0);
        gradient.Freeze();
        return gradient;
    }

    public static Border Btn(string text, bool primary, Action onClick)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = primary ? Ink : Text,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var button = new Border
        {
            Background = primary ? AccentGrad() : Card2,
            BorderBrush = primary ? Brushes.Transparent : Line2,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 8, 13, 8),
            Cursor = Cursors.Hand,
            Child = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.MouseLeftButtonUp += (_, _) => onClick();
        button.MouseEnter += (_, _) => button.Opacity = 0.85;
        button.MouseLeave += (_, _) => button.Opacity = 1.0;
        return button;
    }

    public static Border CardShell(string icon, string title, string subtitle, out StackPanel body, out DockPanel head)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var stack = new StackPanel();
        head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 15) };
        var tile = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = TileBg,
            BorderBrush = Line2,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = icon,
                FontSize = 16,
                Foreground = Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var titles = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, Foreground = Text, FontSize = 15.5, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = subtitle, Foreground = Faint, FontSize = 11.5 });
        DockPanel.SetDock(titles, Dock.Left);
        head.Children.Add(titles);
        stack.Children.Add(head);
        card.Child = stack;
        body = stack;
        return card;
    }

    public static StackPanel StatusBadge(string text, Brush color, out Border dot, out TextBlock label)
    {
        var badge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        label = new TextBlock { Text = text, Foreground = color, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        badge.Children.Add(dot);
        badge.Children.Add(label);
        return badge;
    }

    public static Border InsetRow(UIElement child) => new()
    {
        Margin = new Thickness(0, 0, 0, 7),
        Padding = new Thickness(12, 8, 12, 8),
        CornerRadius = new CornerRadius(10),
        Background = Inset,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        Child = child,
    };

    public static TextBlock SmallLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Dim,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };
}

/// <summary>Popup-based dropdown styled like StarMaster's (native ComboBox can't be themed from code).</summary>
public sealed class DmDropdown : Border
{
    private readonly TextBlock _label;
    private readonly Popup _popup;
    private readonly StackPanel _list;
    private readonly List<string> _items;

    public int SelectedIndex { get; private set; }

    public event Action<int>? SelectionChanged;

    public DmDropdown(IEnumerable<string> items, int selectedIndex, double minWidth = 110)
    {
        _items = items.ToList();
        SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(_items.Count - 1, 0));

        Background = Theme.Bg;
        BorderBrush = Theme.Line2;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(11, 7, 9, 7);
        Cursor = Cursors.Hand;
        MinWidth = minWidth;
        VerticalAlignment = VerticalAlignment.Center;

        var content = new DockPanel { LastChildFill = false };
        _label = new TextBlock { Text = Current, Foreground = Theme.Text, FontSize = 12.5 };
        DockPanel.SetDock(_label, Dock.Left);
        content.Children.Add(_label);
        var chevron = new TextBlock { Text = " ▾", Foreground = Theme.Faint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(chevron, Dock.Right);
        content.Children.Add(chevron);
        Child = content;

        _list = new StackPanel();
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = Theme.Card2,
                BorderBrush = Theme.Line2,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = _list,
                MinWidth = minWidth,
                Margin = new Thickness(0, 3, 0, 0),
            },
        };
        RebuildList();
        MouseLeftButtonUp += (_, _) => _popup.IsOpen = !_popup.IsOpen;
    }

    private string Current => _items.Count > 0 ? _items[SelectedIndex] : "";

    private void RebuildList()
    {
        _list.Children.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var row = new Border
            {
                Padding = new Thickness(11, 7, 11, 7),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = _items[i],
                    Foreground = i == SelectedIndex ? Theme.Accent2 : Theme.Text,
                    FontSize = 12.5,
                },
            };
            row.MouseEnter += (_, _) => row.Background = Theme.TileBg;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) =>
            {
                _popup.IsOpen = false;
                if (SelectedIndex != index)
                {
                    SelectedIndex = index;
                    _label.Text = Current;
                    RebuildList();
                    SelectionChanged?.Invoke(index);
                }
            };
            _list.Children.Add(row);
        }
    }
}

/// <summary>Minimal themed slider: inset track, gradient fill, draggable thumb, step snapping.</summary>
public sealed class DmSlider : Grid
{
    private readonly Border _track;
    private readonly Border _fill;
    private readonly Border _thumb;
    private double _value;

    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }

    public event Action<double>? ValueChanged;

    public DmSlider(double minimum, double maximum, double value, double width, double step = 5)
    {
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Width = width;
        Height = 24;
        Background = Brushes.Transparent; // hit-test the whole strip
        VerticalAlignment = VerticalAlignment.Center;

        _track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Theme.Inset,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fill = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Theme.AccentGrad(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _thumb = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = Theme.Text,
            BorderBrush = Theme.Line2,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_thumb);

        _value = Math.Clamp(value, minimum, maximum);
        SizeChanged += (_, _) => Render();
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); SetFromPosition(e.GetPosition(this).X); };
        MouseMove += (_, e) =>
        {
            if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            {
                SetFromPosition(e.GetPosition(this).X);
            }
        };
        MouseLeftButtonUp += (_, _) => ReleaseMouseCapture();
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, Minimum, Maximum);
            Render();
        }
    }

    public bool Enabled
    {
        set
        {
            IsHitTestVisible = value;
            Opacity = value ? 1.0 : 0.45;
        }
    }

    private void SetFromPosition(double x)
    {
        var fraction = Math.Clamp(x / Math.Max(ActualWidth, 1), 0, 1);
        var raw = Minimum + fraction * (Maximum - Minimum);
        var snapped = Math.Clamp(Math.Round(raw / Step) * Step, Minimum, Maximum);
        if (Math.Abs(snapped - _value) > 0.001)
        {
            _value = snapped;
            Render();
            ValueChanged?.Invoke(_value);
        }
    }

    private void Render()
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        var fraction = (Maximum - Minimum) <= 0 ? 0 : (_value - Minimum) / (Maximum - Minimum);
        _fill.Width = Math.Max(fraction * ActualWidth, 0);
        _thumb.Margin = new Thickness(Math.Clamp(fraction * ActualWidth - 7, 0, Math.Max(ActualWidth - 14, 0)), 0, 0, 0);
    }
}
