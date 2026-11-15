using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Media;

public class EarMediaSettingTests
{
    [Fact]
    public void Pause_on_removal_is_off_by_default_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), "clasp-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.False(new HubSettings().PauseMediaOnEarRemoval);
            Assert.True(HubSettingsStore.Save(new HubSettings { PauseMediaOnEarRemoval = true }, path));
            Assert.True(HubSettingsStore.Load(path).PauseMediaOnEarRemoval);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_settings_file_from_before_the_option_loads_it_as_off()
    {
        var path = Path.Combine(Path.GetTempPath(), "clasp-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Theme":0,"ReduceMotion":true,"ReduceTransparency":false,"AutoShowCaseCard":true,"CaseCardSeconds":6}""");
            var loaded = HubSettingsStore.Load(path);
            Assert.True(loaded.ReduceMotion); // the file was really read
            Assert.False(loaded.PauseMediaOnEarRemoval);
        }
        finally { File.Delete(path); }
    }
}
