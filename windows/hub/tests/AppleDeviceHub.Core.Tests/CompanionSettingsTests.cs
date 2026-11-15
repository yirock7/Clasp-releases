using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Commands;
using AppleDeviceHub.Core.Media;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

/// <summary>The companion-feature settings: shipped defaults, old-file loading and round trips.</summary>
public sealed class CompanionSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "clasp-settings-" + Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_folder, name);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Shipped_defaults_keep_every_automatic_action_off()
    {
        var s = new HubSettings();

        Assert.False(s.AutoPlayOnConnect);
        Assert.False(s.AutoPauseOnDisconnect);
        Assert.Null(s.DefaultVolumeOnConnect);
        Assert.False(s.DefaultVolumeEnabled);
        Assert.False(s.SwitchOutputToAirPodsOnConnect);
        Assert.False(s.BroadcastEarDetection);
        Assert.Equal("", s.EarRemovedOutputEndpointId);
        Assert.Equal(HandsfreeMode.AlwaysOn, s.HandsfreeMode);
        Assert.False(s.LowAudioLatency);
        Assert.False(s.AllowClaspLinks);
        foreach (var command in Enum.GetValues<ClaspCommand>()) Assert.Equal("", s.GetHotkey(command));
        Assert.False(s.StemActionsEnabled);
        Assert.Equal(StemAction.PassThrough, s.StemPlayPauseAction);
        Assert.Equal(StemAction.PassThrough, s.StemNextAction);
        Assert.Equal(StemAction.PassThrough, s.StemPreviousAction);
        Assert.False(s.PerAppOutputEnabled);
        Assert.Equal("", s.PerAppRulesText);
        Assert.Equal("", s.PhoneAudioDeviceId);
    }

    [Fact]
    public void Nearby_detection_is_on_by_default_and_the_other_opt_ins_stay_off()
    {
        var s = new HubSettings();

        Assert.True(s.ObserveNearbyAirPods);
        Assert.True(s.NearbyDefaultOnApplied);
        Assert.False(s.AutoConnectAirPods);
        Assert.False(s.AutoPlayOnConnect);
        Assert.False(s.RestorePreviousAudioOutput);
        Assert.False(s.BroadcastEarDetection);
    }

    [Fact]
    public void A_missing_file_gives_nearby_detection_on()
    {
        var s = HubSettingsStore.Load(PathFor("never-written.json"));

        Assert.True(s.ObserveNearbyAirPods);
        Assert.False(s.AutoConnectAirPods);
    }

    [Fact]
    public void A_file_without_the_nearby_field_loads_with_detection_on()
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor("no-nearby.json");
        File.WriteAllText(path, "{\"Theme\":0,\"ReduceMotion\":false,\"ReduceTransparency\":false,\"AutoShowCaseCard\":true,\"CaseCardSeconds\":6}");

        var s = HubSettingsStore.Load(path);

        Assert.True(s.ObserveNearbyAirPods);
        Assert.False(s.AutoConnectAirPods);
    }

    [Fact]
    public void An_older_explicit_false_is_turned_on_once_and_the_marker_is_set()
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor("old-false.json");
        File.WriteAllText(path, "{\"Theme\":0,\"ReduceMotion\":false,\"ReduceTransparency\":false,\"AutoShowCaseCard\":true,\"CaseCardSeconds\":6,"
            + "\"ObserveNearbyAirPods\":false,\"AutoConnectAirPods\":false}");

        var s = HubSettingsStore.Load(path);

        Assert.True(s.ObserveNearbyAirPods);
        Assert.True(s.NearbyDefaultOnApplied);
        Assert.False(s.AutoConnectAirPods); // the migration touches nothing else
    }

    [Fact]
    public void After_the_migration_a_false_the_owner_chose_survives_reloads()
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor("chose-off.json");
        File.WriteAllText(path, "{\"Theme\":0,\"ReduceMotion\":false,\"ReduceTransparency\":false,\"AutoShowCaseCard\":true,\"CaseCardSeconds\":6,"
            + "\"ObserveNearbyAirPods\":false}");

        var migrated = HubSettingsStore.Load(path);
        Assert.True(migrated.ObserveNearbyAirPods);
        migrated.ObserveNearbyAirPods = false; // the owner turns it off
        Assert.True(HubSettingsStore.Save(migrated, path));

        var reloaded = HubSettingsStore.Load(path);
        Assert.False(reloaded.ObserveNearbyAirPods);
        Assert.True(reloaded.NearbyDefaultOnApplied);
        Assert.True(HubSettingsStore.Save(reloaded, path));
        Assert.False(HubSettingsStore.Load(path).ObserveNearbyAirPods);
    }

    [Fact]
    public void An_unmigrated_backup_is_migrated_the_same_way_when_the_main_file_is_missing()
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor("settings.json");
        File.WriteAllText(path + ".bak", "{\"Theme\":0,\"ReduceMotion\":false,\"ReduceTransparency\":false,\"AutoShowCaseCard\":true,\"CaseCardSeconds\":6,"
            + "\"ObserveNearbyAirPods\":false}");

        Assert.True(HubSettingsStore.Load(path).ObserveNearbyAirPods);
    }

    [Fact]
    public void Shipped_defaults_for_notices_and_tray_match_the_integration_notes()
    {
        var s = new HubSettings();

        Assert.True(s.HandoffNoticeEnabled);
        Assert.Equal(20, s.LowBatteryThresholdPercent);
        Assert.True(s.ShowTrayBatteryPercentage);
        Assert.True(s.DimTrayIconWhenAudioElsewhere);
    }

    [Fact]
    public void An_old_settings_file_without_the_new_fields_loads_with_the_shipped_defaults()
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor("old.json");
        File.WriteAllText(path, """
            {
              "Theme": 2,
              "ReduceMotion": true,
              "ReduceTransparency": false,
              "AutoShowCaseCard": false,
              "CaseCardSeconds": 9,
              "PauseMediaOnEarRemoval": true,
              "RestorePreviousAudioOutput": true,
              "AudioTargetEndpointId": "ep",
              "AudioTargetContainerId": "c",
              "AudioFallbackEndpointId": "fb",
              "ShowPlaybackControls": false,
              "AirPodsDeviceId": "dev",
              "ObserveNearbyAirPods": true,
              "AutoConnectAirPods": true
            }
            """);

        var s = HubSettingsStore.Load(path);

        // Old values survive.
        Assert.Equal(AppTheme.Dark, s.Theme);
        Assert.True(s.ReduceMotion);
        Assert.False(s.AutoShowCaseCard);
        Assert.Equal(9, s.CaseCardSeconds);
        Assert.True(s.PauseMediaOnEarRemoval);
        Assert.Equal("dev", s.AirPodsDeviceId);
        Assert.True(s.AutoConnectAirPods);
        // New values take their defaults, including the ones that default to ON.
        Assert.True(s.HandoffNoticeEnabled);
        Assert.Equal(20, s.LowBatteryThresholdPercent);
        Assert.True(s.ShowTrayBatteryPercentage);
        Assert.True(s.DimTrayIconWhenAudioElsewhere);
        Assert.False(s.AutoPlayOnConnect);
        Assert.False(s.AllowClaspLinks);
        Assert.Equal(HandsfreeMode.AlwaysOn, s.HandsfreeMode);
        Assert.False(s.StemActionsEnabled);
        Assert.False(s.PerAppOutputEnabled);
    }

    [Fact]
    public void Every_new_setting_survives_a_save_and_load()
    {
        var path = PathFor("round.json");
        var s = new HubSettings
        {
            AutoPlayOnConnect = true,
            AutoPauseOnDisconnect = true,
            DefaultVolumeOnConnect = 40,
            SwitchOutputToAirPodsOnConnect = true,
            BroadcastEarDetection = true,
            EarRemovedOutputEndpointId = "{0.0.0.00000000}.{speakers}",
            HandsfreeMode = HandsfreeMode.Smart,
            LowAudioLatency = true,
            HandoffNoticeEnabled = false,
            LowBatteryThresholdPercent = 30,
            ShowTrayBatteryPercentage = false,
            DimTrayIconWhenAudioElsewhere = false,
            AllowClaspLinks = true,
            StemActionsEnabled = true,
            StemPlayPauseAction = StemAction.MuteMicrophone,
            StemNextAction = StemAction.Previous,
            StemPreviousAction = StemAction.None,
            PerAppOutputEnabled = true,
            PerAppRulesText = "spotify.exe=airpods\r\ngame.exe=default",
            PhoneAudioDeviceId = "phone-1",
        };
        foreach (var command in Enum.GetValues<ClaspCommand>()) s.SetHotkey(command, "Ctrl+Alt+" + (char)('A' + (int)command));

        Assert.True(HubSettingsStore.Save(s, path));
        var loaded = HubSettingsStore.Load(path);

        Assert.True(loaded.AutoPlayOnConnect);
        Assert.True(loaded.AutoPauseOnDisconnect);
        Assert.Equal(40, loaded.DefaultVolumeOnConnect);
        Assert.True(loaded.SwitchOutputToAirPodsOnConnect);
        Assert.True(loaded.BroadcastEarDetection);
        Assert.Equal("{0.0.0.00000000}.{speakers}", loaded.EarRemovedOutputEndpointId);
        Assert.Equal(HandsfreeMode.Smart, loaded.HandsfreeMode);
        Assert.True(loaded.LowAudioLatency);
        Assert.False(loaded.HandoffNoticeEnabled);
        Assert.Equal(30, loaded.LowBatteryThresholdPercent);
        Assert.False(loaded.ShowTrayBatteryPercentage);
        Assert.False(loaded.DimTrayIconWhenAudioElsewhere);
        Assert.True(loaded.AllowClaspLinks);
        Assert.True(loaded.StemActionsEnabled);
        Assert.Equal(StemAction.MuteMicrophone, loaded.StemPlayPauseAction);
        Assert.Equal(StemAction.Previous, loaded.StemNextAction);
        Assert.Equal(StemAction.None, loaded.StemPreviousAction);
        Assert.True(loaded.PerAppOutputEnabled);
        Assert.Equal("spotify.exe=airpods\r\ngame.exe=default", loaded.PerAppRulesText);
        Assert.Equal("phone-1", loaded.PhoneAudioDeviceId);
        foreach (var command in Enum.GetValues<ClaspCommand>())
            Assert.Equal("Ctrl+Alt+" + (char)('A' + (int)command), loaded.GetHotkey(command));
    }

    [Fact]
    public void Unknown_enum_names_and_null_strings_fall_back_to_the_defaults()
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor("bad.json");
        File.WriteAllText(path, """
            {
              "Theme": 0, "ReduceMotion": false, "ReduceTransparency": false, "AutoShowCaseCard": true, "CaseCardSeconds": 6,
              "HandsfreeMode": "Banana",
              "StemPlayPauseAction": "Explode",
              "StemNextAction": "7",
              "StemPreviousAction": null,
              "HotkeyConnect": null,
              "PerAppRulesText": null,
              "EarRemovedOutputEndpointId": null,
              "LowBatteryThresholdPercent": 999
            }
            """);

        var s = HubSettingsStore.Load(path);

        Assert.Equal(HandsfreeMode.AlwaysOn, s.HandsfreeMode);
        Assert.Equal(StemAction.PassThrough, s.StemPlayPauseAction);
        Assert.Equal(StemAction.PassThrough, s.StemNextAction);
        Assert.Equal(StemAction.PassThrough, s.StemPreviousAction);
        Assert.Equal("", s.HotkeyConnect);
        Assert.Equal("", s.PerAppRulesText);
        Assert.Equal("", s.EarRemovedOutputEndpointId);
        Assert.Equal(100, s.LowBatteryThresholdPercent);
    }

    [Fact]
    public void A_corrupt_file_gives_the_shipped_defaults()
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor("corrupt.json");
        File.WriteAllText(path, "{ not json");

        var s = HubSettingsStore.Load(path);

        Assert.True(s.HandoffNoticeEnabled);
        Assert.False(s.AutoPlayOnConnect);
    }

    [Fact]
    public void The_volume_toggle_starts_at_25_and_clearing_it_leaves_the_volume_alone()
    {
        var s = new HubSettings();
        Assert.Equal(25, s.DefaultVolumeSliderValue);

        s.DefaultVolumeEnabled = true;
        Assert.Equal(25, s.DefaultVolumeOnConnect);

        s.DefaultVolumeSliderValue = 61.4;
        Assert.Equal(61, s.DefaultVolumeOnConnect);

        s.DefaultVolumeEnabled = false;
        Assert.Null(s.DefaultVolumeOnConnect);

        // Moving the slider while the option is off must not turn it on by itself.
        s.DefaultVolumeSliderValue = 80;
        Assert.Null(s.DefaultVolumeOnConnect);

        s.DefaultVolumeOnConnect = 250;
        Assert.Equal(100, s.DefaultVolumeOnConnect);
    }

    [Fact]
    public void The_low_battery_choice_maps_to_off_10_20_30_and_stored_odd_values_show_as_the_nearest_choice()
    {
        var s = new HubSettings();
        Assert.Equal(2, s.LowBatteryThresholdIndex);

        s.LowBatteryThresholdIndex = 0;
        Assert.Equal(0, s.LowBatteryThresholdPercent);
        s.LowBatteryThresholdIndex = 1;
        Assert.Equal(10, s.LowBatteryThresholdPercent);
        s.LowBatteryThresholdIndex = 3;
        Assert.Equal(30, s.LowBatteryThresholdPercent);
        s.LowBatteryThresholdIndex = -1; // a ComboBox clearing its selection
        Assert.Equal(30, s.LowBatteryThresholdPercent);

        s.LowBatteryThresholdPercent = 15;
        Assert.Equal(2, s.LowBatteryThresholdIndex);
    }

    [Fact]
    public void Changing_a_hotkey_raises_its_own_property_name()
    {
        var s = new HubSettings();
        var names = new List<string?>();
        s.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        s.SetHotkey(ClaspCommand.FixConnection, "Ctrl+Alt+F");
        s.SetHotkey(ClaspCommand.FixConnection, "Ctrl+Alt+F"); // unchanged: no event

        Assert.Equal([nameof(HubSettings.HotkeyFixConnection)], names);
        Assert.True(HubSettings.IsHotkeyProperty(nameof(HubSettings.HotkeyPlayPause)));
        Assert.False(HubSettings.IsHotkeyProperty(nameof(HubSettings.AllowClaspLinks)));
        foreach (var command in Enum.GetValues<ClaspCommand>())
            Assert.True(HubSettings.IsHotkeyProperty(HubSettings.HotkeyPropertyName(command)));
    }
}
