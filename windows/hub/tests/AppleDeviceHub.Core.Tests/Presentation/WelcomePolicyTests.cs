using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Presentation;

public class WelcomePolicyTests
{
    [Fact]
    public void A_new_install_shows_the_guide_once_on_a_normal_launch()
        => Assert.True(WelcomePolicy.ShouldShowAtStartup(welcomeCompleted: false, isDemoTransport: false, quietStart: false, isCommandLaunch: false, explicitRequest: false));

    [Fact]
    public void A_finished_or_upgraded_install_does_not_show_it()
        => Assert.False(WelcomePolicy.ShouldShowAtStartup(welcomeCompleted: true, isDemoTransport: false, quietStart: false, isCommandLaunch: false, explicitRequest: false));

    [Fact]
    public void The_demo_transport_never_shows_it_unasked()
        => Assert.False(WelcomePolicy.ShouldShowAtStartup(welcomeCompleted: false, isDemoTransport: true, quietStart: false, isCommandLaunch: false, explicitRequest: false));

    [Fact]
    public void A_quiet_sign_in_start_or_a_link_launch_does_not_show_it()
    {
        Assert.False(WelcomePolicy.ShouldShowAtStartup(false, false, quietStart: true, isCommandLaunch: false, explicitRequest: false));
        Assert.False(WelcomePolicy.ShouldShowAtStartup(false, false, quietStart: false, isCommandLaunch: true, explicitRequest: false));
    }

    [Fact]
    public void An_explicit_request_shows_it_even_on_the_demo_transport_but_not_on_a_quiet_start()
    {
        Assert.True(WelcomePolicy.ShouldShowAtStartup(true, isDemoTransport: true, quietStart: false, isCommandLaunch: false, explicitRequest: true));
        Assert.False(WelcomePolicy.ShouldShowAtStartup(true, true, quietStart: true, isCommandLaunch: false, explicitRequest: true));
    }

    [Theory]
    [InlineData("--welcome-page=1", 0)]
    [InlineData("--welcome-page=3", 2)]
    [InlineData("--welcome-page=0", 0)]
    [InlineData("--welcome-page=4", 0)]
    [InlineData("--welcome-page=x", 0)]
    public void The_screenshot_page_argument_is_read_safely(string argument, int expected)
        => Assert.Equal(expected, WelcomePolicy.ParseStartPage(new[] { "--show=welcome", argument }));

    [Fact]
    public void Without_the_page_argument_the_guide_starts_on_the_first_page()
        => Assert.Equal(0, WelcomePolicy.ParseStartPage(new[] { "--show=welcome" }));
}
