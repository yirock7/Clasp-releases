using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Commands;
using AppleDeviceHub.Core.Media;
using AppleDeviceHub.Core.Storage;
using AppleDeviceHub.Core.Updates;

namespace AppleDeviceHub.Core.Presentation;

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// User preferences. <see cref="ReduceMotion"/> and <see cref="ReduceTransparency"/> are
/// app-level overrides: when off, the app follows the Windows settings; when on, the app
/// reduces effects regardless. They also let a reviewer test the fallbacks without changing
/// any Windows setting.
/// </summary>
public sealed class HubSettings : ObservableObject
{
    /// <summary>Volume the "set volume on connect" slider starts at when first switched on (as MagicPods).</summary>
    public const int DefaultConnectVolumePercent = 25;

    /// <summary>Low-battery notification level used until the user chooses another (0 = off).</summary>
    public const int DefaultLowBatteryThresholdPercent = 20;

    private AppTheme _theme;
    private bool _reduceMotion;
    private bool _reduceTransparency;
    private bool _autoShowCaseCard = true;
    private int _caseCardSeconds = 6;
    private bool _pauseMediaOnEarRemoval;
    private bool _showPlaybackControls = true;
    private bool _restorePreviousAudioOutput;
    private string _audioTargetEndpointId = "";
    private string _audioTargetContainerId = "";
    private string _audioFallbackEndpointId = "";
    private string _airPodsDeviceId = "";
    private bool _observeNearbyAirPods = true;
    private bool _nearbyDefaultOnApplied = true;
    private bool _welcomeCompleted;
    private bool _autoConnectAirPods;
    private bool _autoPlayOnConnect;
    private bool _autoPauseOnDisconnect;
    private int? _defaultVolumeOnConnect;
    private bool _switchOutputToAirPodsOnConnect;
    private bool _broadcastEarDetection;
    private string _earRemovedOutputEndpointId = "";
    private HandsfreeMode _handsfreeMode = HandsfreeMode.AlwaysOn;
    private bool _lowAudioLatency;
    private bool _handoffNoticeEnabled = true;
    private int _lowBatteryThresholdPercent = DefaultLowBatteryThresholdPercent;
    private bool _showTrayBatteryPercentage = true;
    private bool _dimTrayIconWhenAudioElsewhere = true;
    private bool _allowClaspLinks;
    private readonly Dictionary<ClaspCommand, string> _hotkeys = new();
    private bool _stemActionsEnabled;
    private StemAction _stemPlayPauseAction = StemAction.PassThrough;
    private StemAction _stemNextAction = StemAction.PassThrough;
    private StemAction _stemPreviousAction = StemAction.PassThrough;
    private bool _perAppOutputEnabled;
    private string _perAppRulesText = "";
    private string _phoneAudioDeviceId = "";
    private Dictionary<string, string> _artworkColorways = new(StringComparer.Ordinal);
    private UpdateCheckFrequency _updateCheckFrequency = UpdateCheckFrequency.NotAsked;
    private DateTimeOffset? _lastUpdateCheckAt;

    // ----- Updates -----

    /// <summary>How often Clasp checks GitHub for a new release. Not asked until the user answers the one-time question.</summary>
    public UpdateCheckFrequency UpdateCheckFrequency
    {
        get => _updateCheckFrequency;
        set
        {
            var defined = Enum.IsDefined(value) ? value : UpdateCheckFrequency.NotAsked;
            if (Set(ref _updateCheckFrequency, defined)) Raise(nameof(UpdateCheckFrequencyIndex));
        }
    }

    /// <summary>Only when I click, Daily, Weekly, Monthly as 0..3 (ComboBox SelectedIndex). Not asked shows as 0.</summary>
    public int UpdateCheckFrequencyIndex
    {
        get => _updateCheckFrequency switch
        {
            UpdateCheckFrequency.Daily => 1,
            UpdateCheckFrequency.Weekly => 2,
            UpdateCheckFrequency.Monthly => 3,
            _ => 0,
        };
        set
        {
            // The ComboBox writes its first index back when it loads; that must not answer the question.
            if (value < 0 || value == UpdateCheckFrequencyIndex) return;
            UpdateCheckFrequency = value switch
            {
                1 => UpdateCheckFrequency.Daily,
                2 => UpdateCheckFrequency.Weekly,
                3 => UpdateCheckFrequency.Monthly,
                _ => UpdateCheckFrequency.Manual,
            };
        }
    }

    /// <summary>When Clasp last reached GitHub for an update check (automatic or manual).</summary>
    public DateTimeOffset? LastUpdateCheckAt { get => _lastUpdateCheckAt; set => Set(ref _lastUpdateCheckAt, value); }

