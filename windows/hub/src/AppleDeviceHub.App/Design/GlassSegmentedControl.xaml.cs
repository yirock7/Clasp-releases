using System.Numerics;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Resources;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// Glass segmented control for listening modes.
/// <para><see cref="SelectedIndex"/> is what the view model displays (the requested mode while
/// pending), <see cref="ConfirmedIndex"/> is what the device last reported. The control never
/// changes its own selection: checking a segment raises <see cref="SelectionRequested"/> and
/// the view model decides; the checks are then re-synchronised to <see cref="SelectedIndex"/>.</para>
/// <para>Keyboard: Tab enters the group once, Left/Right move focus (XY navigation),
/// Space/Enter select. UIA: each segment is a RadioButton named by its label, with
/// ItemStatus "Current mode" / "Requested, waiting for confirmation".</para>
/// </summary>
public sealed partial class GlassSegmentedControl : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(GlassSegmentedControl),
        new PropertyMetadata(null, (d, _) => ((GlassSegmentedControl)d).Rebuild()));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(GlassSegmentedControl),
        new PropertyMetadata(-1, (d, _) => ((GlassSegmentedControl)d).OnSelectionStateChanged(animate: true)));

    public static readonly DependencyProperty ConfirmedIndexProperty = DependencyProperty.Register(
        nameof(ConfirmedIndex), typeof(int), typeof(GlassSegmentedControl),
        new PropertyMetadata(-1, (d, _) => ((GlassSegmentedControl)d).OnSelectionStateChanged(animate: true)));

    public static readonly DependencyProperty IsPendingProperty = DependencyProperty.Register(
        nameof(IsPending), typeof(bool), typeof(GlassSegmentedControl),
        new PropertyMetadata(false, (d, _) => ((GlassSegmentedControl)d).OnSelectionStateChanged(animate: true)));

    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register(
        nameof(HasError), typeof(bool), typeof(GlassSegmentedControl),
        new PropertyMetadata(false, (d, e) => ((GlassSegmentedControl)d).OnHasErrorChanged((bool)e.OldValue, (bool)e.NewValue)));

    public static readonly DependencyProperty IsEnabledForInputProperty = DependencyProperty.Register(
        nameof(IsEnabledForInput), typeof(bool), typeof(GlassSegmentedControl),
        new PropertyMetadata(true, (d, _) => ((GlassSegmentedControl)d).RefreshAvailability()));

    private static readonly TimeSpan ErrorFlash = TimeSpan.FromMilliseconds(650);

    private readonly string _groupName = "GlassSegments_" + Guid.NewGuid().ToString("N");
    private readonly List<RadioButton> _buttons = new();
    private readonly List<TextBlock> _confirmedDots = new();
    private DispatcherQueueTimer? _errorTimer;
    private bool _errorFlashActive;
    private bool _syncing;
    private float _capsuleX;
    private LiquidSlide? _slide;
    private bool _hasCapsuleX;

    public GlassSegmentedControl()
    {
        InitializeComponent();
        ItemsHost.SizeChanged += (_, _) => UpdateCapsule(animate: false);
        Loaded += (_, _) =>
        {
            UpdateCapsule(animate: false);
            UpdateCapsuleState();
        };
        Unloaded += (_, _) => _errorTimer?.Stop();
    }

    /// <summary>Raised when the user picks a segment (pointer, Space or Enter). The view model decides.</summary>
    public event EventHandler<ListeningMode>? SelectionRequested;

    /// <summary>The options, in display order (normally <see cref="HubViewModel.ModeOptions"/>).</summary>
    public IReadOnlyList<ListeningModeOption>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty) as IReadOnlyList<ListeningModeOption>;
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Displayed selection (requested while pending), or -1.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Selection the device confirmed, or -1.</summary>
    public int ConfirmedIndex
    {
        get => (int)GetValue(ConfirmedIndexProperty);
        set => SetValue(ConfirmedIndexProperty, value);
    }

    public bool IsPending
    {
        get => (bool)GetValue(IsPendingProperty);
        set => SetValue(IsPendingProperty, value);
    }

    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    /// <summary>False disables every segment (for example, no control session).</summary>
    public bool IsEnabledForInput
    {
        get => (bool)GetValue(IsEnabledForInputProperty);
        set => SetValue(IsEnabledForInputProperty, value);
    }

    /// <summary>Optional per-option support check; unsupported options are disabled, not hidden.</summary>
    public Func<ListeningModeOption, bool>? IsOptionSupported { get; set; }

    /// <summary>Re-evaluate which segments are enabled.</summary>
    public void RefreshAvailability()
    {
        var items = ItemsSource;
        for (int i = 0; i < _buttons.Count; i++)
        {
            bool supported = items is not null && i < items.Count && (IsOptionSupported?.Invoke(items[i]) ?? true);
            _buttons[i].IsEnabled = IsEnabledForInput && supported;
        }
    }

    /// <summary>Put keyboard focus on the selected segment (or the first enabled one).</summary>
    public bool FocusSelection()
    {
        int i = SelectedIndex;
        if (i >= 0 && i < _buttons.Count && _buttons[i].IsEnabled)
        {
            return _buttons[i].Focus(FocusState.Programmatic);
        }
        foreach (var button in _buttons)
        {
            if (button.IsEnabled) return button.Focus(FocusState.Programmatic);
        }
        return false;
    }

    // ---- Building ---------------------------------------------------------------------

    private void Rebuild()
    {
        foreach (var old in _buttons)
        {
            old.Checked -= OnSegmentChecked;
            old.KeyDown -= OnSegmentKeyDown;
        }
        _buttons.Clear();
        _confirmedDots.Clear();
        ItemsHost.Children.Clear();
        ItemsHost.ColumnDefinitions.Clear();

        var items = ItemsSource;
        if (items is not null)
        {
            var style = Resources["GlassSegmentRadioStyle"] as Style;
            for (int i = 0; i < items.Count; i++)
            {
                var option = items[i];
                ItemsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var glyph = new FontIcon
                {
                    Glyph = option.Glyph,
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var label = new TextBlock
                {
                    Text = option.Label,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MaxLines = 2,
                };
                // Marks the device's confirmed mode while another one is requested.
                // A text bullet inherits the segment foreground, so it follows every theme.
                var dot = new TextBlock
                {
                    Text = "●",
                    FontSize = 7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Visibility = Visibility.Collapsed,
                };
                var stack = new StackPanel
                {
                    Spacing = 3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                stack.Children.Add(glyph);
                stack.Children.Add(label);
                stack.Children.Add(dot);

                var button = new RadioButton
                {
                    GroupName = _groupName,
                    Content = stack,
                };
                if (style is not null) button.Style = style;
                AutomationProperties.SetName(button, option.Label);
                AutomationProperties.SetPositionInSet(button, i + 1);
                AutomationProperties.SetSizeOfSet(button, items.Count);
                Grid.SetColumn(button, i);
                button.Checked += OnSegmentChecked;
                button.KeyDown += OnSegmentKeyDown;

                ItemsHost.Children.Add(button);
                _buttons.Add(button);
                _confirmedDots.Add(dot);
            }
        }

        RefreshAvailability();
        OnSelectionStateChanged(animate: false);
    }

    // ---- Input ------------------------------------------------------------------------

    private void OnSegmentChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton button) return;
        int index = _buttons.IndexOf(button);
        var items = ItemsSource;
        if (index < 0 || items is null || index >= items.Count) return;

        if (index != SelectedIndex)
        {
            SelectionRequested?.Invoke(this, items[index].Mode);
        }
        // The view model may have accepted (SelectedIndex already moved) or ignored the
        // request; either way the checks must mirror SelectedIndex, never the click alone.
        DispatcherQueue.TryEnqueue(SyncChecks);
    }

    private void OnSegmentKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Fully qualified: Windows.System also defines a DispatcherQueueTimer (CS0104 with Microsoft.UI.Dispatching).
        if (e.Key == Windows.System.VirtualKey.Enter && sender is RadioButton { IsEnabled: true } button)
        {
            button.IsChecked = true;
            e.Handled = true;
        }
    }

    // ---- State ------------------------------------------------------------------------

    private void OnSelectionStateChanged(bool animate)
    {
        RefreshAvailability(); // supported modes can change with the device state
        SyncChecks();
        UpdateCapsule(animate);
        UpdateCapsuleState();
        UpdateMarkers();
    }

    private void OnHasErrorChanged(bool oldValue, bool newValue)
    {
        if (newValue && !oldValue)
        {
            // Brief error edge only; the persistent explanation is the status text.
            _errorFlashActive = true;
            _errorTimer ??= CreateErrorTimer();
            _errorTimer.Stop();
            _errorTimer.Start();
        }
        else if (!newValue)
        {
            _errorFlashActive = false;
            _errorTimer?.Stop();
        }
        UpdateCapsuleState();
    }

    private DispatcherQueueTimer CreateErrorTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = ErrorFlash;
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            _errorFlashActive = false;
            UpdateCapsuleState();
        };
        return timer;
    }

    private void SyncChecks()
    {
        _syncing = true;
        try
        {
            int selected = SelectedIndex;
            for (int i = 0; i < _buttons.Count; i++)
            {
                bool shouldCheck = i == selected;
                if (_buttons[i].IsChecked != shouldCheck) _buttons[i].IsChecked = shouldCheck;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Slide the capsule to the selected column. Interrupting retargets (Motion starts from the current value).</summary>
    private void UpdateCapsule(bool animate)
    {
        int count = _buttons.Count;
        int index = SelectedIndex;
        double width = ItemsHost.ActualWidth;
        if (count == 0 || index < 0 || index >= count || width <= 0)
        {
            Capsule.Visibility = Visibility.Collapsed;
            _hasCapsuleX = false;
            return;
        }

        double column = width / count;
        bool wasHidden = Capsule.Visibility == Visibility.Collapsed;
        Capsule.Width = column;
        Capsule.Visibility = Visibility.Visible;
        float left = (float)(column * index);
        bool slide = animate && !wasHidden && _hasCapsuleX;
        // Liquid blob: two edge springs (leading edge quick, trailing edge slower) drive Translation.X and Scale.X.
        _slide ??= new LiquidSlide(Capsule);
        if (slide) _slide.SlideTo(left, (float)column);
        else _slide.Place(left, (float)column);
        _capsuleX = left;
        _hasCapsuleX = true;
    }

    private void UpdateCapsuleState()
    {
        string state = _errorFlashActive && HasError ? "Failed"
            : IsPending ? "Requested"
            : "Confirmed";
        VisualStateManager.GoToState(this, state, false);
    }

    private void UpdateMarkers()
    {
        int selected = SelectedIndex;
        int confirmed = ConfirmedIndex;
        bool split = IsPending && confirmed >= 0 && confirmed != selected;
        for (int i = 0; i < _buttons.Count; i++)
        {
            bool showDot = split && i == confirmed;
            _confirmedDots[i].Visibility = showDot ? Visibility.Visible : Visibility.Collapsed;

            string status = IsPending && i == selected ? Strings.Design_Segments_StatusRequested
                : i == confirmed ? Strings.Design_Segments_StatusCurrent
                : string.Empty;
            AutomationProperties.SetItemStatus(_buttons[i], status);
        }
    }
}
