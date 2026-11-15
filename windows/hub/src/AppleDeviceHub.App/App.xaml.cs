﻿using System.ComponentModel;
using System.Globalization;
using AppleDeviceHub.App.Ble;
using AppleDeviceHub.App.Companion;
using AppleDeviceHub.App.Audio;
using AppleDeviceHub.App.Connection;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.App.Media;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Commands;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;
using AppleDeviceHub.Core.Interop;
using AppleDeviceHub.Core.Media;
using AppleDeviceHub.Core.Pairing;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Resources;
using AppleDeviceHub.Core.Storage;
using AppleDeviceHub.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AppleDeviceHub.App;

/// <summary>
/// Tray-first app: no main window. Startup picks the transport deliberately, then creates the
/// tray icon and the reusable flyout (which also peeks when the AirPods case opens).
///
/// Transports: --transport=demo (the default; synthetic data with a visible Demo badge), or
/// --transport=hardware --engine-path=&lt;full path&gt; [--engine-sha256=&lt;hex&gt;]
/// --device=&lt;dev-...&gt; [--session=observe|control] for the Clasp engine. Nothing here starts
/// an engine process, searches PATH or asks for elevation; the engine must already be running.
/// A hardware request that is invalid or cannot connect shows a hardware-unavailable state with
/// the reason and never falls back to the demo. --transport=ble (opt-in) observes nearby Apple
/// proximity broadcasts read-only, and listens automatically while nearby detection is on (the default).
///
/// Optional arguments for manual review: --show=flyout | --show=card (the flyout peek) | --show=settings | --show=welcome (with --welcome-page=1..4), and
/// (demo transport only) --demo=nearby | --demo=connecting to start in that connection state, and
/// --demo-model=&lt;hex&gt; (for example --demo-model=0x1720) to show that model id's artwork and battery layout.
///
/// --settings-section=general|airpods|history|sound|shortcuts|advanced|about (with --show=settings) jumps to that section (screenshots only).
/// --demo-battery=low|unknown|stale (demo transport only) starts with that battery situation (screenshots only).
/// --demo-ears=in|out|left|case (demo transport only) reports that ear placement once connected: buds in ears, out of the case and ears, only the left bud out, or both in the case (screenshots only).
/// --demo-history (demo transport only) shows made-up battery history in Settings (screenshots only; never saved);
/// --demo-history=sparse shows an irregular-use week instead, to check how the chart handles long silent stretches.
/// --startup (the sign-in entry) starts quietly to the tray: no flyout, case peek or settings window.
/// A launch from a clasp:// link (--uri= / --command=) ignores every other argument, so a crafted
/// link cannot smuggle options such as --transport=hardware or --engine-path into the command
/// line; when no copy is running it starts like a sign-in launch (BLE, quiet) and then runs the
/// command if clasp:// links are turned on. A second copy only forwards the command and exits.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The Application lives for the whole process; Quit() disposes the tray, view model and mutex before Exit().")]
public partial class App : Application
{
    private const string DemoModelPrefix = "--demo-model=";

    /// <summary>The fixed arguments used when a clasp:// link starts Clasp: never taken from the link itself.</summary>
    private static readonly string[] CommandLaunchArguments = ["--transport=ble", "--startup"];

    private Mutex? _singleInstance;
    private DispatcherQueue? _queue;
    private HubSettings _settings = new();
    private CaseCardPolicy? _cardPolicy;
    private HubViewModel? _viewModel;
    private CompanionConnectionViewModel? _connectionViewModel;
    private WindowsAirPodsConnectionController? _connectionController;
    private BroadcastObservationTransport? _broadcastTransport;
    private NearbyPromptPolicy? _nearbyPromptPolicy;
    private DispatcherQueueTimer? _nearbyPromptTimer;
    private readonly SemaphoreSlim _observationSettingsGate = new(1, 1);
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcomeWindow;
    private IPairingDiscovery? _pairingDiscovery;
    private IAirPodsTransport? _transport;
    private PlaybackViewModel? _playbackViewModel;
    private AudioOutputViewModel? _audioOutput;
    private WindowsAudioOutputController? _audioController;
    private CompanionHost? _host;
    private ClaspCommand? _launchCommand;
    private int _playbackGeneration;
    private bool _isQuitting;
    private volatile bool _quitCleanupDone;
    private int _nearbyEvaluationQueued;
    private UpdateViewModel? _updates;
    private DispatcherQueueTimer? _updateTimer;