    /// <summary>Opt-in Windows output restoration. Requires an explicitly selected audio device.</summary>
    public bool RestorePreviousAudioOutput { get => _restorePreviousAudioOutput; set => Set(ref _restorePreviousAudioOutput, value); }
    /// <summary>Show manual Windows media controls in the Clasp flyout. On by default.</summary>
    public bool ShowPlaybackControls { get => _showPlaybackControls; set => Set(ref _showPlaybackControls, value); }
    public string AudioTargetEndpointId { get => _audioTargetEndpointId; set => Set(ref _audioTargetEndpointId, value ?? ""); }
    public string AudioTargetContainerId { get => _audioTargetContainerId; set => Set(ref _audioTargetContainerId, value ?? ""); }
    public string AudioFallbackEndpointId { get => _audioFallbackEndpointId; set => Set(ref _audioFallbackEndpointId, value ?? ""); }

    /// <summary>The explicitly selected paired Bluetooth identity. It is stored locally and never logged.</summary>
    public string AirPodsDeviceId { get => _airPodsDeviceId; set => Set(ref _airPodsDeviceId, value ?? ""); }

    /// <summary>Passive nearby AirPods observation. On by default; the user can turn it off in Settings.</summary>
    public bool ObserveNearbyAirPods { get => _observeNearbyAirPods; set => Set(ref _observeNearbyAirPods, value); }

    /// <summary>
    /// Saved so the one-time "nearby detection is now on by default" change is applied once. A settings file written
    /// before that change holds an explicit false the owner never chose; the first load flips it to true and sets this,
    /// and from then on a false the owner chose is kept. Always true for new and migrated settings.
    /// </summary>
    /// <summary>
    /// The welcome guide has been finished or skipped. False only for a brand-new install: a settings file written by
    /// an older version has no such key and loads as true, so upgrading users never get the guide unasked.
    /// </summary>
    public bool WelcomeCompleted { get => _welcomeCompleted; set => Set(ref _welcomeCompleted, value); }

    public bool NearbyDefaultOnApplied { get => _nearbyDefaultOnApplied; set => Set(ref _nearbyDefaultOnApplied, value); }

    /// <summary>Opt in to one reconnect attempt for a fresh nearby episode. Off by default.</summary>
    public bool AutoConnectAirPods { get => _autoConnectAirPods; set => Set(ref _autoConnectAirPods, value); }

    // ----- Connection-triggered actions (all off by default) -----

    /// <summary>Press Play shortly after the AirPods connect and are the sound output. Off by default.</summary>
    public bool AutoPlayOnConnect { get => _autoPlayOnConnect; set => Set(ref _autoPlayOnConnect, value); }

    /// <summary>Pause playing media when the AirPods disconnect. Off by default.</summary>
    public bool AutoPauseOnDisconnect { get => _autoPauseOnDisconnect; set => Set(ref _autoPauseOnDisconnect, value); }

    /// <summary>Volume (0..100) to set on each connect, or null to leave the volume alone (default).</summary>
    public int? DefaultVolumeOnConnect
    {
        get => _defaultVolumeOnConnect;
        set
        {
            int? clamped = value is int v ? Math.Clamp(v, 0, 100) : null;
            if (!Set(ref _defaultVolumeOnConnect, clamped)) return;
            Raise(nameof(DefaultVolumeEnabled));
            Raise(nameof(DefaultVolumeSliderValue));
        }
    }

    /// <summary>Toggle view of <see cref="DefaultVolumeOnConnect"/>: on starts at 25, off clears the value.</summary>
    public bool DefaultVolumeEnabled
    {
        get => _defaultVolumeOnConnect is not null;
        set => DefaultVolumeOnConnect = value ? _defaultVolumeOnConnect ?? DefaultConnectVolumePercent : null;
    }

    /// <summary>Slider view of <see cref="DefaultVolumeOnConnect"/> (shows 25 while the option is off).</summary>
    public double DefaultVolumeSliderValue
    {
        get => _defaultVolumeOnConnect ?? DefaultConnectVolumePercent;
        set
        {
            // Dragging the slider while the option is off must not switch it on by itself.
            if (_defaultVolumeOnConnect is not null) DefaultVolumeOnConnect = (int)Math.Round(value);
        }
    }

    /// <summary>Make the AirPods the Windows sound output on connect. Off by default.</summary>
    public bool SwitchOutputToAirPodsOnConnect { get => _switchOutputToAirPodsOnConnect; set => Set(ref _switchOutputToAirPodsOnConnect, value); }

