using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AppleDeviceHub.App.Audio;
using AppleDeviceHub.App.Companion;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Artwork;
using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Commands;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Resources;
using AppleDeviceHub.Core.Startup;
using AppleDeviceHub.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AppleDeviceHub.App.Views;

/// <summary>A display label paired with the exact colourway id parsed from an artwork document.</summary>
public sealed class ArtworkColorwayOption(string? id, string name)
{
    // WinUI's generated XAML metadata requires public setters even though these options are immutable after creation.
    public string? Id { get; set; } = id;
    public string Name { get; set; } = name;
}

/// <summary>
/// Settings page. Host contract: set <see cref="ViewModel"/> and <see cref="Settings"/>.
/// Appearance toggles write <see cref="HubSettings"/> only; applying them (theme,
/// EffectsPolicy.SetOverrides, saving) is the host's job.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private HubViewModel? _viewModel;
    private HubSettings? _settings;
    private bool _effectsSubscribed;
    private bool _audioInputReady;
    private AudioOutputViewModel? _audio;
    private PlaybackViewModel? _playback;
    private CompanionConnectionViewModel? _connection;
    private bool _isRefreshingPairedDevices;
    private readonly ObservableCollection<ArtworkColorwayOption> _artworkColorwayChoices = [];
    private bool _projectingArtworkColorway;
    private bool? _pairingHelpHadDevices;
    private DispatcherQueueTimer? _volumeDebounceTimer;
    private AudioOutputViewModel? _pendingVolumeAudio;
    private string? _pendingVolumeEndpointId;
    private float? _pendingVolumeScalar;
    private long _pendingVolumeTargetGeneration;
    private string? _trackedAudioTargetId;
    private long _audioTargetGeneration;
    private CompanionHost? _companion;
    private UpdateViewModel? _updates;
    private bool _startupRefreshing;
    private bool _hotkeyRowsBuilt;
    private bool _phoneListUpdating;
    private string? _phoneNote;
    private IReadOnlyList<PhoneStreamingCandidate> _phones = Array.Empty<PhoneStreamingCandidate>();
    private readonly Dictionary<ClaspCommand, (TextBox Box, TextBlock Message)> _hotkeyControls = new();

    /// <summary>The companion features (connection actions, Fix connection, shortcuts, startup, phone audio...).</summary>
    internal CompanionHost? Companion
    {
        get => _companion;
        set
        {
            if (ReferenceEquals(_companion, value)) return;
            if (_companion is not null) _companion.Status.PropertyChanged -= OnCompanionStatusChanged;
            _companion = value;
            if (_companion is not null) _companion.Status.PropertyChanged += OnCompanionStatusChanged;
            Bindings.Update();
            if (IsLoaded) RefreshCompanionPage();
        }
    }

    /// <summary>The status lines the companion sections bind to.</summary>
    internal CompanionStatus? CompanionState => _companion?.Status;

    /// <summary>The output chosen for "both AirPods out of your ears", if it is in the output list.</summary>
    public AudioOutputChoice? SelectedEarOutput
        => Settings is { EarRemovedOutputEndpointId.Length: > 0 } settings
            ? Audio?.Outputs.FirstOrDefault(output => string.Equals(output.Id, settings.EarRemovedOutputEndpointId, StringComparison.OrdinalIgnoreCase))
            : null;

    public static string PercentText(double value)
        => string.Format(CultureInfo.CurrentCulture, Strings.Settings_Format_Percent, Math.Round(value).ToString("0", CultureInfo.CurrentCulture));

    public static string HandsfreeDescription(int modeIndex) => modeIndex switch
    {
        1 => Strings.Settings_Handsfree_Description_AlwaysOff,
        2 => Strings.Settings_Handsfree_Description_Smart,
        _ => Strings.Settings_Handsfree_Description_AlwaysOn,
    };

    /// <summary>Every clasp:// link and what it does, for copying into Stream Deck or PowerToys.</summary>
    public static string LinkListText => string.Join(
        "\n",
        Enum.GetValues<ClaspCommand>().Select(command => $"{ClaspCommandText.Link(command)}  \u2013  {ClaspCommandText.DisplayName(command)}"));

    /// <summary>A switch can always be turned off; it can only be turned on where Windows offers the feature.</summary>
    public static bool CanToggle(bool supported, bool isOn) => supported || isOn;

    public static string IgnoredRulesText(string? rulesText)
    {
        PerAppRuleText.Parse(rulesText, out var rejected);
        return rejected.Count switch
        {
            0 => "",
            1 => Strings.Settings_PerApp_IgnoredRules_One,
            var count => string.Format(CultureInfo.CurrentCulture, Strings.Settings_PerApp_IgnoredRules_Many, count),
        };
    }

    public static Visibility IgnoredRulesVisibility(string? rulesText)
        => IgnoredRulesText(rulesText).Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public AudioOutputViewModel? Audio
    {
        get => _audio;
        set
        {
            if (ReferenceEquals(_audio, value)) return;
            if (_audio is not null) _audio.PropertyChanged -= OnAudioPropertyChanged;
            CancelPendingVolumeChange();
            _audioTargetGeneration++;
            _audio = value;
            _trackedAudioTargetId = _audio?.SelectedTarget?.Id;
            if (_audio is not null) _audio.PropertyChanged += OnAudioPropertyChanged;
            Bindings.Update();
        }
    }

    /// <summary>Update checks and install; null hides nothing but disables the Updates controls.</summary>
    public UpdateViewModel? Updates
    {
        get => _updates;
        set
        {
            if (ReferenceEquals(_updates, value)) return;
            if (_updates is not null) _updates.PropertyChanged -= OnUpdatesPropertyChanged;
            _updates = value;
            if (_updates is not null) _updates.PropertyChanged += OnUpdatesPropertyChanged;
            Bindings.Update();
        }
    }

    private void OnUpdatesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Bindings.Update();
        else DispatcherQueue.TryEnqueue(Bindings.Update);
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (Updates is not { } updates) return;
        try { await updates.CheckNowAsync(); }
        catch (Exception) { /* StatusText reports the outcome. */ }
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (Updates is not { } updates) return;
        try { await updates.InstallAsync(); }
        catch (Exception) { /* StatusText reports the outcome. */ }
    }

    /// <summary>About > Show welcome guide. The host opens the guide.</summary>
    public event EventHandler? WelcomeRequested;

    private void OnShowWelcomeClick(object sender, RoutedEventArgs e) => WelcomeRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>About > Open-source licenses: Clasp's own licence plus the notices for NTPods and the shipped dependencies.</summary>
    private async void OnOpenSourceLicensesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var panel = new StackPanel { Spacing = 8 };
            void Heading(string text) => panel.Children.Add(new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.Resources["GlassLabelTextStyle"],
                Margin = new Thickness(0, 8, 0, 0),
            });
            void Line(string text) => panel.Children.Add(new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.Resources["GlassBodyTextStyle"],
                TextWrapping = TextWrapping.WrapWholeWords,
                IsTextSelectionEnabled = true,
            });
            void Link(string text, string url) => panel.Children.Add(new HyperlinkButton
            {
                Content = text,
                NavigateUri = new Uri(url),
                Padding = new Thickness(0),
            });

            Heading("Clasp");
            Line(Strings.Settings_Licenses_Line_ClaspLicense);
            Link("gnu.org/licenses/gpl-3.0", "https://www.gnu.org/licenses/gpl-3.0.html");
            Line(Strings.Settings_Licenses_Line_Source);
            Line("© 2026 yirock7");

            Heading(Strings.Settings_Licenses_Heading_ThirdParty);
            Line(Strings.Settings_Licenses_Line_NtPods);
            Link("github.com/arctumn/ntpods", "https://github.com/arctumn/ntpods");
            Line(Strings.Settings_Licenses_Line_WindowsAppSdk);
            Link("github.com/microsoft/WindowsAppSDK", "https://github.com/microsoft/WindowsAppSDK");
            Line(Strings.Settings_Licenses_Line_DotNetRuntime);
            Link("github.com/dotnet/runtime", "https://github.com/dotnet/runtime");
            Line(Strings.Settings_Licenses_Line_WebView2);
            Link("aka.ms/webview2", "https://aka.ms/webview2");

            var dialog = new ContentDialog
            {
                Title = Strings.Settings_Licenses_Title,
                Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
                CloseButtonText = Strings.Settings_Licenses_Close,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // A dialog is already open or the window is closing; nothing to report.
        }
    }

    private void OnReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        var url = Updates?.AvailableRelease?.PageUrl ?? ReleaseFeed.ReleasesPageUrl;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception) { /* Opening the browser is optional. */ }
    }

    public PlaybackViewModel? Playback
    {
        get => _playback;
        set { if (ReferenceEquals(_playback, value)) return; _playback = value; Bindings.Update(); }
    }

    public CompanionConnectionViewModel? Connection
    {
        get => _connection;
        set
        {
            if (ReferenceEquals(_connection, value)) return;
            if (_connection is not null) _connection.PropertyChanged -= OnConnectionPropertyChanged;
            _connection = value;
            if (_connection is not null) _connection.PropertyChanged += OnConnectionPropertyChanged;
            Bindings.Update();
            UpdatePairingHelp();
        }
    }

    public IEnumerable<ConnectionDevice> PairedDevices => Connection is { } connection
        ? connection.Devices
        : Array.Empty<ConnectionDevice>();
    public ConnectionDevice? SelectedPairedDevice => PairedDevices.FirstOrDefault(device => device.Id == Connection?.SelectedDeviceId);
    public string PairedDeviceStatusText => Connection switch
    {
        { IsConnected: true, IsCurrentOutput: true } => Strings.Settings_Paired_Status_ConnectedOutput,
        { IsConnected: true } => Strings.Settings_Paired_Status_ConnectedElsewhere,
        { } connection => connection.StatusText,
        _ => Strings.Settings_Paired_Status_Unknown,
    };
    public string PairedDeviceDetailText => Connection switch
    {
        { DetailText: not null } connection => connection.DetailText,
        { IsConnected: true } => "",
        _ => Strings.Settings_Paired_Detail_Unreadable,
    };
    public bool HasConnectionController => Connection is not null;
    public bool CanRefreshPairedDevices => Connection is not null;
    public bool PairedConnectionIsRefreshing => _isRefreshingPairedDevices;
    public bool CanObserveNearbyAirPods => ViewModel?.IsBroadcastObservation == true;
    public bool CanAutoConnectNearby => CanObserveNearbyAirPods && Settings?.ObserveNearbyAirPods == true;
    public string NearbyObservationHelpText => CanObserveNearbyAirPods
        ? Strings.Settings_Nearby_Help_Available
        : Strings.Settings_Nearby_Help_Unavailable;

    public bool IsAudioAvailable => Audio is not null;
    public bool CanUseAudioTarget => Audio?.SelectedTarget is { IsAvailable: true };
    public bool CanUseAudioFallback => Audio?.SelectedFallback is { IsAvailable: true };
    public bool HasAudioFallback => Audio?.SelectedFallback is not null;
    public bool HasSelectedOutputVolume => DisplayedVolumeScalar is not null;
    public double OutputVolumePercent => DisplayedVolumeScalar is float value ? value * 100d : 0d;
    public string OutputVolumePercentText => DisplayedVolumeScalar is float value ? PercentText(value * 100) : "";
    public bool CanUsePlaybackControls => ViewModel?.IsDemo != true;
    private float? DisplayedVolumeScalar => PendingVolumeScalarForSelectedTarget ?? Audio?.SelectedOutputVolume;

    private float? PendingVolumeScalarForSelectedTarget
    {
        get
        {
            var audio = Audio;
            if (_pendingVolumeAudio is null || !ReferenceEquals(_pendingVolumeAudio, audio)
                || _pendingVolumeEndpointId is not { } endpointId
                || _pendingVolumeTargetGeneration != _audioTargetGeneration
                || audio?.SelectedTarget is not { } target
                || !string.Equals(target.Id, endpointId, StringComparison.Ordinal)) return null;
            return _pendingVolumeScalar;
        }
    }

    public string AudioAvailabilityText => ViewModel?.IsDemo == true
        ? Strings.Settings_Audio_Unavailable_Demo
        : Strings.Settings_Audio_Unavailable_Unreachable;

    private void OnAudioTargetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: AudioOutputChoice output } && Audio is { } audio)
        {
            audio.SelectTarget(output.Id);
            TrackAudioTargetChange();
        }
        Bindings.Update();
    }

    private void OnAudioFallbackSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: AudioOutputChoice output }) Audio?.SelectFallback(output.Id);
        Bindings.Update();
    }

    private void OnRefreshAudioClick(object sender, RoutedEventArgs e) => Audio?.Refresh();

    private void OnUseAudioTargetClick(object sender, RoutedEventArgs e)
    {
        if (Audio?.SelectedTarget is { IsAvailable: true } target) Audio.UseOutput(target.Id);
    }

    private void OnUseAudioFallbackClick(object sender, RoutedEventArgs e)
    {
        if (Audio?.SelectedFallback is { IsAvailable: true } fallback) Audio.UseOutput(fallback.Id);
    }

    private void OnClearAudioFallbackClick(object sender, RoutedEventArgs e) => Audio?.SelectFallback("");

    private void OnOutputVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var audio = Audio;
        if (!_audioInputReady || audio is null || audio.SelectedTarget is not { IsAvailable: true } target
            || audio.SelectedOutputVolume is not float current) return;
        var scalar = (float)(e.NewValue / 100d);
        if (Math.Abs(current - scalar) < 0.005f)
        {
            CancelPendingVolumeChange(); // The slider returned to the current output level.
            Bindings.Update();
            return;
        }

        _pendingVolumeAudio = audio;
        _pendingVolumeEndpointId = target.Id;
        _pendingVolumeScalar = scalar;
        _pendingVolumeTargetGeneration = _audioTargetGeneration;
        _volumeDebounceTimer ??= CreateVolumeDebounceTimer();
        _volumeDebounceTimer.Stop();
        _volumeDebounceTimer.Start();
        Bindings.Update(); // Preserve the in-flight scalar across unrelated audio/status updates.
    }

    private DispatcherQueueTimer CreateVolumeDebounceTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.IsRepeating = false;
        timer.Tick += OnVolumeDebounceElapsed;
        return timer;
    }

    private void OnVolumeDebounceElapsed(DispatcherQueueTimer sender, object args)
    {
        var audio = _pendingVolumeAudio;
        var endpointId = _pendingVolumeEndpointId;
        var scalar = _pendingVolumeScalar;
        var targetGeneration = _pendingVolumeTargetGeneration;
        if (!_audioInputReady || !IsLoaded || XamlRoot is not { IsHostVisible: true }
            || audio is null || endpointId is null || scalar is null
            || !ReferenceEquals(audio, Audio) || targetGeneration != _audioTargetGeneration
            || audio.SelectedTarget is not { IsAvailable: true } target
            || !string.Equals(target.Id, endpointId, StringComparison.Ordinal)
            || audio.SelectedOutputVolume is not float current
            || Math.Abs(current - scalar.Value) < 0.005f)
        {
            CancelPendingVolumeChange();
            Bindings.Update();
            return;
        }

        CancelPendingVolumeChange();
        audio.SetOutputVolume(endpointId, scalar.Value);
        Bindings.Update();
    }

    private void TrackAudioTargetChange()
    {
        var currentTargetId = Audio?.SelectedTarget?.Id;
        if (string.Equals(currentTargetId, _trackedAudioTargetId, StringComparison.Ordinal)) return;
        _trackedAudioTargetId = currentTargetId;
        _audioTargetGeneration++;
        CancelPendingVolumeChange();
    }

    private void CancelPendingVolumeChange()
    {
        _volumeDebounceTimer?.Stop();
        _pendingVolumeAudio = null;
        _pendingVolumeEndpointId = null;
        _pendingVolumeScalar = null;
        _pendingVolumeTargetGeneration = 0;
    }

    private void OnAudioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Update()
        {
            TrackAudioTargetChange();
            Bindings.Update();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        HookNavFollowsScroll();
    }

    public HubViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value)) return;
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelArtworkChanged;
            _viewModel = value;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelArtworkChanged;
            Bindings.Update(); // required after load; harmless before it
            RefreshArtworkColorways();
        }
    }

    public HubSettings? Settings
    {
        get => _settings;
        set
        {
            if (ReferenceEquals(_settings, value)) return;
            if (_settings is not null) _settings.PropertyChanged -= OnSettingsArtworkChanged;
            _settings = value;
            if (_settings is not null) _settings.PropertyChanged += OnSettingsArtworkChanged;
            Bindings.Update();
            RefreshArtworkColorways();
        }
    }

    public ObservableCollection<ArtworkColorwayOption> ArtworkColorwayChoices => _artworkColorwayChoices;

    // Same id choice as the flyout: the paired Windows device when one is selected, else the transport's. A saved colour is also
    // stored model-wide, so the two id spaces still find it.
    private string? CurrentArtworkDeviceId => _viewModel?.IsDemo == true
        ? _viewModel.ArtworkDeviceId
        : !string.IsNullOrWhiteSpace(Connection?.SelectedDeviceId) ? Connection.SelectedDeviceId : _viewModel?.ArtworkDeviceId;

    private void OnViewModelArtworkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(HubViewModel.ArtworkModelId) or nameof(HubViewModel.ArtworkDeviceId))) return;
        void Update()
        {
            if (ReferenceEquals(sender, _viewModel)) RefreshArtworkColorways();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void OnSettingsArtworkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HubSettings.ArtworkColorways) || _projectingArtworkColorway) return;
        void Update()
        {
            if (ReferenceEquals(sender, _settings)) RefreshArtworkColorways();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    /// <summary>Lists the current model's colourways; the row shows only when its drawing has two or more.</summary>
    private void RefreshArtworkColorways()
    {
        var modelId = _viewModel?.ArtworkModelId;
        var key = ArtworkCatalog.DrawingKeyFor(modelId);
        var ways = (key is null ? null : ArtworkStore.Get(key))?.Colorways;
        bool offer = ways is { Count: >= 2 };
        _projectingArtworkColorway = true;
        try
        {
            _artworkColorwayChoices.Clear();
            if (offer)
            {
                foreach (var way in ways!) _artworkColorwayChoices.Add(new ArtworkColorwayOption(way.Id, way.DisplayName));
                var saved = _settings?.GetArtworkColorway(CurrentArtworkDeviceId, modelId);
                ArtworkColorwayPicker.SelectedItem = _artworkColorwayChoices.FirstOrDefault(o => string.Equals(o.Id, saved, StringComparison.Ordinal))
                    ?? _artworkColorwayChoices[0];
            }
            else
            {
                ArtworkColorwayPicker.SelectedItem = null;
            }
        }
        finally { _projectingArtworkColorway = false; }
        ArtworkColorwayPicker.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnArtworkColorwaySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_projectingArtworkColorway || ArtworkColorwayPicker.SelectedItem is not ArtworkColorwayOption option
            || _viewModel?.ArtworkModelId is not ushort modelId || _settings is null) return;
        _projectingArtworkColorway = true;
        try { _settings.SetArtworkColorway(CurrentArtworkDeviceId, modelId, option.Id); }
        finally { _projectingArtworkColorway = false; }
    }

    public static string SecondsText(int seconds) => seconds == 1
        ? Strings.Settings_Seconds_One
        : string.Format(CultureInfo.CurrentCulture, Strings.Settings_Seconds_Many, seconds);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _audioInputReady = true;
        if (!_effectsSubscribed)
        {
            EffectsPolicy.Changed += OnEffectsChanged;
            _effectsSubscribed = true;
        }
        UpdateEffectsText();
        BuildHotkeyRows();
        RefreshCompanionPage();
        UpdatePairingHelp();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _audioInputReady = false;
        CancelPendingVolumeChange();
        if (_effectsSubscribed)
        {
            EffectsPolicy.Changed -= OnEffectsChanged;
            _effectsSubscribed = false;
        }
    }

    private void OnEffectsChanged(object? sender, EventArgs e) => UpdateEffectsText();

    private void UpdateEffectsText()
        => EffectsText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Settings_Effects_Details, EffectsPolicy.Describe());

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        foreach (var entry in SectionEntries)
        {
            if (!ReferenceEquals(entry.Nav, sender)) continue;
            JumpTo(entry.Nav, entry.Section); // first row of this item
            return;
        }
    }

    // ----- Left rail follows the page -----

    /// <summary>One row per section in on-page order. Battery history has no rail item of its own, so it lights AirPods.</summary>
    private readonly record struct SectionEntry(FrameworkElement Section, FrameworkElement Nav);

    private SectionEntry[]? _sectionEntries;
    private FrameworkElement? _selectedNav;
    private bool _navPinned;
    private bool _selectionUpdateQueued;

    private SectionEntry[] SectionEntries => _sectionEntries ??= new SectionEntry[]
    {
        new(GeneralSection, NavGeneral),
        new(AirPodsSection, NavAirPods),
        new(BatteryHistorySection, NavAirPods),
        new(SoundSection, NavSound),
        new(ShortcutsSection, NavShortcuts),
        new(AdvancedSection, NavAdvanced),
        new(AboutSection, NavAbout),
    };

    private void HookNavFollowsScroll()
    {
        _selectedNav = NavGeneral; // the XAML starts with General selected
        ContentScroller.ViewChanged += (_, _) => QueueSelectionUpdate();
        ContentScroller.SizeChanged += (_, _) => QueueSelectionUpdate();
        // Sections change height (expanders, text), which moves the others without a scroll event.
        if (ContentScroller.Content is FrameworkElement content) content.SizeChanged += (_, _) => QueueSelectionUpdate();
        // The user scrolling by hand releases a nav click's pin. The ScrollViewer often marks wheel and
        // key events handled, so listen for handled events too.
        ContentScroller.AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _navPinned = false), true);
        ContentScroller.AddHandler(KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnContentKeyDown), true);
        ContentScroller.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnContentPointerPressed), true);
        ContentScroller.DirectManipulationStarted += (_, _) => _navPinned = false; // touch / pen drag
    }

    private void OnContentKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown
            or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End
            or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
            _navPinned = false;
    }

    private void OnContentPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Only the scroll bar (arrows, track, thumb) counts as scrolling; clicking content does not.
        for (var node = e.OriginalSource as DependencyObject;
             node is not null && !ReferenceEquals(node, ContentScroller);
             node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollBar) { _navPinned = false; return; }
        }
    }

    /// <summary>Coalesces bursts of scroll/size events into one recalculation per dispatcher turn.</summary>
    private void QueueSelectionUpdate()
    {
        if (_navPinned || _selectionUpdateQueued) return;
        _selectionUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _selectionUpdateQueued = false;
            UpdateSelectionFromScroll();
        });
    }

    private void UpdateSelectionFromScroll()
    {
        if (_navPinned) return;
        var entries = SectionEntries;
        var tops = new double?[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            tops[i] = entries[i].Nav.Visibility == Visibility.Visible ? SectionTop(entries[i].Section) : null;
        bool atTop = ContentScroller.VerticalOffset <= 1;
        bool atEnd = ContentScroller.ScrollableHeight > 1 && ContentScroller.VerticalOffset >= ContentScroller.ScrollableHeight - 1;
        int index = SettingsSectionSelector.Choose(tops, ContentScroller.ViewportHeight, atTop, atEnd);
        if (index >= 0) SelectNav(entries[index].Nav);
    }

    /// <summary>The section's top edge measured from the top of the visible area, read now (no cached offsets); null if hidden.</summary>
    private double? SectionTop(FrameworkElement section)
    {
        if (section.Visibility != Visibility.Visible || section.ActualHeight <= 0) return null;
        try { return section.TransformToVisual(ContentScroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y; }
        catch (ArgumentException) { return null; } // not in the visual tree right now
    }

    /// <summary>Highlights one rail item. Only the two buttons involved change style, and only when the choice really changed.</summary>
    private void SelectNav(FrameworkElement nav)
    {
        if (ReferenceEquals(nav, _selectedNav)) return;
        var previous = _selectedNav;
        _selectedNav = nav;
        var focusRoot = XamlRoot;
        var focused = focusRoot is null ? null : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(focusRoot) as Control;
        var focusState = focused?.FocusState ?? FocusState.Unfocused;
        if (previous is not null) previous.Style = (Style)Application.Current.Resources["GlassNavButtonStyle"];
        nav.Style = (Style)Application.Current.Resources["GlassNavSelectedButtonStyle"];
        // A style swap re-applies the template; make sure keyboard focus did not go anywhere.
        if (focused is not null && focusRoot is not null
            && (ReferenceEquals(focused, previous) || ReferenceEquals(focused, nav))
            && !ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(focusRoot), focused))
            focused.Focus(focusState);
        // The rail has its own ScrollViewer: keep the highlighted item visible without touching content scroll or focus.
        nav.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    /// <summary>A jump (nav click or code) selects its item now and pins it until the user scrolls by hand.</summary>
    private void JumpTo(FrameworkElement nav, FrameworkElement section)
    {
        _navPinned = true;
        SelectNav(nav);
        section.StartBringIntoView();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Update()
        {
            if (!ReferenceEquals(sender, Connection)) return;
            Bindings.Update();
            RefreshArtworkColorways();
            UpdatePairingHelp();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    /// <summary>The host window was shown (true) or hidden (false). A hidden window stops the pairing animation.</summary>
    public void SetWindowVisible(bool visible) => PairingHelp.SetActive(visible);

    /// <summary>
    /// The pairing help opens by itself when no AirPods are paired and closes once one is; in between it stays as the
    /// user left it. It starts on the model Clasp already knows, if any.
    /// </summary>
    private void UpdatePairingHelp()
    {
        bool hasDevices = Connection?.Devices.Count > 0;
        if (_pairingHelpHadDevices != hasDevices)
        {
            _pairingHelpHadDevices = hasDevices;
            PairingHelp.SetExpanded(!hasDevices);
        }
        PairingHelp.SetModel(_viewModel?.ModelName);
    }

    private void OnPairedDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Connection is null || PairedDevicePicker.SelectedItem is not ConnectionDevice device) return;
        if (!device.HasAvailableAudioEndpoint)
        {
            PairedDevicePicker.SelectedItem = SelectedPairedDevice;
            return;
        }
        Connection.SelectedDeviceId = device.Id;
    }

    private async void OnRefreshPairedDevicesClick(object sender, RoutedEventArgs e)
    {
        if (Connection is null) return;
        _isRefreshingPairedDevices = true;
        Bindings.Update();
        try { await Connection.RefreshAsync(); }
        catch (Exception) { /* StatusText and DetailText show the last complete state. */ }
        finally
        {
            _isRefreshingPairedDevices = false;
            Bindings.Update();
        }
    }

    private void OnClearPairedDeviceClick(object sender, RoutedEventArgs e)
    {
        Connection?.ClearSelection();
        PairedDevicePicker.SelectedItem = null;
    }

    private void OnOpenBluetoothSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception) { /* Windows Settings is optional; the paired device list remains usable. */ }
    }

    private void OnCaseSecondsChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_settings is null) return;
        int seconds = (int)Math.Round(e.NewValue);
        if (seconds != _settings.CaseCardSeconds) _settings.CaseCardSeconds = seconds;
    }

    // ----- Companion features -----

    /// <summary>Refreshes everything on the companion sections that reads live state.</summary>
    private void RefreshCompanionPage()
    {
        // The demo never reads or changes Windows (as in the Sound section): it does not
        // look at the sign-in entry, the per-app output interface or nearby phones.
        var demo = ViewModel?.IsDemo == true;
        if (_companion is { } host)
        {
            host.RefreshStatusTexts();
            if (!demo) host.CheckPerAppSupport();
        }
        RefreshStartupToggle();
        RefreshBatteryHistory();
        UpdateHotkeyProblems();
        UpdatePhoneUi();
        if (!demo) _ = RefreshPhonesAsync();
    }

    private void OnCompanionStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Update()
        {
            if (!ReferenceEquals(sender, _companion?.Status)) return;
            if (e.PropertyName == nameof(CompanionStatus.HotkeyProblems)) UpdateHotkeyProblems();
            else if (e.PropertyName == nameof(CompanionStatus.PhoneState)) UpdatePhoneUi();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void OnEarOutputSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_audioInputReady || Settings is not { } settings) return;
        if (sender is ComboBox { SelectedItem: AudioOutputChoice output }
            && !string.Equals(settings.EarRemovedOutputEndpointId, output.Id, StringComparison.Ordinal))
        {
            settings.EarRemovedOutputEndpointId = output.Id;
        }
        Bindings.Update();
    }

    private void OnClearEarOutputClick(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings) return;
        settings.EarRemovedOutputEndpointId = "";
        Bindings.Update();
    }

    private async void OnFixConnectionClick(object sender, RoutedEventArgs e)
    {
        if (_companion is not { } host) return;
        try { await host.RunRepairAsync(); }
        catch (Exception) { /* the status line already shows the last message */ }
    }

    private void OnCancelFixConnectionClick(object sender, RoutedEventArgs e) => _companion?.CancelRepair();

    private void OnRefreshBatteryHistoryClick(object sender, RoutedEventArgs e) => RefreshBatteryHistory();

    private async void OnClearBatteryHistoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Settings_History_Clear_Title,
                Content = Strings.Settings_History_Clear_Content,
                PrimaryButtonText = Strings.Settings_History_Clear_Confirm,
                CloseButtonText = Strings.Settings_History_Clear_Cancel,
                DefaultButton = ContentDialogButton.Close, // Enter must not delete by accident
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _companion?.ClearBatteryData();
            RefreshBatteryHistory();
        }
        catch (Exception)
        {
            // A dialog is already open or the window is closing; nothing was cleared.
        }
    }

    private void RefreshBatteryHistory()
    {
        BatteryHistoryPanel.Show(
            _companion?.BatterySamples() ?? Array.Empty<BatterySample>(),
            DateTimeOffset.UtcNow,
            _companion?.LastHeardAt());
    }

    /// <summary>Back to the top: Settings is hidden, not closed, so a reopen should not keep the old scroll or pin.</summary>
    public void ResetToTop()
    {
        if (!IsLoaded) return;
        _navPinned = false;
        ContentScroller.ChangeView(null, 0, null, true);
        SelectNav(NavGeneral);
    }

    /// <summary>Dev-only (--settings-section=name): jump to General, AirPods, History, Sound, Shortcuts, Advanced or About.</summary>
    public void JumpToSectionByName(string name)
    {
        var map = new (string Name, FrameworkElement Nav, FrameworkElement Section)[]
        {
            ("general", NavGeneral, GeneralSection), ("airpods", NavAirPods, AirPodsSection),
            ("history", NavAirPods, BatteryHistorySection), ("sound", NavSound, SoundSection),
            ("shortcuts", NavShortcuts, ShortcutsSection), ("advanced", NavAdvanced, AdvancedSection),
            ("about", NavAbout, AboutSection),
        };
        foreach (var m in map)
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) { JumpTo(m.Nav, m.Section); return; }
    }

    /// <summary>Called by the flyout's history button: refresh and scroll to the chart.</summary>
    public void ShowBatteryHistory()
    {
        RefreshBatteryHistory();
        JumpTo(NavAirPods, BatteryHistorySection);
    }

    // ----- Startup: the registry is the truth; nothing is saved in settings -----

    private void RefreshStartupToggle()
    {
        if (_companion is null || ViewModel?.IsDemo == true)
        {
            RunOnStartupToggle.IsEnabled = false;
            RunOnStartupStatus.Text = ViewModel?.IsDemo == true ? Strings.Settings_Startup_Unavailable_Demo : "";
            return;
        }
        RunOnStartupToggle.IsEnabled = true;
        var status = CompanionHost.GetStartupStatus();
        _startupRefreshing = true;
        try { RunOnStartupToggle.IsOn = status is StartupStatus.On or StartupStatus.OffInTaskManager; }
        finally { _startupRefreshing = false; }
        RunOnStartupStatus.Text = status is { } known
            ? StartupMessages.Describe(known)
            : Strings.Settings_Startup_ReadFailed;
    }

    private void OnRunOnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_startupRefreshing || _companion is null) return;
        var result = CompanionHost.SetRunOnStartup(RunOnStartupToggle.IsOn);
        RunOnStartupStatus.Text = result.Message;
        if (!result.Succeeded) RefreshStartupToggle(); // show what Windows really has
    }

    // ----- Phone audio -----

    private async Task RefreshPhonesAsync()
    {
        if (_companion is null) return;
        PhoneRefreshButton.IsPending = true;
        try
        {
            var search = await CompanionHost.FindPhonesAsync();
            _phones = PhoneDeviceList.Normalize(search.Candidates);
            _phoneNote = !search.Succeeded
                ? PhoneStreamingStates.Unavailable().Message
                : _phones.Count == 0 ? PhoneStreamingStates.NoPhonesFound().Message : null;
            FillPhonePicker();
        }
        catch (Exception)
        {
            _phones = Array.Empty<PhoneStreamingCandidate>();
            _phoneNote = PhoneStreamingStates.Unavailable().Message;
            FillPhonePicker();
        }
        finally
        {
            PhoneRefreshButton.IsPending = false;
            UpdatePhoneUi();
        }
    }

    private void FillPhonePicker()
    {
        var selectedId = PhoneDeviceList.ChooseSelection(_phones, SelectedPhoneId ?? Settings?.PhoneAudioDeviceId);
        _phoneListUpdating = true;
        try
        {
            PhonePicker.Items.Clear();
            foreach (var phone in _phones)
            {
                var item = new ComboBoxItem { Content = PhoneStreamingStates.DisplayName(phone.Name), Tag = phone.DeviceId };
                PhonePicker.Items.Add(item);
                if (string.Equals(phone.DeviceId, selectedId, StringComparison.OrdinalIgnoreCase)) PhonePicker.SelectedItem = item;
            }
        }
        finally { _phoneListUpdating = false; }
    }

    private string? SelectedPhoneId => (PhonePicker.SelectedItem as ComboBoxItem)?.Tag as string;

    private string? SelectedPhoneName
        => _phones.FirstOrDefault(phone => string.Equals(phone.DeviceId, SelectedPhoneId, StringComparison.OrdinalIgnoreCase))?.Name;

    private void OnPhoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_phoneListUpdating) return;
        if (SelectedPhoneId is { } id && Settings is { } settings) settings.PhoneAudioDeviceId = id;
        UpdatePhoneUi();
    }

    private void UpdatePhoneUi()
    {
        var demo = ViewModel?.IsDemo == true;
        var state = _companion?.Status.PhoneState ?? PhoneStreamingStates.Idle();
        PhoneStatusText.Text = demo
            ? Strings.Settings_Phone_Unavailable_Demo
            : state.Phase == PhoneStreamingPhase.Idle && _phoneNote is not null ? _phoneNote : state.Message;
        PhoneRefreshButton.IsEnabled = _companion is not null && !demo;
        PhoneConnectButton.Content = state.IsActive ? Strings.Settings_Phone_Disconnect : Strings.Settings_Phone_Connect;
        PhoneConnectButton.IsEnabled = _companion is not null && !demo && (state.IsActive || PhoneStreamingStates.CanConnect(state, SelectedPhoneId));
        PhoneConnectButton.IsPending = state.Phase == PhoneStreamingPhase.Connecting;
    }

    private void OnPhoneRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshPhonesAsync();

    private async void OnPhoneConnectClick(object sender, RoutedEventArgs e)
    {
        if (_companion is not { } host) return;
        try
        {
            if (host.PhoneState.IsActive) await host.StopPhoneAsync();
            else if (SelectedPhoneId is { } id) await host.StartPhoneAsync(id, SelectedPhoneName);
        }
        catch (Exception)
        {
            // PhoneStreaming reports failures through its state; nothing else to do here.
        }
        UpdatePhoneUi();
    }

    // ----- Global shortcuts: one row per command, validated before anything is saved -----

    private void BuildHotkeyRows()
    {
        if (_hotkeyRowsBuilt || Settings is not { } settings) return;
        _hotkeyRowsBuilt = true;
        foreach (var command in Enum.GetValues<ClaspCommand>())
        {
            var name = ClaspCommandText.DisplayName(command);
            var label = new TextBlock { Text = name, Style = (Style)Application.Current.Resources["GlassLabelTextStyle"], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords };
            var box = new TextBox
            {
                Text = settings.GetHotkey(command),
                PlaceholderText = Strings.Settings_Hotkey_NotSet,
                Width = 136,
                MaxLength = 64,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, string.Format(CultureInfo.CurrentCulture, Strings.Settings_Hotkey_BoxAutomationName, name));
            var clear = new GlassButton { Content = Strings.Settings_Hotkey_ClearButton };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clear, string.Format(CultureInfo.CurrentCulture, Strings.Settings_Hotkey_ClearAutomationName, name));
            var message = new TextBlock
            {
                Style = (Style)Application.Current.Resources["GlassCaptionTextStyle"],
                TextWrapping = TextWrapping.WrapWholeWords,
                Visibility = Visibility.Collapsed,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(box, 1);
            Grid.SetColumn(clear, 2);
            grid.Children.Add(label);
            grid.Children.Add(box);
            grid.Children.Add(clear);

            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(grid);
            row.Children.Add(message);
            HotkeyRows.Children.Add(row);
            _hotkeyControls[command] = (box, message);

            var captured = command;
            box.LostFocus += (_, _) => CommitHotkey(captured);
            box.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    CommitHotkey(captured);
                    args.Handled = true;
                }
            };
            clear.Click += (_, _) =>
            {
                box.Text = "";
                CommitHotkey(captured);
            };
        }
        settings.PropertyChanged += OnHotkeySettingChanged;
    }

    private void OnHotkeySettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!HubSettings.IsHotkeyProperty(e.PropertyName) || Settings is not { } settings) return;
        foreach (var (command, controls) in _hotkeyControls)
            if (HubSettings.HotkeyPropertyName(command) == e.PropertyName && controls.Box.Text != settings.GetHotkey(command)
                && !controls.Box.FocusState.HasFlag(FocusState.Keyboard) && !controls.Box.FocusState.HasFlag(FocusState.Pointer))
                controls.Box.Text = settings.GetHotkey(command);
    }

    private void CommitHotkey(ClaspCommand command)
    {
        if (Settings is not { } settings || !_hotkeyControls.TryGetValue(command, out var controls)) return;
        var text = controls.Box.Text.Trim();
        if (text.Length == 0)
        {
            settings.SetHotkey(command, "");
            controls.Box.Text = "";
            ShowHotkeyMessage(command, null);
            return;
        }
        if (!HotkeyBinding.TryParse(text, out var binding, out var reason) || binding is null)
        {
            ShowHotkeyMessage(command, string.Format(CultureInfo.CurrentCulture, Strings.Settings_Hotkey_ParseFailed, reason));
            return;
        }
        var validation = binding.Validate();
        if (!validation.IsValid)
        {
            ShowHotkeyMessage(command, validation.Reason);
            return;
        }
        var canonical = binding.ToString();
        controls.Box.Text = canonical;
        settings.SetHotkey(command, canonical);
        ShowHotkeyMessage(command, null); // a registration problem, if any, arrives from the host
        UpdateHotkeyProblems();
    }

    private void ShowHotkeyMessage(ClaspCommand command, string? text)
    {
        if (!_hotkeyControls.TryGetValue(command, out var controls)) return;
        controls.Message.Text = text ?? "";
        controls.Message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateHotkeyProblems()
    {
        var problems = _companion?.Status.HotkeyProblems;
        foreach (var command in _hotkeyControls.Keys)
        {
            // Keep a validation message the user is still fixing; otherwise show the registration result.
            var shown = problems is not null && problems.TryGetValue(command, out var problem) ? problem : null;
            if (shown is null && _hotkeyControls[command].Message.Visibility == Visibility.Visible
                && !HotkeyBinding.TryParse(_hotkeyControls[command].Box.Text.Trim(), out _, out _)) continue;
            ShowHotkeyMessage(command, shown);
        }
    }
}