    public App()
    {
        Design.XamlCastTypes.Keep();
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static string DataFolder => AppDataFolder.Current;

    /// <summary>
    /// Leaves a trace of a fatal crash: a line in hub.log (type, HRESULT and stack, never the message) and a small
    /// marker file so the next launch can say Clasp closed unexpectedly. Never throws.
    /// </summary>
    private void RecordFatal(string source, Exception? exception)
    {
        try
        {
            LocalLog.Write($"Fatal ({source}): {LogText.Exception(exception)}");
            if (!_quitCleanupDone) CrashMarker.TryWrite(DataFolder, DateTimeOffset.UtcNow, exception);
        }
        catch (Exception)
        {
            // Already crashing; there is nothing more to do.
        }
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        => RecordFatal("AppDomain", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Not fatal on .NET Core: log it (type and stack only) and mark it observed so it cannot escalate.
        LocalLog.Write($"Unobserved task exception: {LogText.Exception(e.Exception)}");
        e.SetObserved();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LocalLog.Write($"Unhandled: {LogText.Exception(e.Exception)}");
        if (!_quitCleanupDone)
        {
            // Normal running: leave the default behaviour alone (the process ends); just leave the marker first.
            RecordFatal("WinUI", e.Exception);
            return;
        }
        // Everything is already stopped and saved, so a fault while the windows tear down must not become a crash that
        // Windows Error Reporting keeps alive (which would also stop an update installer from replacing Clasp).
        e.Handled = true;
        LocalLog.Write("Unhandled exception during quit teardown; ending the process.");
        Environment.Exit(0);
    }

    /// <summary>
    /// If the last run left a crash marker, tells the person once (a toast, the same way update and low-battery notices
    /// are shown; the flyout has no place for a one-off message) and then removes the marker. If the toast cannot be
    /// shown the marker stays for the next launch (it is dropped after 30 days), so the notice is not silently lost.
    /// </summary>
    private static void ShowCrashNoticeIfAny()
    {
        try
        {
            var folder = DataFolder;
            if (CrashMarker.ReadPending(folder, DateTimeOffset.UtcNow) is not { } crash) return;
            LocalLog.Write($"The previous run ended with a crash ({crash.ExceptionType}).");
            var logFolder = "%LOCALAPPDATA%\\" + Path.GetFileName(folder);
            var shown = Toasts.Show(Strings.Toast_CrashNotice_Title,
                string.Format(CultureInfo.CurrentCulture, Strings.Toast_CrashNotice_Body, logFolder));
            if (shown) CrashMarker.Clear(folder);
            else LocalLog.Write("Crash notice could not be shown; it will be tried again next time.");
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Crash notice failed: {LogText.ExceptionKind(ex)}");
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var commandLaunch = ClaspCommandParser.IsCommandLaunch(launchArgs);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceNames.MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // One tray icon and one engine owner per user session. A link or --command= launch
            // hands its (re-validated, canonical) command to the running copy first.
            if (commandLaunch)
            {
                var (forwarded, forwardFailure) = await CommandRouter.TryForwardAsync(launchArgs);
                if (forwarded) LocalLog.Write("Command forwarded to the running instance.");
                else LocalLog.Write($"Could not forward command: {forwardFailure}");
            }
            LocalLog.Write("Another instance is running; exiting.");
            Exit();
            return;
        }

        // The mutex is ours, so no other Clasp is touching the data folder. Bring over data from the pre-rename folder before
        // anything reads or writes it, and repair per-user entries that still name the old program.
        AppDataFolder.MigrateDefault(LocalLog.Write);
        if (StartupRegistration.RepairLegacyEntry(Environment.ProcessPath)) LocalLog.Write("Sign-in entry now points at Clasp.exe.");
        if (ProtocolRegistration.RepairLegacy(Environment.ProcessPath)) LocalLog.Write("clasp: link entry now points at Clasp.exe.");

        _queue = DispatcherQueue.GetForCurrentThread();
        EffectsPolicy.Initialize(_queue);
        _settings = HubSettingsStore.Load(HubSettingsStore.DefaultPath, LocalLog.Write);
        EffectsPolicy.SetOverrides(_settings.ReduceMotion, _settings.ReduceTransparency);
        _settings.PropertyChanged += OnSettingChanged;

        // A command launch trusts nothing but the command: every other argument is ignored.
        var argv = commandLaunch ? CommandLaunchArguments : launchArgs;
        if (commandLaunch)
        {
            var parsed = ClaspCommandParser.ParseArgs(launchArgs);
            _launchCommand = parsed?.Command;
            if (_launchCommand is null) LocalLog.Write($"Launch command rejected: {parsed?.Reason}");
        }
        var quietStart = argv.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        var (transport, deviceId) = CreateTransport(TransportSelection.FromArgs(argv), argv);
        _transport = transport;

        var queue = _queue;
        _cardPolicy = new CaseCardPolicy { AutoPresentEnabled = _settings.AutoShowCaseCard };
        _viewModel = new HubViewModel(
            transport,
            deviceId,
            action => queue.TryEnqueue(() => action()),
            _cardPolicy);
        if (transport.Info.Origin == DataOrigin.Demo)
        {
            _pairingDiscovery = new DemoPairingDiscovery();
        }
        else
        {
            try { _pairingDiscovery = new WindowsPairingDiscovery(); }
            catch (Exception ex) { LocalLog.Write($"Pairing search unavailable ({ex.GetType().Name})."); }
            if (transport is BroadcastObservationTransport broadcasts)
            {
                _broadcastTransport = broadcasts;
                _nearbyPromptPolicy = new NearbyPromptPolicy();
                broadcasts.SourcesChanged += OnNearbySourcesChanged;
                _nearbyPromptTimer = queue.CreateTimer();
                _nearbyPromptTimer.Interval = TimeSpan.FromMilliseconds(250);
                _nearbyPromptTimer.IsRepeating = true;
                _nearbyPromptTimer.Tick += (_, _) => EvaluateNearbyPrompt();
            }
            try
            {
                _connectionController = new WindowsAirPodsConnectionController();
                _connectionViewModel = new CompanionConnectionViewModel(
                    _connectionController,
                    _settings,
                    action => queue.TryEnqueue(() => action()),
                    autoConnectIdentity: IdentityForAutoConnect,
                    log: LocalLog.Write);
            }
            catch (Exception ex)
            {
                LocalLog.Write($"Paired-device connection controls unavailable ({ex.GetType().Name}).");
            }
            try
            {
                _audioController = new WindowsAudioOutputController();
                _audioOutput = new AudioOutputViewModel(_audioController, _settings,
                    action => queue.TryEnqueue(() => action()),
                    restorationLog: LocalLog.Write,
                    log: LocalLog.Write);
            }
            catch (Exception ex) { LocalLog.Write($"Audio output access unavailable ({ex.GetType().Name})."); }
        }
        LocalLog.Write($"Started. Transport: {_viewModel.Transport.Name} (origin {_viewModel.Transport.Origin}, contract {_viewModel.Transport.ContractVersion}).");
        LocalLog.Write($"Startup media settings: pause-on-ear-removal={_settings.PauseMediaOnEarRemoval}; playback-controls={_settings.ShowPlaybackControls}; transport={_viewModel.Transport.Name} (origin {_viewModel.Transport.Origin}).");

        var updateService = new GitHubUpdateService();
        updateService.CleanUpOldDownloads();
        var updates = _updates = new UpdateViewModel(updateService, _settings);
        updates.UpdateFound += (_, release) =>
            Toasts.Show(string.Format(CultureInfo.CurrentCulture, Strings.Toast_UpdateAvailable_Title, release.Version), Strings.Toast_UpdateAvailable_Body);
        updates.InstallerStarted += (_, _) => Quit();

        var viewModel = _viewModel;
        var flyout = _flyout = new FlyoutWindow(viewModel, playbackViewModel: null,
            audioOutput: _audioOutput, connection: _connectionViewModel, updates: updates, settings: _settings);
        var tray = _tray = new TrayIcon(TooltipText());
        var host = _host = new CompanionHost(_settings, queue, viewModel, transport, _broadcastTransport,
            _connectionViewModel, _connectionController, _audioController, _audioOutput, TooltipText, ShowSettings);
        host.AttachShell(tray, flyout);
        host.BatteryHistoryRequested += (_, _) => ShowBatteryHistory();

        flyout.SettingsRequested += (_, _) => ShowSettings();

        tray.Activated += (_, a) =>
        {
            // Keyboard selection only opens (Escape closes): Windows can deliver NIN_KEYSELECT
            // twice for one Enter press, which would otherwise open and immediately close.
            host.RefreshLastHeard();
            if (a.ByKeyboard)
            {
                // Keyboard focus state, so the focus ring is visible on the first control.
                // A passive peek is promoted to the focused panel too, not left unfocused.
                if (!flyout.IsOpen || flyout.IsPassive) flyout.Open(tray.GetIconRect(), (a.X, a.Y), FlyoutOpenReason.TrayClick, byKeyboard: true);
            }
            else
            {
                flyout.Toggle(tray.GetIconRect(), (a.X, a.Y));
            }
        };
        tray.OpenRequested += (_, _) =>
        {
            host.RefreshLastHeard();
            flyout.Open(tray.GetIconRect(), NativeMethods.CursorPosition(), FlyoutOpenReason.TrayClick);
        };
        tray.MenuState = BuildTrayMenuState;
        tray.ConnectToggleRequested += async (_, _) => await ToggleConnectionAsync();
        tray.SettingsRequested += (_, _) => ShowSettings();
        tray.QuitRequested += (_, _) => Quit();
        tray.Show();
        ShowCrashNoticeIfAny();

        viewModel.PropertyChanged += (_, _) => RefreshTrayTooltip();
        viewModel.Left.PropertyChanged += (_, _) => RefreshTrayTooltip();
        viewModel.Right.PropertyChanged += (_, _) => RefreshTrayTooltip();
        viewModel.Single.PropertyChanged += (_, _) => RefreshTrayTooltip();
        if (_audioOutput is not null) _audioOutput.PropertyChanged += OnAudioOutputPropertyChanged;
        viewModel.CaseCardRequested += (_, _) =>
        {
            // Automatic presentations wait while the user is in a full-screen app or presenting.
            if (NativeMethods.IsUserBusy()) LocalLog.Write("Case peek suppressed: user busy/full screen.");
            else flyout.PresentPassive(tray.GetIconRect(), _settings.CaseCardSeconds);
        };

        ApplyTheme();
        flyout.Prewarm();

        try
        {
            await viewModel.StartAsync();
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Transport start failed: {LogText.ExceptionKind(ex)}");
        }
        if (viewModel.UnavailableReason is not null) LocalLog.Write("Hardware transport unavailable.");

        if (_connectionViewModel is not null) _ = _connectionViewModel.RefreshAsync();
        if (_settings.ObserveNearbyAirPods && _broadcastTransport is not null && !viewModel.IsBroadcastObservation)
        {
            // The transport's first state reaches the view model through the dispatcher shortly after
            // StartAsync, so a request made now would be dropped; apply the setting once it is ready.
            PropertyChangedEventHandler? ready = null;
            ready = (_, e) =>
            {
                if (e.PropertyName != nameof(HubViewModel.IsBroadcastObservation) || !viewModel.IsBroadcastObservation) return;
                viewModel.PropertyChanged -= ready;
                _ = ApplyNearbyObservationSettingAsync();
            };
            viewModel.PropertyChanged += ready;
        }
        await ApplyNearbyObservationSettingAsync();

        // Design review: start the demo disconnected (nearby) or mid-connection before showing.
        // Demo transport only: these must never send Disconnect/Connect to real hardware.
        if (viewModel.Demo is { } demo)
        {
            if (argv.Contains("--demo=nearby") || argv.Contains("--demo=connecting"))
            {
                await viewModel.DisconnectAsync();
                await Task.Delay(400); // the demo reads the disconnect back after a short delay
            }
            if (argv.Contains("--demo=connecting"))
            {
                demo.Scenario.ConnectionStepDelay = TimeSpan.FromMinutes(10); // hold "Connecting…" for inspection
                _ = viewModel.ConnectAsync();
            }
        }
        else if (argv.Any(a => a.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase)))
        {
            LocalLog.Write("--demo= ignored: the demo transport is not active.");
        }

        // Sleep/resume: never resume media across it.
        Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatusChanged += (_, _) =>
        {
            _host?.OnSystemPowerChange();
            _audioOutput?.ForgetHistory();
        };
        await host.StartAsync();
        await ApplyPlaybackSettingAsync();

        // The welcome guide: once on a new install's first normal launch, or when asked for (--show=welcome, with
        // --welcome-page=N for a screenshot of one page). Never on the demo transport unless asked, never on a quiet start.
        bool askedForWelcome = argv.Contains("--show=welcome", StringComparer.OrdinalIgnoreCase);
        bool showWelcomeAtStartup = WelcomePolicy.ShouldShowAtStartup(_settings.WelcomeCompleted, viewModel.IsDemo, quietStart, _launchCommand is not null, askedForWelcome);

        // A sign-in (--startup) launch is quiet: only the tray icon. When the welcome guide opens, it wins: the installer's
        // first launch (--show=flyout) would otherwise pop the flyout up underneath it, and the two would fight for focus.
        if (!quietStart && !showWelcomeAtStartup)
        {
            if (argv.Contains("--show=flyout")) flyout.Open(tray.GetIconRect(), null, FlyoutOpenReason.Programmatic);
            if (argv.Contains("--show=card")) flyout.PresentPassive(tray.GetIconRect(), _settings.CaseCardSeconds);
            if (argv.Contains("--show=settings"))
            {
                ShowSettings();
                // Dev-only, for screenshots: --settings-section=general|airpods|history|sound|shortcuts|advanced|about
                var sectionArg = argv.FirstOrDefault(a => a.StartsWith("--settings-section=", StringComparison.OrdinalIgnoreCase));
                if (sectionArg is not null)
                {
                    var section = sectionArg["--settings-section=".Length..];
                    _queue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _settingsWindow?.JumpToSection(section));
                }
            }
        }
        if (showWelcomeAtStartup)
            ShowWelcome(askedForWelcome ? WelcomePolicy.ParseStartPage(argv) : 0);
        if (_launchCommand is { } launchCommand) host.RunLinkCommand(launchCommand, "launch");

        // Automatic update checks (only after the user agreed): first a minute after launch, then
        // hourly the schedule is asked whether a daily, weekly or monthly check is due.
        _updateTimer = queue.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromMinutes(1);
        _updateTimer.IsRepeating = true;
        _updateTimer.Tick += (timer, _) =>
        {
            timer.Interval = TimeSpan.FromHours(1);
            if (!_isQuitting) _ = updates.CheckIfDueAsync();
        };
        _updateTimer.Start();
    }