    // ----- Ear detection from nearby broadcasts (opt-in) -----

    /// <summary>
    /// Opt in to ear detection from the nearby broadcast (slow or unreliable on H2 AirPods). Off by
    /// default. Only has an effect together with the pause/resume preference or the output switch below.
    /// </summary>
    public bool BroadcastEarDetection { get => _broadcastEarDetection; set => Set(ref _broadcastEarDetection, value); }

    /// <summary>Windows output to play through while both AirPods are out of the ears; "" = off.</summary>
    public string EarRemovedOutputEndpointId { get => _earRemovedOutputEndpointId; set => Set(ref _earRemovedOutputEndpointId, value ?? ""); }

    // ----- Audio link -----

    /// <summary>Always on (Windows default; Clasp changes nothing), Always off, or Smart.</summary>
    public HandsfreeMode HandsfreeMode
    {
        get => _handsfreeMode;
        set
        {
            var clamped = Enum.IsDefined(value) ? value : HandsfreeMode.AlwaysOn;
            if (Set(ref _handsfreeMode, clamped)) Raise(nameof(HandsfreeModeIndex));
        }
    }

    /// <summary>AlwaysOn, AlwaysOff, Smart as 0, 1, 2 (for ComboBox SelectedIndex).</summary>
    public int HandsfreeModeIndex
    {
        get => (int)_handsfreeMode;
        set { if (value >= 0) HandsfreeMode = (HandsfreeMode)value; }
    }

    /// <summary>Keep the AirPods audio link awake with inaudible silence. Off by default.</summary>
    public bool LowAudioLatency { get => _lowAudioLatency; set => Set(ref _lowAudioLatency, value); }

    // ----- Notices and tray -----

    /// <summary>Tell the user when the AirPods probably moved to another device. On by default (needs nearby detection).</summary>
    public bool HandoffNoticeEnabled { get => _handoffNoticeEnabled; set => Set(ref _handoffNoticeEnabled, value); }

    /// <summary>Show a Windows notification when an earbud falls to this level (10, 20, 30); 0 = off.</summary>
    public int LowBatteryThresholdPercent
    {
        get => _lowBatteryThresholdPercent;
        set
        {
            if (Set(ref _lowBatteryThresholdPercent, Math.Clamp(value, 0, 100))) Raise(nameof(LowBatteryThresholdIndex));
        }
    }

    /// <summary>Off, 10, 20, 30 percent as 0..3 (for ComboBox SelectedIndex). Other stored values show as the nearest choice.</summary>
    public int LowBatteryThresholdIndex
    {
        get => _lowBatteryThresholdPercent switch { <= 0 => 0, <= 10 => 1, <= 20 => 2, _ => 3 };
        set
        {
            if (value < 0) return; // a ComboBox briefly reports -1 while its list changes
            LowBatteryThresholdPercent = value switch { 1 => 10, 2 => 20, 3 => 30, _ => 0 };
        }
    }

    /// <summary>Draw the average earbud battery on the tray icon. On by default.</summary>
    public bool ShowTrayBatteryPercentage { get => _showTrayBatteryPercentage; set => Set(ref _showTrayBatteryPercentage, value); }

    /// <summary>Dim the tray icon when Windows plays sound somewhere other than the AirPods. On by default.</summary>
    public bool DimTrayIconWhenAudioElsewhere { get => _dimTrayIconWhenAudioElsewhere; set => Set(ref _dimTrayIconWhenAudioElsewhere, value); }

    // ----- Links and shortcuts (off / unassigned by default) -----

    /// <summary>Allow clasp:// links to control the running app. Off by default; turning it on registers the scheme.</summary>
    public bool AllowClaspLinks { get => _allowClaspLinks; set => Set(ref _allowClaspLinks, value); }

    /// <summary>The saved shortcut text for a command ("" = unassigned), in <c>HotkeyBinding.ToString()</c> form.</summary>
    public string GetHotkey(ClaspCommand command) => _hotkeys.TryGetValue(command, out var text) ? text : "";

    /// <summary>Saves a shortcut text; the caller validates it first. Raises the matching <c>Hotkey...</c> property.</summary>
    public void SetHotkey(ClaspCommand command, string? text)
    {
        var value = text ?? "";
        if (string.Equals(GetHotkey(command), value, StringComparison.Ordinal)) return;
        _hotkeys[command] = value;
        Raise(HotkeyPropertyName(command));
    }

