using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeviceMaster.Core.Curves;

namespace DeviceMaster.Ui;

/// <summary>
/// Temperature → duty graph with draggable points (the headless web dashboard draws the same
/// graph in SVG). Dragging moves a point between its neighbours; on release the curve goes
/// through <see cref="CurveRules.Normalize"/> and <see cref="CurveEdited"/> fires. A live marker
/// shows where the loop is on the curve right now.
/// </summary>
public sealed class CurveEditor : Canvas
{
    private const double PadLeft = 44, PadRight = 24, PadTop = 14, PadBottom = 28;

    private List<CurvePoint> _points = [];
    private readonly (double Min, double Max) _range = (CurveRules.MinTemperature, CurveRules.MaxTemperature);
    private double? _liveTemp;
    private int? _liveDuty;
    private int _dragIndex = -1;

    /// <summary>Fires with the repaired curve when a drag ends.</summary>
    public event Action<List<CurvePoint>>? CurveEdited;

    public CurveEditor()
    {
        Height = 340;
        Background = Brushes.Transparent; // hit-test the whole area while dragging
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => EndDrag();
    }

    public void SetCurve(IReadOnlyList<CurvePoint> points)
    {
        if (_dragIndex >= 0)
        {
            return; // never yank a point out from under the cursor
        }

        _points = points.ToList();
        Redraw();
    }

    /// <summary>Current source temperature and the duty the curve gives for it (shown in Fixed
    /// mode too, for information); null hides the marker.</summary>
    public void SetLive(double? temperatureC, int? duty)
    {
        if (_liveTemp == temperatureC && _liveDuty == duty)
        {
            return;
        }

        _liveTemp = temperatureC;
        _liveDuty = duty;
        if (_dragIndex < 0)
        {
            Redraw();
        }
    }

    // ---------- geometry ----------

    private double PlotWidth => Math.Max(ActualWidth - PadLeft - PadRight, 10);
    private double PlotHeight => Math.Max(ActualHeight - PadTop - PadBottom, 10);

    private double X(double temp) => PadLeft + (temp - _range.Min) / (_range.Max - _range.Min) * PlotWidth;
    private double Y(double duty) => PadTop + (1 - duty / 100.0) * PlotHeight;
    private double TempAt(double x) => _range.Min + (x - PadLeft) / PlotWidth * (_range.Max - _range.Min);
    private double DutyAt(double y) => (1 - (y - PadTop) / PlotHeight) * 100;

    // ---------- drawing ----------

    private void Redraw()
    {
        Children.Clear();
        if (ActualWidth < 40 || _points.Count == 0)
        {
            return;
        }

        // grid: duty every 10%, temperature every 10 °C (the web dashboard draws the same grid)
        for (var duty = 0; duty <= 100; duty += 10)
        {
            Add(new Line { X1 = PadLeft, X2 = PadLeft + PlotWidth, Y1 = Y(duty), Y2 = Y(duty), Stroke = Theme.Line, StrokeThickness = 1, Opacity = duty % 50 == 0 ? 1 : 0.55 });
            AddText($"{duty}%", 0, Y(duty) - 8, 38, TextAlignment.Right);
        }

        const int step = 10;
        for (var t = Math.Ceiling(_range.Min / step) * step; t <= _range.Max; t += step)
        {
            Add(new Line { X1 = X(t), X2 = X(t), Y1 = PadTop, Y2 = PadTop + PlotHeight, Stroke = Theme.Line, StrokeThickness = 1, Opacity = 0.6 });
            AddText($"{t:0}°", X(t) - 20, PadTop + PlotHeight + 6, 40, TextAlignment.Center);
        }

        // curve: flat before the first point and after the last, like FanCurve.EvaluateDuty
        var line = new PointCollection { new(PadLeft, Y(_points[0].DutyPercent)) };
        foreach (var p in _points)
        {
            line.Add(new Point(X(p.TemperatureC), Y(p.DutyPercent)));
        }

        line.Add(new Point(PadLeft + PlotWidth, Y(_points[^1].DutyPercent)));
        var area = new PointCollection(line) { new(PadLeft + PlotWidth, Y(0)), new(PadLeft, Y(0)) };
        Add(new Polygon { Points = area, Fill = Theme.AccentGrad(), Opacity = 0.12 });
        Add(new Polyline { Points = line, Stroke = Theme.AccentGrad(), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round });

        // live marker
        if (_liveTemp is { } live && _liveDuty is { } liveDuty)
        {
            var x = X(Math.Clamp(live, _range.Min, _range.Max));
            Add(new Line { X1 = x, X2 = x, Y1 = PadTop, Y2 = PadTop + PlotHeight, Stroke = Theme.Accent2, StrokeThickness = 1, StrokeDashArray = [3, 3] });
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Theme.Accent2 };
            Place(dot, x - 5, Y(liveDuty) - 5);
            AddText($"now {live:0.0}° → {liveDuty}%", Math.Min(x + 8, PadLeft + PlotWidth - 120), PadTop + 2, 120, TextAlignment.Left, Theme.Accent2);
        }

        // draggable points
        for (var i = 0; i < _points.Count; i++)
        {
            var index = i;
            var p = _points[i];
            var handle = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = Theme.Text,
                Stroke = Theme.Accent,
                StrokeThickness = 3,
                Cursor = Cursors.SizeAll,
                ToolTip = $"{p.TemperatureC:0} °C → {p.DutyPercent}%",
            };
            handle.MouseLeftButtonDown += (_, e) =>
            {
                _dragIndex = index;
                CaptureMouse();
                e.Handled = true;
            };
            Place(handle, X(p.TemperatureC) - 8, Y(p.DutyPercent) - 8);
            if (i == _dragIndex)
            {
                AddText($"{p.TemperatureC:0}° → {p.DutyPercent}%", X(p.TemperatureC) - 50, Y(p.DutyPercent) - 30, 100, TextAlignment.Center, Theme.Text);
            }
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var lo = _dragIndex > 0 ? _points[_dragIndex - 1].TemperatureC + 1 : _range.Min;
        var hi = _dragIndex < _points.Count - 1 ? _points[_dragIndex + 1].TemperatureC - 1 : _range.Max;
        var temp = Math.Clamp(Math.Round(TempAt(pos.X)), lo, Math.Max(lo, hi));
        var duty = (int)Math.Clamp(Math.Round(DutyAt(pos.Y) / 5) * 5, 0, 100);
        _points[_dragIndex] = new CurvePoint(temp, duty);
        Redraw();
    }

    private void EndDrag()
    {
        if (_dragIndex < 0)
        {
            return;
        }

        _dragIndex = -1;
        ReleaseMouseCapture();
        if (CurveRules.Normalize(_points) is { } repaired)
        {
            _points = repaired;
            CurveEdited?.Invoke(repaired.ToList());
        }

        Redraw();
    }

    private void Add(UIElement element) => Children.Add(element);

    private void Place(UIElement element, double left, double top)
    {
        SetLeft(element, left);
        SetTop(element, top);
        Children.Add(element);
    }

    private void AddText(string text, double left, double top, double width, TextAlignment align, Brush? brush = null) =>
        Place(new TextBlock { Text = text, Width = width, TextAlignment = align, FontSize = 11, Foreground = brush ?? Theme.Faint }, left, top);
}
