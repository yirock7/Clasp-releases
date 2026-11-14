using System.Numerics;
using AppleDeviceHub.App.Companion;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Artwork;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// The welcome guide: three short pages (what Clasp does, choose your AirPods, find the tray icon). The host sets
/// <see cref="Setup"/> and listens for <see cref="Finished"/> (true = Done, false = Skip or Esc). Pages slide with the
/// existing spring tokens; under reduced motion they crossfade for a moment instead. Enter is Next, Esc is Skip, and each
/// page change is announced to Narrator.
/// </summary>
public sealed partial class WelcomeView : UserControl
{
    public const int PageCount = 3;

    private static readonly string[] PageNames = { "Welcome to Clasp", "Choose your AirPods", "Find Clasp in the tray" };

    private readonly FrameworkElement[] _pages;
    private readonly FrameworkElement[] _dots;
    private int _page = -1;
    private bool _attached;
    private bool _updatingPicker;
    private bool _updatingToggles;
    private bool? _helpHadDevices;
    private bool _heroPlayed;
    private HubViewModel? _viewModel;
    private HubSettings? _settings;
    private CompanionConnectionViewModel? _connection;
    private CompanionHost? _companion;

    public WelcomeView()
    {
        InitializeComponent();
        // Page 2 is hidden until it is shown, so nothing animates until then.
        HeroWaiting.Pause();
        PairingHelp.SetActive(false);
        SyncHeroModel();
        _pages = new FrameworkElement[] { Page1, Page2, Page3 };
        _dots = new[] { Dot1, Dot2, Dot3 };
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) =>
        {
            Attach();
            if (_page == 0 && !_heroPlayed)
            {
                _heroPlayed = true;
                HeroArt.PlayOpen();
            }
        };
    }

    /// <summary>Done (true) or Skip / Esc (false).</summary>
    public event EventHandler<bool>? Finished;

    public int CurrentPage => Math.Max(_page, 0);

    /// <summary>Hands the guide the same objects Settings uses, so it edits the same settings.</summary>
    internal void Setup(HubViewModel viewModel, HubSettings settings, CompanionConnectionViewModel? connection, CompanionHost? companion)
    {
        _viewModel = viewModel;
        _settings = settings;
        SyncHeroModel();
        Detach();
        _connection = connection;
        _companion = companion;
        _helpHadDevices = null;
        PairedDevicePicker.ItemsSource = _connection is null ? null : BindableItems.From(_connection.Devices);
    }

    /// <summary>
    /// The hero on page 1 draws the user's own model when the hub already knows one that has a per-model drawing, otherwise an
    /// AirPods Pro case. Its lid opens once when the page first shows.
    /// </summary>
    private void SyncHeroModel()
    {
        ushort? known = _viewModel?.ArtworkModelId;
        ushort model = ArtworkCatalog.DrawingKeyFor(known) is not null ? known!.Value : DefaultHeroModel;
        HeroArt.SetModel(model, null);
        HeroArt.Group = AppleModelCatalog.ArtworkOf(model);
    }

    /// <summary>AirPods Pro 2 (USB-C): the model the hero shows until Clasp knows the user's own.</summary>
    private const ushort DefaultHeroModel = 0x2420;

    /// <summary>Listens to the connection only while the guide is showing.</summary>
    private void Attach()
    {
        if (_attached || _connection is null) return;
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnConnectionPropertyChanged;
        _attached = true;
        if (_page == 1) SetPage2Active(true);
    }

    /// <summary>Stops listening (the window hid, or the view unloaded), so a hidden guide never touches the selection.</summary>
    internal void Detach()
    {
        SetPage2Active(false); // also when there is no connection to unhook
        if (!_attached || _connection is null) { _attached = false; return; }
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnConnectionPropertyChanged;
        _attached = false;
        SetPage2Active(false);
    }

    /// <summary>Starts (or restarts) the guide on <paramref name="page"/> (0 to 2), without a slide.</summary>
    public void Restart(int page = 0)
    {
        Attach();
        _page = -1;
        _heroPlayed = false;
        ShowPage(Math.Clamp(page, 0, PageCount - 1), animate: false, announce: false);
    }

    /// <summary>Puts keyboard focus on the primary button, so Enter means Next.</summary>
    public void FocusPrimary() => NextButton.Focus(FocusState.Programmatic);

    // ----- Paging -----

    private void ShowPage(int page, bool animate, bool announce = true)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == _page) return;
        int direction = page >= _page ? 1 : -1;
        bool first = _page < 0;
        _page = page;

        for (int i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = page > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = page < PageCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = page == PageCount - 1 ? "Done" : "Next";
        string step = $"Step {page + 1} of {PageCount}";
        AutomationProperties.SetName(Dots, step);
        UpdateDots();

        if (page == 0 && !_heroPlayed && IsLoaded)
        {
            _heroPlayed = true;
            HeroArt.PlayOpen();
        }
        SetPage2Active(page == 1);
        if (page == 1) RefreshDevices(refreshFromWindows: first || animate);
        if (page == 2) RefreshToggles();

        if (animate && IsLoaded) Transition(_pages[page], direction);
        // A hidden Back button cannot keep focus: hand it to the primary button.
        if (page == 0 && !first) FocusPrimary();
        if (announce) Announce($"{step}: {PageNames[page]}");
    }

    /// <summary>Slide in from the side with the existing spring tokens; reduced motion only fades briefly.</summary>
    private static void Transition(FrameworkElement page, int direction)
    {
        try
        {
            var visual = Motion.EnableTranslation(page);
            visual.StopAnimation("Opacity");
            if (EffectsPolicy.ReduceMotion)
            {
                Motion.StopTranslation(page, Vector3.Zero);
                visual.Opacity = 0f;
                Motion.AnimateOpacity(page, 1f, MotionTokens.ReducedFade);
                return;
            }
            visual.Opacity = 0f;
            Motion.AnimateOpacity(page, 1f, TimeSpan.FromMilliseconds(MotionTokens.Entrance.TotalMilliseconds * 0.7));
            Motion.SpringTranslationFromOffset(page, new Vector3(direction * 36f, 0f, 0f), MotionTokens.Release, MotionTokens.SpringDamping);
        }
        catch (Exception)
        {
            // Motion is cosmetic; the page is already shown.
        }
    }

    private void UpdateDots()
    {
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i].Visibility = i == _page ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Announce(string text)
    {
        try
        {
            if (FrameworkElementAutomationPeer.FromElement(Dots) is not { } peer)
                peer = FrameworkElementAutomationPeer.CreatePeerForElement(Dots);
            peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent, text, "WelcomePageChanged");
        }
        catch (Exception)
        {
            // Accessibility tree not available yet; the headings still name each page.
        }
    }

    // ----- Buttons and keys -----

    /// <summary>Lets a short page sit in the middle of the card instead of hugging the top.</summary>
    private void OnPageScrollerSizeChanged(object sender, SizeChangedEventArgs e)
        => PageHost.MinHeight = Math.Max(0, PageScroller.ViewportHeight > 0 ? PageScroller.ViewportHeight : e.NewSize.Height);

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_page >= PageCount - 1) Finished?.Invoke(this, true);
        else ShowPage(_page + 1, animate: true);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowPage(_page - 1, animate: true);

    private void OnSkipClick(object sender, RoutedEventArgs e) => Finished?.Invoke(this, false);

    private void OnViewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                Finished?.Invoke(this, false);
                break;
            case VirtualKey.Enter:
                // A focused button, picker or link already acts on Enter itself.
                if (e.OriginalSource is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or ComboBox or ComboBoxItem) return;
                e.Handled = true;
                OnNextClick(sender, e);
                break;
        }
    }

    /// <summary>Runs page 2's animations only while that page is showing and the guide is open.</summary>
    private void SetPage2Active(bool active)
    {
        if (active) HeroWaiting.Resume(); else HeroWaiting.Pause();
        PairingHelp.SetActive(active);
    }

    /// <summary>
    /// Tells the hero whether a device has been found: a live hub model (connected or nearby) or the chosen paired
    /// device. The status line is a live region, so Narrator says "Found ..." or "Looking for ...".
    /// </summary>
    private void UpdateDetection()
    {
        var hub = _viewModel;
        bool live = hub is { Connection: AppleDeviceHub.Core.Contracts.ConnectionSummary.Connected or AppleDeviceHub.Core.Contracts.ConnectionSummary.Nearby };
        var found = DeviceDetection.Decide(_connection?.SelectedDeviceName, hub?.ModelName, live);
        HeroWaiting.Update(found);
        string text = found is null ? "Looking for your AirPods" : $"Found {found.Name}";
        if (WaitingStatus.Text != text) WaitingStatus.Text = text;
    }

    // ----- Page 2: the paired-device picker (the same logic as Settings > AirPods) -----

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) UpdateDevices();
        else DispatcherQueue.TryEnqueue(UpdateDevices);
    }

    private async void RefreshDevices(bool refreshFromWindows)
    {
        UpdateDevices();
        if (_connection is null || !refreshFromWindows) return;
        await RefreshFromWindowsAsync();
    }

    private async Task RefreshFromWindowsAsync()
    {
        if (_connection is null) return;
        RefreshButton.IsPending = true;
        try { await _connection.RefreshAsync(); }
        catch (Exception) { /* The status line shows the last complete state. */ }
        finally
        {
            RefreshButton.IsPending = false;
            UpdateDevices();
        }
    }

    private void UpdateDevices()
    {
        var connection = _connection;
        int count = connection?.Devices.Count ?? 0;
        bool hasDevices = count > 0;

        UpdateDetection();
        PickerPanel.Visibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;
        OpenBluetoothButton.Visibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = connection is not null;
        EmptyDetail.Text = connection is null
            ? "Clasp can't check your paired devices right now."
            : "Once they're paired, they'll show up here.";

        // The pairing help opens by itself when nothing is paired, and closes once something is. Between those
        // changes it stays however the user left it.
        if (_helpHadDevices != hasDevices)
        {
            _helpHadDevices = hasDevices;
            PairingHelp.SetExpanded(!hasDevices);
        }
        PairingHelp.SetModel(_viewModel?.ModelName);
        if (connection is null) return;

        // Selection is left to the view model: its AutoSelectSoleAirPods policy picks a sole AirPods on refresh
        // and respects an explicit Clear. The guide only shows the result.
        _updatingPicker = true;
        try { PairedDevicePicker.SelectedItem = connection.SelectedDevice; }
        finally { _updatingPicker = false; }

        PickerStatus.Text = connection switch
        {
            { IsConnected: true, IsCurrentOutput: true } => "Connected · Sound output",
            { IsConnected: true } => "Connected · Sound is going elsewhere",
            _ => connection.StatusText,
        };
    }

    private void OnPairedDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPicker || _connection is null || PairedDevicePicker.SelectedItem is not ConnectionDevice device) return;
        if (!device.HasAvailableAudioEndpoint)
        {
            _updatingPicker = true;
            try { PairedDevicePicker.SelectedItem = _connection.SelectedDevice; }
            finally { _updatingPicker = false; }
            return;
        }
        _connection.SelectedDeviceId = device.Id;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshFromWindowsAsync();

    private void OnOpenBluetoothSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch (Exception) { /* Windows Settings is optional; Refresh still works. */ }
    }

    // ----- Page 3: the same two settings as Settings (Startup is the registry, Nearby detection is HubSettings) -----

    private void RefreshToggles()
    {
        _updatingToggles = true;
        try
        {
            bool demo = _viewModel?.IsDemo == true;
            if (_companion is null || demo)
            {
                RunOnStartupToggle.IsEnabled = false;
                RunOnStartupToggle.IsOn = false;
                SetStartupMessage(demo ? "The sign-in setting isn't available in demo mode." : "");
            }
            else
            {
                RunOnStartupToggle.IsEnabled = true;
                var status = CompanionHost.GetStartupStatus();
                RunOnStartupToggle.IsOn = status is StartupStatus.On or StartupStatus.OffInTaskManager;
                SetStartupMessage(status is { } known && known != StartupStatus.On && known != StartupStatus.Off
                    ? StartupMessages.Describe(known)
                    : status is null ? "Couldn't read the sign-in setting from Windows." : "");
            }

            bool canObserve = _viewModel?.IsBroadcastObservation == true;
            NearbyToggle.IsEnabled = canObserve && _settings is not null;
            NearbyToggle.IsOn = _settings?.ObserveNearbyAirPods == true;
            NearbyHelp.Text = canObserve || _settings is null
                ? "Listens for nearby AirPods to show their battery. It never pairs or connects to anything."
                : "Nearby detection isn't available right now.";
        }
        finally { _updatingToggles = false; }
    }

    private void SetStartupMessage(string text)
    {
        RunOnStartupStatus.Text = text;
        RunOnStartupStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRunOnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles || _companion is null) return;
        var result = CompanionHost.SetRunOnStartup(RunOnStartupToggle.IsOn);
        SetStartupMessage(result.Succeeded ? "" : result.Message);
        if (!result.Succeeded) RefreshToggles(); // show what Windows really has
    }

    private void OnNearbyToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles || _settings is null) return;
        _settings.ObserveNearbyAirPods = NearbyToggle.IsOn;
    }
}