    /// <summary>
    /// The demo only when it was chosen (or nothing was asked for). An explicit hardware request
    /// gets the Clasp engine transport, or, when its arguments are unusable, a transport that
    /// shows the reason and no data. Never the demo after a hardware request.
    /// </summary>
    private static (IAirPodsTransport Transport, string DeviceId) CreateTransport(TransportChoice choice, string[] argv)
    {
        var modelArg = argv.FirstOrDefault(a => a.StartsWith(DemoModelPrefix, StringComparison.OrdinalIgnoreCase));
        if (choice.IsDemo)
        {
            var scenario = new DemoScenario();
            if (modelArg is not null)
            {
                var text = modelArg[DemoModelPrefix.Length..].Trim();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
                if (ushort.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var id))
                    scenario.ModelId = id;
                else
                    LocalLog.Write("--demo-model= ignored: not a 16-bit hex model id.");
            }
            // Dev-only, for screenshots: --demo-battery=low|unknown|stale
            var batteryArg = argv.FirstOrDefault(a => a.StartsWith("--demo-battery=", StringComparison.OrdinalIgnoreCase));
            if (batteryArg is not null && Enum.TryParse<DemoBatteryScenario>(batteryArg["--demo-battery=".Length..], true, out var batteryScenario))
                scenario.Battery = batteryScenario;
            // Dev-only, for screenshots: --demo-ears=in|out|left|case
            var earsArg = argv.FirstOrDefault(a => a.StartsWith("--demo-ears=", StringComparison.OrdinalIgnoreCase));
            if (earsArg is not null)
            {
                switch (earsArg["--demo-ears=".Length..].Trim().ToLowerInvariant())
                {
                    case "in": scenario.Ears = DemoEarScenario.InEar; break;
                    case "out": scenario.Ears = DemoEarScenario.OutOfEar; break;
                    case "left": scenario.Ears = DemoEarScenario.LeftOnly; break;
                    case "case": scenario.Ears = DemoEarScenario.InCase; break;
                    default: LocalLog.Write("--demo-ears= ignored: use in, out, left or case."); break;
                }
            }
            // Dev-only, for screenshots: made-up battery history (in memory only, never saved).
            AppleDeviceHub.App.Battery.DemoBatteryHistory.Sparse = argv.Any(a => string.Equals(a, "--demo-history=sparse", StringComparison.OrdinalIgnoreCase));
            AppleDeviceHub.App.Battery.DemoBatteryHistory.Enabled = AppleDeviceHub.App.Battery.DemoBatteryHistory.Sparse
                || argv.Any(a => string.Equals(a, "--demo-history", StringComparison.OrdinalIgnoreCase));
            return (new DemoAirPodsTransport(scenario), DemoAirPodsTransport.DemoDeviceId);
        }
        if (modelArg is not null) LocalLog.Write("--demo-model= ignored: the demo transport is not active.");

