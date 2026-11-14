using System.Globalization;
using AppleDeviceHub.Core.Battery;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// Battery history: a 24 hours / 7 days switch, a legend, the chart and one summary line. All numbers and
/// positions come from Core (<see cref="BatteryChartModel"/>, <see cref="BatteryChartSummary"/>); this view only
/// draws what it is given, and moves a guide with a tooltip over the readings for the mouse, touch and the keyboard.
/// It redraws only when it is shown, resized, switched or re-themed, never on a timer, and nothing animates.
/// </summary>
public sealed partial class BatteryHistoryView : UserControl
{
    private const double PlotLeft = 38;
    private const double PlotTop = 8;
    private const double PlotHeight = 104;
    private const double RightPad = 10;
    private const double TimeLabelWidth = BatteryChartModel.LabelWidth;
    private const double LineThickness = 2.5;

    private IReadOnlyList<BatterySample> _samples = Array.Empty<BatterySample>();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastHeardAt;
    private BatteryChartRange _range = BatteryChartRange.Day;
    private bool _userPickedRange;
    private bool _syncingRange;
    private BatteryChartModel? _model;
    private int _probeIndex = -1;
    private bool _probeFromPointer;
    private string _baseName = "Battery history chart";

    public BatteryHistoryView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    /// <summary>Shows the history of one device. <paramref name="lastHeardAt"/> keeps the estimate fresh when known.</summary>
    public void Show(IReadOnlyList<BatterySample> samples, DateTimeOffset now, DateTimeOffset? lastHeardAt = null)
    {
        _samples = samples;
        _now = now;
        _lastHeardAt = lastHeardAt;
        if (!_userPickedRange) _range = BatteryChartModel.ChooseRange(samples, now);
        _syncingRange = true;
        DayButton.IsChecked = _range == BatteryChartRange.Day;
        WeekButton.IsChecked = _range == BatteryChartRange.Week;
        _syncingRange = false;

        bool any = samples.Count > 0;
        SummaryText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = BatteryChartSummary.Text(samples, now, lastHeardAt);
        var wear = BatteryWearReport.Build(samples, now);
        WearText.Text = wear.Text;
        WearText.Visibility = wear.Kind == WearReportKind.Comparable ? Visibility.Visible : Visibility.Collapsed;
        Redraw();
    }