    public string HotkeyConnect { get => GetHotkey(ClaspCommand.Connect); set => SetHotkey(ClaspCommand.Connect, value); }
    public string HotkeyDisconnect { get => GetHotkey(ClaspCommand.Disconnect); set => SetHotkey(ClaspCommand.Disconnect, value); }
    public string HotkeyOutputAirPods { get => GetHotkey(ClaspCommand.OutputAirPods); set => SetHotkey(ClaspCommand.OutputAirPods, value); }
    public string HotkeyOutputPrevious { get => GetHotkey(ClaspCommand.OutputPrevious); set => SetHotkey(ClaspCommand.OutputPrevious, value); }
    public string HotkeyFixConnection { get => GetHotkey(ClaspCommand.FixConnection); set => SetHotkey(ClaspCommand.FixConnection, value); }
    public string HotkeyShowFlyout { get => GetHotkey(ClaspCommand.ShowFlyout); set => SetHotkey(ClaspCommand.ShowFlyout, value); }
    public string HotkeyShowSettings { get => GetHotkey(ClaspCommand.ShowSettings); set => SetHotkey(ClaspCommand.ShowSettings, value); }
    public string HotkeyPlayPause { get => GetHotkey(ClaspCommand.PlayPause); set => SetHotkey(ClaspCommand.PlayPause, value); }

    /// <summary>The property name raised when the shortcut of <paramref name="command"/> changes.</summary>
    public static string HotkeyPropertyName(ClaspCommand command) => command switch
    {
        ClaspCommand.Connect => nameof(HotkeyConnect),
        ClaspCommand.Disconnect => nameof(HotkeyDisconnect),
        ClaspCommand.OutputAirPods => nameof(HotkeyOutputAirPods),
        ClaspCommand.OutputPrevious => nameof(HotkeyOutputPrevious),
        ClaspCommand.FixConnection => nameof(HotkeyFixConnection),
        ClaspCommand.ShowFlyout => nameof(HotkeyShowFlyout),
        ClaspCommand.ShowSettings => nameof(HotkeyShowSettings),
        ClaspCommand.PlayPause => nameof(HotkeyPlayPause),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>True when <paramref name="propertyName"/> is one of the shortcut properties.</summary>
    public static bool IsHotkeyProperty(string? propertyName)
        => propertyName is not null && Enum.GetValues<ClaspCommand>().Any(command => HotkeyPropertyName(command) == propertyName);

    // ----- Experimental -----

    /// <summary>Remap AirPods stem presses (they arrive as media keys). Off by default.</summary>
    public bool StemActionsEnabled { get => _stemActionsEnabled; set => Set(ref _stemActionsEnabled, value); }

    public StemAction StemPlayPauseAction
    {
        get => _stemPlayPauseAction;
        set { if (Set(ref _stemPlayPauseAction, Defined(value))) Raise(nameof(StemPlayPauseActionIndex)); }
    }

    public StemAction StemNextAction
    {
        get => _stemNextAction;
        set { if (Set(ref _stemNextAction, Defined(value))) Raise(nameof(StemNextActionIndex)); }
    }

    public StemAction StemPreviousAction
    {
        get => _stemPreviousAction;
        set { if (Set(ref _stemPreviousAction, Defined(value))) Raise(nameof(StemPreviousActionIndex)); }
    }

    /// <summary>Normal, Mute microphone, Play/pause, Next, Previous, Do nothing as 0..5 (ComboBox SelectedIndex).</summary>
    public int StemPlayPauseActionIndex
    {
        get => (int)_stemPlayPauseAction;
        set { if (value >= 0) StemPlayPauseAction = (StemAction)value; }
    }

    public int StemNextActionIndex
    {
        get => (int)_stemNextAction;
        set { if (value >= 0) StemNextAction = (StemAction)value; }
    }

    public int StemPreviousActionIndex
    {
        get => (int)_stemPreviousAction;
        set { if (value >= 0) StemPreviousAction = (StemAction)value; }
    }

    private static StemAction Defined(StemAction value) => Enum.IsDefined(value) ? value : StemAction.PassThrough;

    /// <summary>Route specific apps to specific outputs through an undocumented Windows interface. Off by default.</summary>
    public bool PerAppOutputEnabled { get => _perAppOutputEnabled; set => Set(ref _perAppOutputEnabled, value); }

    /// <summary>One rule per line, for example <c>spotify.exe=airpods</c> (see <c>PerAppRuleText</c>).</summary>
    public string PerAppRulesText { get => _perAppRulesText; set => Set(ref _perAppRulesText, value ?? ""); }

    // ----- Phone audio -----

    /// <summary>The phone last picked for audio streaming; only used to preselect it.</summary>
    public string PhoneAudioDeviceId { get => _phoneAudioDeviceId; set => Set(ref _phoneAudioDeviceId, value ?? ""); }

    /// <summary>Most colourway choices kept; beyond this the oldest-inserted are dropped.</summary>
    public const int MaxArtworkColorways = 32;

    /// <summary>Saved manual artwork colourways, keyed by device and model (device part "*" is model-wide), oldest first.</summary>
    public IReadOnlyDictionary<string, string> ArtworkColorways => _artworkColorways;

    /// <summary>The colourway chosen for this device and model, else the model-wide choice, else null (the drawing's first colourway).</summary>
    public string? GetArtworkColorway(string? deviceId, ushort? modelId)
    {
        if (modelId is not ushort id) return null;
        if (!string.IsNullOrWhiteSpace(deviceId)
            && _artworkColorways.TryGetValue(ArtworkColorwayKey(deviceId, id), out var specific)) return specific;
        return _artworkColorways.TryGetValue(ArtworkColorwayKey(null, id), out var wide) ? wide : null;
    }

    /// <summary>
    /// Stores a colourway id under the device and model key (when a device id is known) and the model-wide key, or clears:
    /// a null or empty id removes the device-specific entry, and the model-wide one too when no device id is given.
    /// </summary>
    public void SetArtworkColorway(string? deviceId, ushort? modelId, string? colorwayId)
    {
        if (modelId is not ushort id) return;
        var hasDevice = !string.IsNullOrWhiteSpace(deviceId);
        var next = new Dictionary<string, string>(_artworkColorways, StringComparer.Ordinal);
        var changed = false;
        if (string.IsNullOrWhiteSpace(colorwayId))
        {
            if (hasDevice) changed |= next.Remove(ArtworkColorwayKey(deviceId, id));
            else changed |= next.Remove(ArtworkColorwayKey(null, id));
        }
        else
        {
            if (hasDevice) changed |= PutArtworkColorway(next, ArtworkColorwayKey(deviceId, id), colorwayId);
            changed |= PutArtworkColorway(next, ArtworkColorwayKey(null, id), colorwayId);
        }

        if (!changed) return;
        _artworkColorways = CapArtworkColorways(next);
        Raise(nameof(ArtworkColorways));
    }

    // A changed value moves its entry to the end (newest); an identical value is left alone.
    private static bool PutArtworkColorway(Dictionary<string, string> map, string key, string value)
    {
        if (map.TryGetValue(key, out var current) && string.Equals(current, value, StringComparison.Ordinal)) return false;
        map.Remove(key);
        map[key] = value;
        return true;
    }

    // Rebuilt in order with no removals, so enumeration order is insertion order and the oldest entries are the first ones.
    private static Dictionary<string, string> CapArtworkColorways(IEnumerable<KeyValuePair<string, string>> ordered)
    {
        var all = ordered.ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in all.Skip(Math.Max(0, all.Count - MaxArtworkColorways))) result[key] = value;
        return result;
    }

