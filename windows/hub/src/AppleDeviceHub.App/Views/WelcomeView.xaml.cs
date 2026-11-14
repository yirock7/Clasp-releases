using System.Numerics;
using AppleDeviceHub.App.Companion;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Artwork;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Pairing;
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
/// The welcome guide: four short pages (what Clasp does, choose or pair your AirPods, find the tray icon, quick settings). The host sets
/// <see cref="Setup"/> and listens for <see cref="Finished"/> (true = Done, false = Skip or Esc). Pages slide with the
/// existing spring tokens; under reduced motion they crossfade for a moment instead. Enter is Next, Esc is Skip, and each
/// page change is announced to Narrator.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The pairing session is disposed when Setup replaces it; the discovery it wraps belongs to the app.")]
public sealed partial class WelcomeView : UserControl
{
    public const int PageCount = 4;

    private static readonly string[] PageNames = { "Welcome to Clasp", "Choose your AirPods", "Find Clasp in the tray", "Quick settings" };

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
    private PairingSessionViewModel? _session;
    private bool _hasDiscovery;
    private readonly Dictionary<string, GlassButton> _pairButtons = new(StringComparer.Ordinal);
    private List<(string Id, string Name)> _pairShown = new();

    public WelcomeView()
    {
        InitializeComponent();
        // Page 2 is hidden until it is shown, so nothing animates until then.
        HeroWaiting.Pause();
        PairingHelp.SetActive(false);
        SyncHeroModel();
        _pages = new FrameworkElement[] { Page1, Page2, Page3, Page4 };
        _dots = new[] { Dot1, Dot2, Dot3, Dot4 };
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
    internal void Setup(HubViewModel viewModel, HubSettings settings, CompanionConnectionViewModel? connection, CompanionHost? companion,
        IPairingDiscovery? pairingDiscovery)
    {
        _viewModel = viewModel;
        _settings = settings;
        SyncHeroModel();
        Detach();
        _connection = connection;
        _companion = companion;
        _helpHadDevices = null;
        PairedDevicePicker.ItemsSource = _connection is null ? null : BindableItems.From(_connection.Devices);
        SetupPairing(pairingDiscovery);
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

    /// <summary>Listens to the connection (and searches for AirPods to pair) only while the guide is showing.</summary>
    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        if (_connection is not null)
        {
            _connection.PropertyChanged += OnConnectionPropertyChanged;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnConnectionPropertyChanged;
        }
        if (_page == 1) SetPage2Active(true);
    }

    /// <summary>Stops listening (the window hid, or the view unloaded), so a hidden guide never touches the selection.</summary>
    internal void Detach()
    {
        bool wasAttached = _attached;
        _attached = false; // before SetPage2Active, so the pairing search stops
        SetPage2Active(false);
        if (!wasAttached || _connection is null) return;
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnConnectionPropertyChanged;
    }

    /// <summary>Starts (or restarts) the guide on <paramref name="page"/> (0 to 3), without a slide.</summary>
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
        if (page == 3) RefreshQuickSettings();

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
        // The search runs only while page 2 shows and the guide is open (never in a hidden window).
        if (active && _attached) _session?.Activate();
        else _session?.Deactivate();
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

    // ----- Page 2: pairing new AirPods in the app -----

    private void SetupPairing(IPairingDiscovery? discovery)
    {
        if (_session is { } old)
        {
            old.PropertyChanged -= OnSessionPropertyChanged;
            old.Candidates.CollectionChanged -= OnSessionCandidatesChanged;
            old.Dispose(); // the discovery belongs to the app and is not disposed here
            _session = null;
        }
        _hasDiscovery = discovery is not null;
        PairingHelp.SetInAppPairing(_hasDiscovery);
        if (_hasDiscovery)
        {
            OpenBluetoothButton.Content = "Pair in Windows Settings instead";
            OpenBluetoothButton.IsFlat = true;
            OpenBluetoothButton.Padding = new Thickness(10, 4, 10, 4);
            OpenBluetoothButton.MinHeight = 32;
        }
        else
        {
            OpenBluetoothButton.Content = "Open Bluetooth settings";
            OpenBluetoothButton.IsFlat = false;
            OpenBluetoothButton.ClearValue(Control.PaddingProperty);
            OpenBluetoothButton.ClearValue(FrameworkElement.MinHeightProperty);
        }

        if (discovery is not null)
        {
            _session = new PairingSessionViewModel(
                discovery,
                action => DispatcherQueue.TryEnqueue(() => action()),
                adoptPairedDevice: _connection is null ? null : AdoptPairedDeviceAsync,
                log: Shell.LocalLog.Write);
            _session.PropertyChanged += OnSessionPropertyChanged;
            _session.Candidates.CollectionChanged += OnSessionCandidatesChanged;
        }
        UpdatePairing();
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdatePairing();
        if (e.PropertyName == nameof(PairingSessionViewModel.State) && _session?.State == PairingState.Paired) UpdateDevices();
    }

    private void OnSessionCandidatesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdatePairing();

    /// <summary>
    /// Shows the session's status line, spinner and the Pair rows. The rows are rebuilt only when the candidates (id or
    /// name) change; otherwise the existing buttons are updated in place so keyboard focus is not lost.
    /// </summary>
    private void UpdatePairing()
    {
        var session = _session;
        PairPanel.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (session is null)
        {
            PairList.Children.Clear();
            _pairButtons.Clear();
            _pairShown.Clear();
            return;
        }

        PairRing.IsActive = session.State is PairingState.Searching or PairingState.Pairing or PairingState.Connecting;
        if (PairStatus.Text != session.StatusText) PairStatus.Text = session.StatusText;

        var wanted = session.Candidates.Select(c => (c.Id, c.Name)).ToList();
        if (!wanted.SequenceEqual(_pairShown)) RebuildPairRows(session, wanted);

        foreach (var (id, button) in _pairButtons)
        {
            bool pending = session.IsBusy && session.PairingCandidateId == id;
            // The button being paired stays enabled (it shows the spinner), so it does not drop keyboard focus.
            button.IsEnabled = session.CanPair || pending;
            button.IsPending = pending;
        }
    }

    private void RebuildPairRows(PairingSessionViewModel session, List<(string Id, string Name)> wanted)
    {
        var focused = _pairButtons.FirstOrDefault(kv => kv.Value.FocusState != FocusState.Unfocused);
        var focusedButton = focused.Value;
        bool hadFocus = focusedButton is not null;
        string? focusedId = focusedButton is not null ? focused.Key : null;
        var focusedState = focusedButton?.FocusState ?? FocusState.Unfocused;
        PairList.Children.Clear();
        _pairButtons.Clear();
        _pairShown = wanted;
        foreach (var candidate in session.Candidates)
        {
            var label = new TextBlock
            {
                Text = candidate.Name,
                Style = (Style)Application.Current.Resources["GlassLabelTextStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.WrapWholeWords,
            };
            var button = new GlassButton { Content = "Pair", Tag = candidate.Id, MinWidth = 72 };
            AutomationProperties.SetName(button, $"Pair {candidate.Name}");
            button.Click += OnPairClick;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(button, 1);
            row.Children.Add(label);
            row.Children.Add(button);
            PairList.Children.Add(row);
            _pairButtons[candidate.Id] = button;
        }
        if (focusedId is not null && _pairButtons.TryGetValue(focusedId, out var again))
        {
            again.Focus(focusedState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic);
        }
        else if (hadFocus)
        {
            // The focused button went away (for example the device paired): hand focus to the next sensible control.
            if (PickerPanel.Visibility == Visibility.Visible) PairedDevicePicker.Focus(FocusState.Programmatic);
            else NextButton.Focus(FocusState.Programmatic);
        }
    }

    private async void OnPairClick(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || (sender as FrameworkElement)?.Tag is not string id) return;
        await session.PairAsync(id);
    }

    /// <summary>
    /// After pairing: refreshes Clasp's paired list until the device shows up with an audio endpoint, selects it, and
    /// connects it when Windows has not already. The caller applies the overall timeout by cancelling the token.
    /// </summary>
    private async Task<bool> AdoptPairedDeviceAsync(PairingCandidate candidate, CancellationToken token)
    {
        var connection = _connection;
        if (connection is null) return false;
        var address = BluetoothAddressParser.TryParse(candidate.Id);
        if (address is null) return false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await connection.RefreshAsync();
            var device = connection.Devices.FirstOrDefault(d =>
                d.HasAvailableAudioEndpoint && BluetoothAddressParser.TryParse(d.Id) == address);
            if (device is not null)
            {
                token.ThrowIfCancellationRequested();
                connection.SelectedDeviceId = device.Id;
                // The picker stays usable meanwhile: if the user picks another device, their choice wins and
                // nothing is connected on their behalf.
                for (int i = 0; i < 10 && !connection.IsConnected; i++)
                {
                    await Task.Delay(500, token);
                    if (!string.Equals(connection.SelectedDeviceId, device.Id, StringComparison.Ordinal)) return false;
                }
                token.ThrowIfCancellationRequested();
                if (!string.Equals(connection.SelectedDeviceId, device.Id, StringComparison.Ordinal)) return false;
                if (!connection.IsConnected && connection.CanConnect) await connection.ConnectAsync();
                // ConnectAsync returns normally when the attempt fails, so report success only when the paired
                // device is still the selected one and is actually connected.
                return connection.IsConnected
                    && string.Equals(connection.SelectedDeviceId, device.Id, StringComparison.Ordinal);
            }
            await Task.Delay(1000, token);
        }
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
        // With in-app pairing the link is always there as a fallback; without it, only when nothing is paired.
        OpenBluetoothButton.Visibility = _hasDiscovery || !hasDevices ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = connection is not null;
        EmptyTitle.Text = _hasDiscovery ? "Put your AirPods in pairing mode" : "Pair your AirPods in Windows first";
        EmptyDetail.Text = connection is null
            ? "Clasp can't check your paired devices right now."
            : _hasDiscovery ? "They'll appear above with a Pair button." : "Once they're paired, they'll show up here.";

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

    // ----- Page 4: quick settings (the same HubSettings as Settings; the demo may change them too, as in Settings) -----

    private void RefreshQuickSettings()
    {
        _updatingToggles = true;
        try
        {
            var settings = _settings;
            bool has = settings is not null;
            LowBatteryBox.IsEnabled = has;
            AutoConnectToggle.IsEnabled = has && settings!.ObserveNearbyAirPods;
            SwitchOutputToggle.IsEnabled = has;
            AutoPlayToggle.IsEnabled = has;
            AutoPauseToggle.IsEnabled = has;
            if (settings is null) return;
            LowBatteryBox.SelectedIndex = settings.LowBatteryThresholdIndex;
            AutoConnectToggle.IsOn = settings.AutoConnectAirPods;
            SwitchOutputToggle.IsOn = settings.SwitchOutputToAirPodsOnConnect;
            AutoPlayToggle.IsOn = settings.AutoPlayOnConnect;
            AutoPauseToggle.IsOn = settings.AutoPauseOnDisconnect;
            AutoConnectHelp.Text = settings.ObserveNearbyAirPods
                ? "Clasp tries once to connect your AirPods when it notices the case opening nearby. That could be someone else's AirPods."
                : "Needs nearby detection (on the previous page).";
        }
        finally { _updatingToggles = false; }
    }

    private void OnLowBatteryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingToggles || _settings is null || LowBatteryBox.SelectedIndex < 0) return;
        _settings.LowBatteryThresholdIndex = LowBatteryBox.SelectedIndex;
    }

    private void OnQuickToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles || _settings is not { } settings) return;
        if (ReferenceEquals(sender, AutoConnectToggle)) settings.AutoConnectAirPods = AutoConnectToggle.IsOn;
        else if (ReferenceEquals(sender, SwitchOutputToggle)) settings.SwitchOutputToAirPodsOnConnect = SwitchOutputToggle.IsOn;
        else if (ReferenceEquals(sender, AutoPlayToggle)) settings.AutoPlayOnConnect = AutoPlayToggle.IsOn;
        else if (ReferenceEquals(sender, AutoPauseToggle)) settings.AutoPauseOnDisconnect = AutoPauseToggle.IsOn;
    }
}
