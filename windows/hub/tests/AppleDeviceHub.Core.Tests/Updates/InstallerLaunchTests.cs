using AppleDeviceHub.Core.Updates;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Updates;

public sealed class InstallerLaunchTests
{
    [Fact]
    public void Arguments_keep_the_silent_switches_and_relaunch()
    {
        var args = InstallerLaunch.Arguments(@"C:\u\setup-0.1.18.log", 1234);
        Assert.Equal(new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH" }, args.Take(5));
    }

    [Fact]
    public void Arguments_tell_setup_which_process_to_wait_for_and_where_to_log()
    {
        var args = InstallerLaunch.Arguments("C:/u/setup-0.1.18.log", 1234);
        Assert.Contains("/WAITPID=1234", args);
        Assert.Equal("/LOG=\"C:/u/setup-0.1.18.log\"", args[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Arguments_leave_out_the_wait_switch_without_a_valid_process_id(int pid)
    {
        Assert.DoesNotContain(InstallerLaunch.Arguments(@"C:\u\x.log", pid), a => a.StartsWith("/WAITPID", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandLine_quotes_a_log_path_that_has_spaces()
    {
        var line = InstallerLaunch.CommandLine("C:/Users/A B/updates/setup-0.1.18.log", 42);
        Assert.EndsWith("/LOG=\"C:/Users/A B/updates/setup-0.1.18.log\"", line);
        Assert.Contains(" /WAITPID=42 ", line);
    }

    [Fact]
    public void Log_file_is_named_after_the_version_and_sits_in_the_updates_folder()
    {
        Assert.True(ClaspVersion.TryParse("0.1.18", out var version));
        Assert.Equal("setup-0.1.18.log", InstallerLaunch.LogFileName(version));
        Assert.Equal(Path.Combine(@"C:\updates", "setup-0.1.18.log"), InstallerLaunch.LogPath(@"C:\updates", version));
    }
}