    private static string ArtworkColorwayKey(string? deviceId, ushort modelId)
        => $"{(string.IsNullOrWhiteSpace(deviceId) ? "*" : Uri.EscapeDataString(deviceId))}|{modelId.ToString("X4", CultureInfo.InvariantCulture)}";

    internal void LoadArtworkColorways(IReadOnlyDictionary<string, string>? colorways)
    {
        _artworkColorways = new Dictionary<string, string>(StringComparer.Ordinal);
        if (colorways is null) return;
        _artworkColorways = CapArtworkColorways(colorways.Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value)));
    }

    public AppTheme Theme
    {
        get => _theme;
        set { if (Set(ref _theme, value)) Raise(nameof(ThemeIndex)); }
    }

    /// <summary>System, Light, Dark as 0, 1, 2 (for RadioButtons/ComboBox SelectedIndex).</summary>
    public int ThemeIndex
    {
        get => (int)_theme;
        set => Theme = (AppTheme)Math.Clamp(value, 0, 2);
    }

    public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }
    public bool ReduceTransparency { get => _reduceTransparency; set => Set(ref _reduceTransparency, value); }
    public bool AutoShowCaseCard { get => _autoShowCaseCard; set => Set(ref _autoShowCaseCard, value); }

    /// <summary>
    /// Opt-in: pause the current media session when the AirPods are taken out, and resume it
    /// when they go back in, only if Clasp paused it. Needs ear placement from a live control
    /// session (the AAP driver), or from the nearby broadcast when <see cref="BroadcastEarDetection"/>
    /// is also on and the broadcast is identified as the selected AirPods.
    /// </summary>
    public bool PauseMediaOnEarRemoval { get => _pauseMediaOnEarRemoval; set => Set(ref _pauseMediaOnEarRemoval, value); }

    /// <summary>How long the case card stays before dismissing itself (3..15 seconds).</summary>
    public int CaseCardSeconds
    {
        get => _caseCardSeconds;
        set => Set(ref _caseCardSeconds, Math.Clamp(value, 3, 15));
    }
}

