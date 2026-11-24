using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeviceMaster.Control;

namespace DeviceMaster.Ui;

/// <summary>
/// CPU and GPU temperature over the last few minutes (#113): two polylines in the same colours
/// as the curve editor's CPU and GPU markers, a °C axis with gridlines, a time axis ending at
/// "now", and a legend with the current readings. A null reading or a pause in sampling leaves a
/// gap in the line. Same grid and type style as <see cref="CurveEditor"/>.
/// </summary>
public sealed class TemperatureChart : Canvas
{
    private const double PadLeft = 44, PadRight = 24, PadTop = 30, PadBottom = 28;

    /// <summary>A pause longer than this between samples breaks the line.</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(5);

    private IReadOnlyList<TemperatureHistory.Sample> _samples = [];
    private DateTimeOffset _now = DateTimeOffset.Now;
    private TimeSpan _window = TimeSpan.FromMinutes(10);

    public TemperatureChart()
    {
        Height = 260;
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
    }

    public TimeSpan Window => _window;

    /// <summary>Shows the history's last <paramref name="window"/> ending at <paramref name="now"/>.</summary>
    public void Show(TemperatureHistory history, DateTimeOffset now, TimeSpan window)
    {
        _now = now;
        _window = window;
        _samples = history.Window(now, window);
        Redraw();
    }

    private double PlotWidth => Math.Max(ActualWidth - PadLeft - PadRight, 10);
    private double PlotHeight => Math.Max(ActualHeight - PadTop - PadBottom, 10);

    private double X(DateTimeOffset t) => PadLeft + (1 - (_now - t).TotalSeconds / _window.TotalSeconds) * PlotWidth;

    private void Redraw()
    {
        Children.Clear();
        if (ActualWidth < 60)
        {
            return;
        }

        // y range: whole tens around what's on screen, at least 30 °C tall
        var values = _samples.SelectMany(s => new[] { s.CpuC, s.GpuC }).OfType<double>().ToList();
        double lo = 20, hi = 80;
        if (values.Count > 0)
        {
            lo = Math.Floor((values.Min() - 3) / 10) * 10;
            hi = Math.Ceiling((values.Max() + 3) / 10) * 10;
            if (hi - lo < 30)
            {
                var mid = (lo + hi) / 2;
                lo = Math.Floor((mid - 15) / 10) * 10;
                hi = lo + 30;
            }

            lo = Math.Max(lo, 0);
        }

        double Y(double c) => PadTop + (1 - (c - lo) / (hi - lo)) * PlotHeight;

        var yStep = hi - lo > 60 ? 20 : 10;
        for (var c = lo; c <= hi + 0.01; c += yStep)
        {
            Add(new Line { X1 = PadLeft, X2 = PadLeft + PlotWidth, Y1 = Y(c), Y2 = Y(c), Stroke = Theme.Line, StrokeThickness = 1, Opacity = c == lo ? 1 : 0.6 });
            AddText($"{c:0}°", 0, Y(c) - 8, 38, TextAlignment.Right);
        }

        // time axis: -10m … now
        var totalSeconds = _window.TotalSeconds;
        var xStep = totalSeconds switch { <= 60 => 10, <= 300 => 60, _ => 120 };
        for (var ago = 0; ago <= totalSeconds + 0.01; ago += xStep)
        {
            var x = X(_now.AddSeconds(-ago));
            Add(new Line { X1 = x, X2 = x, Y1 = PadTop, Y2 = PadTop + PlotHeight, Stroke = Theme.Line, StrokeThickness = 1, Opacity = 0.45 });
            var label = ago == 0 ? "now" : xStep < 60 ? $"-{ago}s" : $"-{ago / 60}m";
            var left = Math.Clamp(x - 20, PadLeft - 20, PadLeft + PlotWidth - 30);
            AddText(label, left, PadTop + PlotHeight + 6, 40, ago == 0 ? TextAlignment.Right : TextAlignment.Center);
        }

        DrawSeries(s => s.CpuC, CurveEditor.CpuBrush, Y);
        DrawSeries(s => s.GpuC, CurveEditor.GpuBrush, Y);

        // legend with current readings, top right
        var latest = _samples.Count > 0 ? _samples[^1] : (TemperatureHistory.Sample?)null;
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        legend.Children.Add(LegendItem("CPU", latest?.CpuC, CurveEditor.CpuBrush));
        legend.Children.Add(LegendItem("GPU", latest?.GpuC, CurveEditor.GpuBrush));
        legend.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(legend, PadLeft + PlotWidth - legend.DesiredSize.Width, 4);

        if (values.Count == 0)
        {
            AddText("waiting for temperature readings…", PadLeft, PadTop + PlotHeight / 2 - 8, PlotWidth, TextAlignment.Center);
        }
    }

    private void DrawSeries(Func<TemperatureHistory.Sample, double?> select, Brush brush, Func<double, double> y)
    {
        foreach (var run in TemperatureHistory.Segments(_samples, select, MaxGap))
        {
            if (run.Count == 1)
            {
                var (t, v) = run[0];
                Place(new Ellipse { Width = 4, Height = 4, Fill = brush }, X(t) - 2, y(v) - 2);
                continue;
            }

            var points = new PointCollection(run.Count);
            foreach (var (t, v) in run)
            {
                points.Add(new Point(X(t), y(v)));
            }

            Add(new Polyline { Points = points, Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
        }
    }

    private static StackPanel LegendItem(string name, double? value, Brush brush)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        item.Children.Add(new Border { Width = 14, Height = 3, CornerRadius = new CornerRadius(1.5), Background = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        item.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        item.Children.Add(new TextBlock { Text = value is { } v ? $"{v:0.0} °C" : "—", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = brush, VerticalAlignment = VerticalAlignment.Center });
        return item;
    }

    private void Add(UIElement element) => Children.Add(element);

    private void Place(UIElement element, double left, double top)
    {
        SetLeft(element, left);
        SetTop(element, top);
        Children.Add(element);
    }

    private void AddText(string text, double left, double top, double width, TextAlignment align) =>
        Place(new TextBlock { Text = text, Width = width, TextAlignment = align, FontSize = 11, Foreground = Theme.Faint }, left, top);
}
