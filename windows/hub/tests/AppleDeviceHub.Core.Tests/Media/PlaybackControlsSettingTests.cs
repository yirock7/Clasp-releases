using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Media;

public class PlaybackControlsSettingTests
{
    [Fact]
    public void Playback_controls_are_on_by_default_and_the_choice_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), "clasp-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.True(new HubSettings().ShowPlaybackControls);
            Assert.True(HubSettingsStore.Save(new HubSettings { ShowPlaybackControls = false }, path));
            Assert.False(HubSettingsStore.Load(path).ShowPlaybackControls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_settings_file_from_before_playback_controls_keeps_the_sensible_default()
    {
        var path = Path.Combine(Path.GetTempPath(), "clasp-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Theme":0,"ReduceMotion":true,"ReduceTransparency":false,"AutoShowCaseCard":true,"CaseCardSeconds":6}""");
            var loaded = HubSettingsStore.Load(path);
            Assert.True(loaded.ReduceMotion); // confirms the old file was read
            Assert.True(loaded.ShowPlaybackControls);
        }
        finally { File.Delete(path); }
    }
}