/// <summary>Loads and saves <see cref="HubSettings"/> as JSON. Never throws: bad files yield defaults.</summary>
public static partial class HubSettingsStore
{
    // Every field after CaseCardSeconds has a default, so an older settings.json that lacks it
    // loads with exactly the shipped default (for example HandoffNoticeEnabled stays true).
    private sealed record Dto(AppTheme Theme, bool ReduceMotion, bool ReduceTransparency, bool AutoShowCaseCard, int CaseCardSeconds, bool PauseMediaOnEarRemoval = false,
        bool RestorePreviousAudioOutput = false, string AudioTargetEndpointId = "", string AudioTargetContainerId = "", string AudioFallbackEndpointId = "",
        bool? ShowPlaybackControls = null, string AirPodsDeviceId = "", bool ObserveNearbyAirPods = true, bool AutoConnectAirPods = false,
        bool AutoPlayOnConnect = false, bool AutoPauseOnDisconnect = false, int? DefaultVolumeOnConnect = null, bool SwitchOutputToAirPodsOnConnect = false,
        bool BroadcastEarDetection = false, string? EarRemovedOutputEndpointId = "", string? HandsfreeMode = null, bool LowAudioLatency = false,
        bool HandoffNoticeEnabled = true, int LowBatteryThresholdPercent = HubSettings.DefaultLowBatteryThresholdPercent,
        bool ShowTrayBatteryPercentage = true, bool DimTrayIconWhenAudioElsewhere = true,
        bool AllowClaspLinks = false, string? HotkeyConnect = "", string? HotkeyDisconnect = "", string? HotkeyOutputAirPods = "", string? HotkeyOutputPrevious = "",
        string? HotkeyFixConnection = "", string? HotkeyShowFlyout = "", string? HotkeyShowSettings = "", string? HotkeyPlayPause = "",
        bool StemActionsEnabled = false, string? StemPlayPauseAction = null, string? StemNextAction = null, string? StemPreviousAction = null,
        bool PerAppOutputEnabled = false, string? PerAppRulesText = "", string? PhoneAudioDeviceId = "",
        string? UpdateCheckFrequency = null, DateTimeOffset? LastUpdateCheckAt = null,
        bool NearbyDefaultOnApplied = false, bool? WelcomeCompleted = null,
        Dictionary<string, string>? ArtworkColorways = null);

    /// <summary>Compile-time JSON metadata for <see cref="Dto"/>, so settings still load and save when the
    /// published app is trimmed (reflection-based serialization is switched off in trimmed builds).</summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Dto))]
    private sealed partial class SettingsJson : JsonSerializerContext;

    public static string DefaultPath => Path.Combine(AppDataFolder.DefaultRoot, "settings.json");

    /// <summary>
    /// Loads <paramref name="path"/>. A missing or unreadable file falls back to <c>path + ".bak"</c> (the copy
    /// <see cref="Save"/> keeps), and an unreadable file is first kept as <c>settings.corrupt-*.json</c> so it is never lost.
    /// </summary>
    public static HubSettings Load(string path, Action<string>? log = null)
    {
        var backupPath = path + ".bak";
        if (!File.Exists(path))
        {
            var restored = TryRead(backupPath);
            if (restored is null) return new HubSettings();
            Log(log, "Settings restored from backup (settings.json was missing).");
            return restored;
        }

        var settings = TryRead(path, out var tooLarge);
        if (settings is not null) return settings;

        // Exists but cannot be read: keep it for the owner before anything can overwrite it. A file over the
        // size limit is moved aside instead of copied (copying it would just duplicate a huge file).
        var keptName = tooLarge ? BoundedFile.MoveAside(path) : PreserveCorrupt(path);
        var kept = keptName is null ? "" : tooLarge
            ? $" The oversized file was moved aside as {keptName}."
            : $" The unreadable file was kept as {keptName}.";
        var fromBackup = TryRead(backupPath);
        if (fromBackup is not null)
        {
            Log(log, $"Settings restored from backup (settings.json was unreadable).{kept}");
            return fromBackup;
        }
        Log(log, $"Settings could not be read; starting with defaults.{kept}");
        // A file existed, so this is not a new install: never show the welcome guide over it.
        return new HubSettings { WelcomeCompleted = true };
    }