    private void OnRangeChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingRange) return;
        _userPickedRange = true;
        _range = ReferenceEquals(sender, WeekButton) ? BatteryChartRange.Week : BatteryChartRange.Day;
        Redraw();
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(220, Math.Floor(e.NewSize.Width));
        if (Math.Abs(Surface.Width - width) < 1) return;
        Surface.Width = width;
        Redraw();
    }

    // ---------------------------------------------------------------- drawing

    private void Redraw()
    {
        double width = Surface.Width;
        double plotWidth = Math.Max(60, width - PlotLeft - RightPad);
        _model = BatteryChartModel.Build(_samples, _now, _range, plotWidth, PlotHeight, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        HideProbe();

        // Gridlines and percent labels.
        Line[] grid = { GridTop, GridMiddle, GridBottom };
        TextBlock[] percentLabels = { Label100, Label50, Label0 };
        for (int i = 0; i < 3; i++)
        {
            double y = PlotTop + _model.Gridlines[i].Y;
            grid[i].X1 = PlotLeft;
            grid[i].X2 = PlotLeft + plotWidth;
            grid[i].Y1 = grid[i].Y2 = y;
            Canvas.SetLeft(percentLabels[i], 0);
            Canvas.SetTop(percentLabels[i], y - 8);
        }

        DrawBands(_model);
        DrawVerticalLines(_model);
        DrawSeries(_model);
        DrawTimeLabels(_model, width);

        ChargingChip.Visibility = _model.Charging.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string rangeName = _range == BatteryChartRange.Week ? "last 7 days" : "last 24 hours";
        if (_samples.Count == 0)
        {
            EmptyText.Text = "No battery history yet";
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (!_model.HasData)
        {
            EmptyText.Text = _range == BatteryChartRange.Week ? "No readings in the last 7 days" : "No readings in the last 24 hours";
            EmptyText.Visibility = Visibility.Visible;
        }
        else EmptyText.Visibility = Visibility.Collapsed;

        _baseName = _samples.Count == 0
            ? "Battery history chart, no readings yet"
            : $"Battery history chart, {rangeName}. {SummaryText.Text}. {(_model.IsCompressed ? "Long stretches without readings are shortened. " : "")}Use the left and right arrow keys to move between readings.";
        AutomationProperties.SetName(ChartFocus, _baseName);
    }

    private void DrawBands(BatteryChartModel model)
    {
        BandLayer.Children.Clear();
        foreach (var band in model.Charging)
        {
            if (band.Instant)
            {
                // One charging reading with nothing known around it: a short cap on the top edge, not a full-height band.
                var cap = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 8, Height = 3, Fill = LegendCharging.BorderBrush, IsHitTestVisible = false };
                Canvas.SetLeft(cap, PlotLeft + Math.Clamp(band.X1 - 4, 0, Math.Max(0, model.Width - 8)));
                Canvas.SetTop(cap, PlotTop);
                BandLayer.Children.Add(cap);
                continue;
            }
            var fill = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = Math.Max(1, band.X2 - band.X1), Height = PlotHeight, Fill = LegendCharging.Background, IsHitTestVisible = false };
            Canvas.SetLeft(fill, PlotLeft + band.X1);
            Canvas.SetTop(fill, PlotTop);
            BandLayer.Children.Add(fill);
            var edge = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = fill.Width, Height = 2, Fill = LegendCharging.BorderBrush, IsHitTestVisible = false };
            Canvas.SetLeft(edge, PlotLeft + band.X1);
            Canvas.SetTop(edge, PlotTop);
            BandLayer.Children.Add(edge);
        }
    }

    private void DrawVerticalLines(BatteryChartModel model)
    {
        LineLayer.Children.Clear();
        // A stretch with no readings that was shortened: a faint strip with a double slash, so it never reads as data.
        foreach (var gap in model.Breaks)
        {
            double mid = PlotTop + PlotHeight / 2, cx = PlotLeft + (gap.X1 + gap.X2) / 2;
            var strip = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = Math.Max(1, gap.X2 - gap.X1), Height = PlotHeight, Fill = GridTop.Stroke, Opacity = 0.16, IsHitTestVisible = false,
            };
            Canvas.SetLeft(strip, PlotLeft + gap.X1);
            Canvas.SetTop(strip, PlotTop);
            LineLayer.Children.Add(strip);
            foreach (double dx in new[] { -2.0, 2.0 })
                LineLayer.Children.Add(new Line
                {
                    X1 = cx + dx - 2, X2 = cx + dx + 2, Y1 = mid + 5, Y2 = mid - 5,
                    Stroke = GridTop.Stroke, StrokeThickness = 1.2, Opacity = 0.9, IsHitTestVisible = false,
                });
        }
        foreach (double x in model.Axis.Lines)
        {
            var line = new Line
            {
                X1 = PlotLeft + x, X2 = PlotLeft + x, Y1 = PlotTop, Y2 = PlotTop + PlotHeight,
                Stroke = GridTop.Stroke, StrokeThickness = 1, Opacity = 0.55, IsHitTestVisible = false,
            };
            LineLayer.Children.Add(line);
        }
    }

    private void DrawSeries(BatteryChartModel model)
    {
        SeriesLayer.Children.Clear();
        // Case first, so the earbuds sit on top. Right is dashed over left, so equal levels show both colours.
        DrawSeries(model.Case, LegendCase.Stroke, dash: new[] { 0.01, 1.6 }, round: true);
        DrawSeries(model.Left, LegendLeft.Stroke, dash: null, round: false);
        DrawSeries(model.Right, LegendRight.Stroke, dash: new[] { 2.0, 1.2 }, round: false);
    }

    private void DrawSeries(IReadOnlyList<ChartSegment> segments, Brush brush, double[]? dash, bool round)
    {
        foreach (var segment in segments)
        {
            if (segment.IsDot)
            {
                var (x, y) = segment.Points[0];
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = brush, IsHitTestVisible = false };
                Canvas.SetLeft(dot, PlotLeft + x - 3);
                Canvas.SetTop(dot, PlotTop + y - 3);
                SeriesLayer.Children.Add(dot);
                continue;
            }
            var points = new PointCollection();
            foreach (var (x, y) in segment.Points) points.Add(new Windows.Foundation.Point(PlotLeft + x, PlotTop + y));
            var line = new Polyline
            {
                Points = points, Stroke = brush, StrokeThickness = LineThickness, IsHitTestVisible = false,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            if (dash is not null)
            {
                var array = new DoubleCollection();
                foreach (double d in dash) array.Add(d);
                line.StrokeDashArray = array;
                if (round) line.StrokeDashCap = PenLineCap.Round;
            }
            SeriesLayer.Children.Add(line);
        }
    }

    private void DrawTimeLabels(BatteryChartModel model, double width)
    {
        LabelLayer.Children.Clear();
        var style = (Style)Application.Current.Resources["GlassCaptionTextStyle"];
        foreach (var tick in model.Axis.Labels)
        {
            var text = new TextBlock
            {
                Text = tick.Label, Style = style, Width = TimeLabelWidth, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap, IsHitTestVisible = false,
            };
            double left = Math.Clamp(PlotLeft + tick.X - TimeLabelWidth / 2, 0, Math.Max(0, width - TimeLabelWidth));
            Canvas.SetLeft(text, left);
            Canvas.SetTop(text, PlotTop + PlotHeight + 10);
            LabelLayer.Children.Add(text);
        }
    }

    // ---------------------------------------------------------------- readout (guide, dots, tooltip)

    private void ShowProbe(int index, bool announce)
    {
        if (_model is not { } model || index < 0 || index >= model.Probes.Count) { HideProbe(); return; }
        var probe = model.Probes[index];
        double x = PlotLeft + probe.X;
        bool changed = index != _probeIndex;
        _probeIndex = index;

        Guide.X1 = Guide.X2 = x;
        Guide.Y1 = PlotTop - 2;
        Guide.Y2 = PlotTop + PlotHeight;
        Guide.Visibility = Visibility.Visible;
        PlaceDot(DotLeft, x, probe.Left);
        PlaceDot(DotRight, x, probe.Right);
        PlaceDot(DotCase, x, probe.Case);

        string text = BatteryChartModel.Describe(probe, _now, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        if (changed || Tip.Visibility != Visibility.Visible)
        {
            TipText.Text = text;
            Tip.MaxWidth = Math.Max(120, Surface.Width - 8);
            Tip.Visibility = Visibility.Visible;
            Tip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double tipWidth = Tip.DesiredSize.Width;
            double left = x + 10;
            if (left + tipWidth > Surface.Width - 2) left = x - 10 - tipWidth;
            Canvas.SetLeft(Tip, Math.Clamp(left, 2, Math.Max(2, Surface.Width - tipWidth - 2)));
            Canvas.SetTop(Tip, PlotTop + 4);
            AutomationProperties.SetName(ChartFocus, text);
            if (announce)
            {
                Announcer.Text = text;
                ChartFocus.Announce(text);
            }
        }
    }

    private static void PlaceDot(Ellipse dot, double x, int? level)
    {
        if (level is not int value) { dot.Visibility = Visibility.Collapsed; return; }
        Canvas.SetLeft(dot, x - dot.Width / 2);
        Canvas.SetTop(dot, PlotTop + PlotHeight * (1 - Math.Clamp(value, 0, 100) / 100.0) - dot.Height / 2);
        dot.Visibility = Visibility.Visible;
    }

    private void HideProbe()
    {
        _probeIndex = -1;
        _probeFromPointer = false;
        Guide.Visibility = Visibility.Collapsed;
        DotLeft.Visibility = DotRight.Visibility = DotCase.Visibility = Visibility.Collapsed;
        Tip.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(ChartFocus, _baseName);
    }

    // ---------------------------------------------------------------- pointer (mouse hover, touch and pen drag)

    private void ShowAtPointer(PointerRoutedEventArgs e)
    {
        if (_model is null) return;
        double x = e.GetCurrentPoint(Surface).Position.X - PlotLeft;
        int index = _model.NearestProbe(x);
        _probeFromPointer = true;
        ShowProbe(index, announce: false);
        _probeFromPointer = index >= 0;
    }

    private void OnChartPointerMoved(object sender, PointerRoutedEventArgs e) => ShowAtPointer(e);

    private void OnChartPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) ChartFocus.CapturePointer(e.Pointer);
        ChartFocus.Focus(FocusState.Pointer);
        ShowAtPointer(e);
    }

    private void OnChartPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        ChartFocus.ReleasePointerCapture(e.Pointer);
        if (_probeFromPointer) HideProbe();
    }

    private void OnChartPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // A captured touch drag keeps reporting; only a plain hover leaving the chart hides the readout.
        if (_probeFromPointer && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) HideProbe();
    }

    // ---------------------------------------------------------------- keyboard

    private void OnChartGotFocus(object sender, RoutedEventArgs e)
    {
        if (ChartFocus.FocusState == FocusState.Keyboard && _model is { Probes.Count: > 0 } model)
            ShowProbe(model.Probes.Count - 1, announce: true);
    }

    private void OnChartLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_probeFromPointer) HideProbe();
    }

    private void OnChartKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_model is not { } model) return;
        int count = model.Probes.Count;
        int current = _probeIndex;
        int target;
        switch (e.Key)
        {
            case VirtualKey.Left: target = current < 0 ? count - 1 : Math.Max(0, current - 1); break;
            case VirtualKey.Right: target = current < 0 ? count - 1 : Math.Min(count - 1, current + 1); break;
            case VirtualKey.Home: target = 0; break;
            case VirtualKey.End: target = count - 1; break;
            case VirtualKey.Escape:
                if (current < 0) return;
                HideProbe();
                e.Handled = true;
                return;
            default: return;
        }
        e.Handled = true;
        if (count == 0 || target == current) return;
        _probeFromPointer = false;
        ShowProbe(target, announce: true);
    }
}
