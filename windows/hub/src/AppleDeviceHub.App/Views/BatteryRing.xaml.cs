using System.ComponentModel;
using System.Diagnostics;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// Determinate battery ring. <see cref="PlayFill"/> sweeps the arc from empty to the level
/// once (case-open moment); later level changes jump. The sweep is a short, self-stopping
/// UI-thread tween (no idle timers); reduced motion shows the final arc immediately.
/// </summary>
public sealed partial class BatteryRing : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(object), typeof(BatteryRing),
        new PropertyMetadata(null, (d, _) => ((BatteryRing)d).OnItemChanged()));

    private const double RingSize = 64;
    private const double RingStroke = 5;
    private static readonly TimeSpan FillDuration = TimeSpan.FromMilliseconds(420);

    private readonly Stopwatch _clock = new();
    private DispatcherQueueTimer? _timer;
    private BatteryItemViewModel? _subscribed;
    private double _shown;      // 0..1 currently drawn
    private double _tweenFrom;
    private bool _tweening;

    public BatteryRing()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) =>
        {
            Detach();
            StopTween();
        };
    }

    public BatteryItemViewModel? Item
    {
        get => GetValue(ItemProperty) as BatteryItemViewModel;
        set => SetValue(ItemProperty, value);
    }

    /// <summary>Sweep the arc from empty to the current level (jumps under reduced motion).</summary>
    public void PlayFill()
    {
        StopTween();
        double target = TargetLevel();
        if (EffectsPolicy.ReduceMotion || target <= 0)
        {
            DrawArc(target);
            return;
        }
        _tweenFrom = 0;
        DrawArc(0);
        _tweening = true;
        _clock.Restart();
        _timer ??= CreateTimer();
        _timer.Start();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RingPeer(this);

    private DispatcherQueueTimer CreateTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => OnTweenTick();
        return timer;
    }

    private void OnTweenTick()
    {
        double t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / FillDuration.TotalMilliseconds, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        double target = TargetLevel();         // retargets if the level changes mid-sweep
        DrawArc(_tweenFrom + (target - _tweenFrom) * eased);
        if (t >= 1) StopTween();
    }

    private void StopTween()
    {
        _tweening = false;
        _timer?.Stop();
        _clock.Reset();
    }

    private double TargetLevel() => Item is { IsKnown: true } item ? Math.Clamp(item.Level01, 0, 1) : 0;

    private void OnItemChanged()
    {
        Detach();
        if (IsLoaded) Attach();
        Bindings.Update();
        UpdateVisuals();
    }

    private void Attach()
    {
        var item = Item;
        if (!ReferenceEquals(_subscribed, item))
        {
            Detach();
            if (item is not null)
            {
                item.PropertyChanged += OnItemPropertyChanged;
                _subscribed = item;
            }
        }
        UpdateVisuals();
    }

    private void Detach()
    {
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnItemPropertyChanged;
            _subscribed = null;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateVisuals();

    private void UpdateVisuals()
    {
        var item = Item;
        string state = item is null || !item.IsKnown ? "Unknown"
            : item.IsStale ? "Stale"
            : item.IsLow ? "Low"
            : "Normal";
        VisualStateManager.GoToState(this, state, false);
        AutomationProperties.SetName(this, item?.AutomationName ?? "Battery unknown");
        if (!_tweening) DrawArc(TargetLevel());
    }

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="level"/> of the circle.</summary>
    private void DrawArc(double level)
    {
        _shown = Math.Clamp(level, 0, 1);
        if (_shown <= 0.001)
        {
            Arc.Data = null;
            return;
        }

        double r = (RingSize - RingStroke) / 2;
        double c = RingSize / 2;
        double sweep = Math.Min(_shown, 0.9999) * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(sweep), c - r * Math.Cos(sweep));

        var segment = new ArcSegment
        {
            Point = end,
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweep > Math.PI,
            RotationAngle = 0,
        };
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(segment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        Arc.Data = geometry;
    }

    private sealed partial class RingPeer : FrameworkElementAutomationPeer
    {
        public RingPeer(BatteryRing owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(BatteryRing);
    }
}