    /// <summary>Reads one settings file. Null when it is missing, unreadable or not valid settings JSON; never throws.</summary>
    private static HubSettings? TryRead(string path) => TryRead(path, out _);

    /// <summary>Same, but says whether the file was refused for being over <see cref="BoundedFile.SettingsMaxBytes"/>.</summary>
    private static HubSettings? TryRead(string path, out bool tooLarge)
    {
        tooLarge = false;
        try
        {
            var status = BoundedFile.ReadText(path, BoundedFile.SettingsMaxBytes, out var text);
            if (status == BoundedReadStatus.Missing) return null;
            if (status == BoundedReadStatus.TooLarge) { tooLarge = true; return null; }
            var dto = JsonSerializer.Deserialize(text!, SettingsJson.Default.Dto);
            return dto is null ? null : FromDto(dto);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static HubSettings FromDto(Dto dto)
    {
        var settings = new HubSettings();
            settings.Theme = Enum.IsDefined(dto.Theme) ? dto.Theme : AppTheme.System;
            settings.ReduceMotion = dto.ReduceMotion;
            settings.ReduceTransparency = dto.ReduceTransparency;
            settings.AutoShowCaseCard = dto.AutoShowCaseCard;
            settings.CaseCardSeconds = dto.CaseCardSeconds;
            settings.PauseMediaOnEarRemoval = dto.PauseMediaOnEarRemoval;
            settings.ShowPlaybackControls = dto.ShowPlaybackControls ?? true;
            settings.RestorePreviousAudioOutput = dto.RestorePreviousAudioOutput;
            settings.AudioTargetEndpointId = dto.AudioTargetEndpointId;
            settings.AudioTargetContainerId = dto.AudioTargetContainerId;
            settings.AudioFallbackEndpointId = dto.AudioFallbackEndpointId;
            settings.AirPodsDeviceId = dto.AirPodsDeviceId;
            // A file from before nearby detection became on-by-default has no marker: its false was never a choice.
            settings.ObserveNearbyAirPods = dto.NearbyDefaultOnApplied ? dto.ObserveNearbyAirPods : true;
            settings.NearbyDefaultOnApplied = true;
            settings.AutoConnectAirPods = dto.AutoConnectAirPods;
            settings.AutoPlayOnConnect = dto.AutoPlayOnConnect;
            settings.AutoPauseOnDisconnect = dto.AutoPauseOnDisconnect;
            settings.DefaultVolumeOnConnect = dto.DefaultVolumeOnConnect;
            settings.SwitchOutputToAirPodsOnConnect = dto.SwitchOutputToAirPodsOnConnect;
            settings.BroadcastEarDetection = dto.BroadcastEarDetection;
            settings.EarRemovedOutputEndpointId = dto.EarRemovedOutputEndpointId ?? "";
            settings.HandsfreeMode = ParseEnum(dto.HandsfreeMode, HandsfreeMode.AlwaysOn);
            settings.LowAudioLatency = dto.LowAudioLatency;
            settings.HandoffNoticeEnabled = dto.HandoffNoticeEnabled;
            settings.LowBatteryThresholdPercent = dto.LowBatteryThresholdPercent;
            settings.ShowTrayBatteryPercentage = dto.ShowTrayBatteryPercentage;
            settings.DimTrayIconWhenAudioElsewhere = dto.DimTrayIconWhenAudioElsewhere;
            settings.AllowClaspLinks = dto.AllowClaspLinks;
            settings.HotkeyConnect = dto.HotkeyConnect ?? "";
            settings.HotkeyDisconnect = dto.HotkeyDisconnect ?? "";
            settings.HotkeyOutputAirPods = dto.HotkeyOutputAirPods ?? "";
            settings.HotkeyOutputPrevious = dto.HotkeyOutputPrevious ?? "";
            settings.HotkeyFixConnection = dto.HotkeyFixConnection ?? "";
            settings.HotkeyShowFlyout = dto.HotkeyShowFlyout ?? "";
            settings.HotkeyShowSettings = dto.HotkeyShowSettings ?? "";
            settings.HotkeyPlayPause = dto.HotkeyPlayPause ?? "";
            settings.StemActionsEnabled = dto.StemActionsEnabled;
            settings.StemPlayPauseAction = ParseEnum(dto.StemPlayPauseAction, StemAction.PassThrough);
            settings.StemNextAction = ParseEnum(dto.StemNextAction, StemAction.PassThrough);
            settings.StemPreviousAction = ParseEnum(dto.StemPreviousAction, StemAction.PassThrough);
            settings.PerAppOutputEnabled = dto.PerAppOutputEnabled;
            settings.PerAppRulesText = dto.PerAppRulesText ?? "";
            settings.PhoneAudioDeviceId = dto.PhoneAudioDeviceId ?? "";
            settings.LoadArtworkColorways(dto.ArtworkColorways);
            settings.UpdateCheckFrequency = ParseEnum(dto.UpdateCheckFrequency, UpdateCheckFrequency.NotAsked);
            settings.LastUpdateCheckAt = dto.LastUpdateCheckAt;
            // A file with no WelcomeCompleted key was written by an older version: the guide counts as already seen.
            settings.WelcomeCompleted = dto.WelcomeCompleted ?? true;
        return settings;
    }

    /// <summary>Copies an unreadable settings file to <c>settings.corrupt-yyyyMMdd-HHmmss.json</c> beside it. Returns that name, or null if the copy failed.</summary>
    private static string? PreserveCorrupt(string path)
    {
        try
        {
            var name = BoundedFile.AsideName(path);
            var folder = Path.GetDirectoryName(path) ?? "";
            File.Copy(path, Path.Combine(folder, name), overwrite: true);
            TrimCorruptBackups(folder, path);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Most timestamped corrupt-settings copies kept; older ones are deleted so a bad file cannot fill the disk over time.</summary>
    public const int MaxCorruptBackups = 3;

    /// <summary>Deletes all but the newest <see cref="MaxCorruptBackups"/> timestamped copies. Best effort.</summary>
    internal static void TrimCorruptBackups(string folder, string settingsPath)
        => BoundedFile.TrimKeptAside(folder, settingsPath, MaxCorruptBackups);

    /// <summary>A log callback must never be able to break loading.</summary>
    private static void Log(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch (Exception) { }
    }

    /// <summary>Enums are stored by name so a reordered enum never changes what a saved file means; unknown text gives the default.</summary>
    private static T ParseEnum<T>(string? text, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    /// <returns>False when the file could not be written (the app keeps running with in-memory settings).</returns>
    public static bool Save(HubSettings settings, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dto = new Dto(settings.Theme, settings.ReduceMotion, settings.ReduceTransparency,
                settings.AutoShowCaseCard, settings.CaseCardSeconds, settings.PauseMediaOnEarRemoval,
                settings.RestorePreviousAudioOutput, settings.AudioTargetEndpointId, settings.AudioTargetContainerId, settings.AudioFallbackEndpointId,
                settings.ShowPlaybackControls, settings.AirPodsDeviceId, settings.ObserveNearbyAirPods, settings.AutoConnectAirPods,
                settings.AutoPlayOnConnect, settings.AutoPauseOnDisconnect, settings.DefaultVolumeOnConnect, settings.SwitchOutputToAirPodsOnConnect,
                settings.BroadcastEarDetection, settings.EarRemovedOutputEndpointId, settings.HandsfreeMode.ToString(), settings.LowAudioLatency,
                settings.HandoffNoticeEnabled, settings.LowBatteryThresholdPercent,
                settings.ShowTrayBatteryPercentage, settings.DimTrayIconWhenAudioElsewhere,
                settings.AllowClaspLinks, settings.HotkeyConnect, settings.HotkeyDisconnect, settings.HotkeyOutputAirPods, settings.HotkeyOutputPrevious,
                settings.HotkeyFixConnection, settings.HotkeyShowFlyout, settings.HotkeyShowSettings, settings.HotkeyPlayPause,
                settings.StemActionsEnabled, settings.StemPlayPauseAction.ToString(), settings.StemNextAction.ToString(), settings.StemPreviousAction.ToString(),
                settings.PerAppOutputEnabled, settings.PerAppRulesText, settings.PhoneAudioDeviceId,
                settings.UpdateCheckFrequency.ToString(), settings.LastUpdateCheckAt,
                settings.NearbyDefaultOnApplied, settings.WelcomeCompleted,
                new Dictionary<string, string>(settings.ArtworkColorways, StringComparer.Ordinal));
            KeepBackup(path);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(dto, SettingsJson.Default.Dto));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the current settings.json to settings.json.bak before it is replaced. Only a file that still reads
    /// as valid settings is copied, so a corrupt file never overwrites a good backup. A failure here never stops the save.
    /// </summary>
    private static void KeepBackup(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
            if (TryRead(path) is null) return;
            File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No backup this time; the save itself still goes ahead.
        }
    }
}