        IAirPodsTransport transport;
        string deviceId;
        if (choice is { Kind: TransportKind.Broadcast, IsAvailable: true })
        {
            // Opt-in (--transport=ble), read-only. Creating it does not listen. The watcher starts
            // automatically while nearby detection is on (the default), not only on the user's
            // Start-observing (Connect) action, and stops on Stop or exit.
            var listener = new WinRtAdvertisementSource();
            listener.Diagnostic += (_, message) => LocalLog.Write($"Broadcast listener: {message}");
            transport = new BroadcastObservationTransport(listener);
            deviceId = BroadcastObservationTransport.DeviceId;
            LocalLog.Write("Broadcast observation selected; listening starts when Nearby detection is on.");
        }
        else if (choice is { IsAvailable: true, Hardware: { } options })
        {
            transport = new NtpodsDaemonTransport(options);
            deviceId = options.DeviceId;
            LocalLog.Write($"Hardware requested: {EngineWire.SessionModeName(options.SessionMode)} session for {options.DeviceId}"
                + (options.EngineSha256 is null ? " (no hash pinned)." : " (SHA-256 pinned)."));
        }
        else
        {
            transport = new UnavailableHardwareTransport(choice.Explanation);
            deviceId = "dev-unselected";
            LocalLog.Write("Hardware requested but not usable.");
        }
        if (transport is IHardwareTransportStatus status) status.Diagnostic += (_, message) => LocalLog.Write($"Hardware: {message}");
        return (transport, deviceId);
    }

    private string TooltipText()
    {
        if (_viewModel is not { } vm) return Strings.Tray_Tooltip_NoDevice;
        var name = vm.IsDemo ? string.Format(CultureInfo.CurrentCulture, Strings.Tray_Tooltip_DeviceNameDemo, vm.DeviceName) : vm.DeviceName;
        static string Level(BatteryItemViewModel b) => b.IsStale
            ? string.Format(CultureInfo.CurrentCulture, Strings.Tray_Tooltip_BatteryLevelStale, b.PercentText, b.StatusText)
            : b.PercentText;
        // A single-battery model shows its one level; earbuds show L and R (the case is never in the tooltip).
        return vm.IsSingleBattery
            ? string.Format(CultureInfo.CurrentCulture, Strings.Tray_Tooltip_SingleBattery, name, vm.ConnectionText, Level(vm.Single))
            : string.Format(CultureInfo.CurrentCulture, Strings.Tray_Tooltip_EarbudsBattery, name, vm.ConnectionText, Level(vm.Left), Level(vm.Right));
    }

    /// <summary>The tray menu's battery line and Connect/Disconnect item, read each time the menu opens.</summary>
    private TrayMenuState BuildTrayMenuState()
    {
        var connection = _connectionViewModel;
        string battery;
        if (connection?.WindowsBatteryPercent is int percent)
        {
            battery = string.Format(CultureInfo.CurrentCulture, Strings.Tray_Menu_WindowsBattery, connection.SelectedDeviceNoun, percent);
        }
        else if (_viewModel is { IsSingleBattery: true, Single.IsKnown: true } single)
        {
            var singleAge = single.Single.IsStale ? single.Single.StatusText : null;
            battery = string.IsNullOrEmpty(singleAge)
                ? string.Format(CultureInfo.CurrentCulture, Strings.Tray_Menu_NearbySingle, single.Single.PercentText)
                : string.Format(CultureInfo.CurrentCulture, Strings.Tray_Menu_NearbySingleStale, singleAge, single.Single.PercentText);
        }
        else if (_viewModel is { IsSingleBattery: false } vm && (vm.Left.IsKnown || vm.Right.IsKnown || vm.Case.IsKnown))
        {
            // The last reading stays, labelled with its age, never as live. The case reports rarely (not at all in the
            // ears), so its own age is shown beside it and the earbud age only counts left/right.
            var age = new[] { vm.Left, vm.Right }.FirstOrDefault(item => item.IsKnown && item.IsStale)?.StatusText;
            var caseText = !vm.HasCaseBattery ? ""
                : vm.Case is { IsKnown: true, IsStale: true }
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Tray_Menu_NearbyCaseStale, vm.Case.PercentText, vm.Case.StatusText)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Tray_Menu_NearbyCase, vm.Case.PercentText);
            battery = string.IsNullOrEmpty(age)
                ? string.Format(CultureInfo.CurrentCulture, Strings.Tray_Menu_NearbyEarbuds, vm.Left.PercentText, vm.Right.PercentText, caseText)
                : string.Format(CultureInfo.CurrentCulture, Strings.Tray_Menu_NearbyEarbudsStale, age, vm.Left.PercentText, vm.Right.PercentText, caseText);
        }
        else
        {
            battery = string.IsNullOrEmpty(connection?.WindowsBatteryHint)
                ? Strings.Tray_Menu_BatteryUnavailable
                : Strings.Tray_Menu_BatteryUnavailableHandsfreeOff;
        }

        if (connection is null) return new TrayMenuState(battery, Strings.Tray_Menu_ConnectAirPods, false);
        var (text, enabled) = connection switch
        {
            { IsDisconnecting: true } => (Strings.Tray_Menu_Disconnecting, false),
            { IsConnecting: true } => (Strings.Tray_Menu_Connecting, false),
            { CanDisconnect: true } => (Strings.Tray_Menu_DisconnectAirPods, true),
            { CanConnect: true } => (Strings.Tray_Menu_ConnectAirPods, true),
            _ => (Strings.Tray_Menu_ConnectAirPods, false),
        };
        return new TrayMenuState(battery, text, enabled);
    }

    private async Task ToggleConnectionAsync()
    {
        if (_connectionViewModel is not { } connection) return;
        try
        {
            if (connection.CanDisconnect) await connection.DisconnectAsync();
            else if (connection.CanConnect) await connection.ConnectAsync();
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Tray connect/disconnect failed ({ex.GetType().Name}).");
        }
    }

    private void ShowSettings() => ShowSettings(true);

    private void ShowSettings(bool resetToTop)
    {
        if (_viewModel is null) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_viewModel, _settings, _audioOutput, _playbackViewModel, _connectionViewModel, _host, _updates);
            _settingsWindow.SetTheme(WindowChrome.ToElementTheme(_settings.Theme));
            _settingsWindow.WelcomeRequested += (_, _) => ShowWelcome(0);
        }
        // Hidden, not closed: start at the top again (a new window already does).
        if (resetToTop) _settingsWindow.ResetToTop();
        _settingsWindow.ShowAndFocus();
    }

    /// <summary>Opens the welcome guide (first run, About > Show welcome guide, or --show=welcome) on page 0 to 3.</summary>
    private void ShowWelcome(int page)
    {
        if (_viewModel is null) return;
        if (_welcomeWindow is null)
        {
            _welcomeWindow = new WelcomeWindow(_viewModel, _settings, _connectionViewModel, _host, _pairingDiscovery);
            _welcomeWindow.SetTheme(WindowChrome.ToElementTheme(_settings.Theme));
        }
        _welcomeWindow.ShowAndFocus(page);
    }

    /// <summary>The flyout's history button: open Settings and jump to the battery history.</summary>
    private void ShowBatteryHistory()
    {
        ShowSettings(resetToTop: false);
        _queue?.TryEnqueue(() => _settingsWindow?.ShowBatteryHistory());
    }

    private void RefreshTrayTooltip()
    {
        if (_host is not null) _host.RefreshTooltip();
        else _tray?.SetTooltip(TooltipText());
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isQuitting) return;
        _host?.OnSettingChanged(e.PropertyName);
        switch (e.PropertyName)
        {
            case nameof(HubSettings.ReduceMotion):
            case nameof(HubSettings.ReduceTransparency):
                EffectsPolicy.SetOverrides(_settings.ReduceMotion, _settings.ReduceTransparency);
                break;
            case nameof(HubSettings.Theme):
                ApplyTheme();
                break;
            case nameof(HubSettings.ShowPlaybackControls):
                _ = ApplyPlaybackSettingAsync();
                break;
            case nameof(HubSettings.AutoShowCaseCard):
                if (_cardPolicy is not null) _cardPolicy.AutoPresentEnabled = _settings.AutoShowCaseCard;
                break;
            case nameof(HubSettings.ObserveNearbyAirPods):
                _ = ApplyNearbyObservationSettingAsync();
                break;
        }
        if (!HubSettingsStore.Save(_settings, HubSettingsStore.DefaultPath))
            LocalLog.Write("Settings could not be saved; continuing with in-memory settings.");
    }

    private async Task ApplyNearbyObservationSettingAsync()
    {
        if (_viewModel is not { IsBroadcastObservation: true } viewModel) return;
        await _observationSettingsGate.WaitAsync();
        try
        {
            if (_isQuitting) return;
            if (_settings.ObserveNearbyAirPods)
            {
                if (!viewModel.IsObserving)
                {
                    await viewModel.ConnectAsync();
                    if (viewModel.LastErrorText is { Length: > 0 }) LocalLog.Write("Nearby detection did not start.");
                }
                _nearbyPromptTimer?.Start();
            }
            else
            {
                _nearbyPromptTimer?.Stop();
                if (viewModel.IsObserving) await viewModel.DisconnectAsync();
                EvaluateNearbyPrompt();
            }
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Nearby observation could not be updated ({ex.GetType().Name}).");
        }
        finally
        {
            _observationSettingsGate.Release();
        }
    }

    private void OnNearbySourcesChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _nearbyEvaluationQueued, 1) != 0) return;
        if (_queue?.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _nearbyEvaluationQueued, 0);
            EvaluateNearbyPrompt();
        }) != true)
            Interlocked.Exchange(ref _nearbyEvaluationQueued, 0);
    }

    private void EvaluateNearbyPrompt()
    {
        if (_isQuitting || _viewModel is null || _broadcastTransport is null || _nearbyPromptPolicy is null) return;
        var decision = _nearbyPromptPolicy.Evaluate(
            _broadcastTransport.Sources,
            _broadcastTransport.Phase,
            _viewModel.IsObserving && _settings.ObserveNearbyAirPods,
            DateTimeOffset.UtcNow);

        if (decision.ShouldNotifyNearbyEpisodeAbsent) _connectionViewModel?.NotifyNearbyEpisode(false);
        if (decision.HasUniqueFreshOpenSource) _connectionViewModel?.NotifyNearbyEpisode(true);
        if (!decision.ShouldPrompt || !_settings.AutoShowCaseCard) return;
        if (NativeMethods.IsUserBusy())
        {
            LocalLog.Write("Nearby case prompt suppressed while the user is busy or in full screen.");
            return;
        }
        _flyout?.PresentPassive(_tray?.GetIconRect(), _settings.CaseCardSeconds);
    }

    private IdentityConfidence IdentityForAutoConnect()
    {
        var broadcasts = _broadcastTransport;
        if (broadcasts is null || broadcasts.Phase != BroadcastPhase.Fresh) return IdentityConfidence.Unknown;
        var matched = broadcasts.Sources.Where(source => source.Identity.HasIndependentClue()).ToArray();
        return matched.Length == 1 ? matched[0].Identity : IdentityConfidence.Unknown;
    }

    private void OnAudioOutputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioOutputViewModel.CurrentWindowsOutputId)
            or nameof(AudioOutputViewModel.CurrentWindowsOutputGeneration))
            _ = _connectionViewModel?.RefreshAsync();
    }

    /// <summary>
    /// Manual Windows-player controls are independent of AirPods observation and AAP. The
    /// Windows media session is created only while the user has left the feature enabled.
    /// </summary>
    private async Task ApplyPlaybackSettingAsync()
    {
        var generation = ++_playbackGeneration;
        if (_isQuitting || !_settings.ShowPlaybackControls || _transport is null || _transport.Info.Origin == DataOrigin.Demo)
        {
            if (!_isQuitting && !_settings.ShowPlaybackControls)
                LocalLog.Write($"Manual Windows playback controls off (setting is disabled; transport origin {_transport?.Info.Origin.ToString() ?? "unavailable"}).");
            var previous = _playbackViewModel;
            _playbackViewModel = null;
            _flyout?.SetPlaybackViewModel(null);
            _settingsWindow?.SetPlaybackViewModel(null);
            if (previous is not null) await previous.DisposeAsync();
            return;
        }
        if (_playbackViewModel is not null) return;

        IPlaybackController? controller = null;
        try
        {
            controller = await WindowsPlaybackController.TryCreateAsync(diagnosticLog: LocalLog.Write);
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Windows media controls unavailable ({ex.GetType().Name}).");
        }

        if (_isQuitting || generation != _playbackGeneration || !_settings.ShowPlaybackControls
            || _transport.Info.Origin == DataOrigin.Demo || _playbackViewModel is not null)
        {
            if (controller is not null) await controller.DisposeAsync();
            return;
        }

        var queue = _queue;
        _playbackViewModel = new PlaybackViewModel(
            controller,
            action => queue?.TryEnqueue(() => action()),
            beforeManualIntent: () => _host?.ForgetEarMedia());
        _flyout?.SetPlaybackViewModel(_playbackViewModel);
        _settingsWindow?.SetPlaybackViewModel(_playbackViewModel);
        if (controller is null) LocalLog.Write("Windows media controls are unavailable in this session.");
    }

    private void ApplyTheme()
    {
        var theme = WindowChrome.ToElementTheme(_settings.Theme);
        _flyout?.SetTheme(theme);
        _settingsWindow?.SetTheme(theme);
        _welcomeWindow?.SetTheme(theme);
        _host?.SetTheme(theme);
    }

    private async void Quit()
    {
        if (_isQuitting) return;
        _isQuitting = true;
        Design.Motion.IsShuttingDown = true; // no new composition animations while everything unloads
        LocalLog.Write("Quit requested.");
        _updateTimer?.Stop();
        _updateTimer = null;
        // The companion features were created last (they use the tray, flyout, connection and audio
        // view models), so they go first.
        if (_host is not null)
        {
            try { await _host.DisposeAsync(); }
            catch (Exception ex) { LocalLog.Write($"Companion features stopped with an error ({ex.GetType().Name})."); }
            _host = null;
        }
        _tray?.Dispose();
        _tray = null;
        _nearbyPromptTimer?.Stop();
        _nearbyPromptTimer = null;
        if (_broadcastTransport is not null) _broadcastTransport.SourcesChanged -= OnNearbySourcesChanged;
        if (_audioOutput is not null) _audioOutput.PropertyChanged -= OnAudioOutputPropertyChanged;
        _pairingDiscovery?.Dispose();
        _pairingDiscovery = null;
        _connectionViewModel?.Dispose();
        _connectionViewModel = null;
        _connectionController?.Dispose();
        _connectionController = null;
        _playbackGeneration++;
        if (_flyout?.IsOpen == true) await _flyout.HideAsync();
        if (_viewModel?.IsObserving == true)
        {
            try { await _viewModel.DisconnectAsync(); }
            catch (Exception ex) { LocalLog.Write($"Nearby observation stop failed during quit ({ex.GetType().Name})."); }
        }
        _flyout?.SetAudioOutputViewModel(null);
        _audioOutput?.Dispose();
        _audioOutput = null;
        _audioController = null;
        var playback = _playbackViewModel;
        _playbackViewModel = null;
        if (playback is not null) await playback.DisposeAsync();
        if (_viewModel is not null) await _viewModel.DisposeAsync();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        _singleInstance = null;
        LocalLog.Write("Quit: cleanup finished; exiting.");
        _quitCleanupDone = true;
        Exit();
        // The windows are kept for the whole session and cancel their own close (they hide instead,
        // see SettingsWindow/FlyoutWindow), which can leave a hidden window holding the process open
        // after Exit(). Everything is already stopped and saved here, so end the process if it is
        // still alive shortly after: an update installer waits for Clasp to exit before replacing it.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Environment.Exit(0), TaskScheduler.Default);
    }
}
