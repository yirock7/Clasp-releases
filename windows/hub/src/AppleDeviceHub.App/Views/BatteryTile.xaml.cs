using System.ComponentModel;
using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Views;

/// <summary>Compact battery item for the tray flyout.</summary>
public sealed partial class BatteryTile : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(object), typeof(BatteryTile),
        new PropertyMetadata(null, (d, e) => ((BatteryTile)d).OnItemChanged(e.OldValue as BatteryItemViewModel)));

    private BatteryItemViewModel? _subscribed;

    public BatteryTile()
    {
        InitializeComponent();
        BarHost.SizeChanged += (_, e) => UnknownBar.X2 = e.NewSize.Width;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    public BatteryItemViewModel? Item
    {
        get => GetValue(ItemProperty) as BatteryItemViewModel;
        set => SetValue(ItemProperty, value);
    }

    /// <summary>Status line text: adds "Low" so a low level is never shown by colour alone.</summary>
    public static string StatusLine(string? status, bool isLow)
    {
        if (!isLow) return status ?? string.Empty;
        return string.IsNullOrEmpty(status) ? "Low" : $"Low · {status}";
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TilePeer(this);

    private void OnItemChanged(BatteryItemViewModel? oldItem)
    {
        Detach();
        if (IsLoaded) Attach();
        Bindings.Update();
        UpdateState();
    }

    private void Attach()
    {
        var item = Item;
        if (ReferenceEquals(_subscribed, item)) { UpdateState(); return; }
        Detach();
        if (item is not null)
        {
            item.PropertyChanged += OnItemPropertyChanged;
            _subscribed = item;
        }
        UpdateState();
    }

    private void Detach()
    {
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnItemPropertyChanged;
            _subscribed = null;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        var item = Item;
        string state = item is null || !item.IsKnown ? "Unknown"
            : item.IsStale ? "Stale"
            : item.IsLow ? "Low"
            : "Normal";
        VisualStateManager.GoToState(this, state, false);
        AutomationProperties.SetName(this, item?.AutomationName ?? "Battery unknown");
    }

    /// <summary>One text element per tile for screen readers (children are Raw).</summary>
    private sealed partial class TilePeer : FrameworkElementAutomationPeer
    {
        public TilePeer(BatteryTile owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(BatteryTile);
    }
}
