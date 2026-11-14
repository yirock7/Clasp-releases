using AppleDeviceHub.Core.Presentation;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Presentation;

public sealed class FlyoutOpenPolicyTests
{
    [Theory]
    [InlineData(FlyoutOpenReason.Programmatic)]
    [InlineData(FlyoutOpenReason.Passive)]
    public void Non_tray_opens_never_wait_or_cancel(FlyoutOpenReason reason)
    {
        Assert.False(FlyoutOpenPolicy.ChecksOverflow(reason));
        Assert.False(FlyoutOpenPolicy.CanCancel(reason));
        Assert.False(FlyoutOpenPolicy.ShouldWaitForOverflow(reason, overflowGenuinelyOpen: true));
        foreach (var (esc, click, other) in new[] { (true, false, false), (false, true, false), (false, false, true), (true, true, true) })
        {
            Assert.Equal(OverflowWaitAction.Show, FlyoutOpenPolicy.Decide(reason, 0, true, esc, click, other));
            Assert.Equal(OverflowWaitAction.Show, FlyoutOpenPolicy.Decide(reason, 10_000, false, esc, click, other));
        }
    }

    [Fact]
    public void Tray_click_waits_only_when_the_popup_is_genuinely_open()
    {
        Assert.True(FlyoutOpenPolicy.ShouldWaitForOverflow(FlyoutOpenReason.TrayClick, true));
        Assert.False(FlyoutOpenPolicy.ShouldWaitForOverflow(FlyoutOpenReason.TrayClick, false));
    }

    [Fact]
    public void Tray_click_keeps_the_wait_and_cancel_rules()
    {
        Assert.True(FlyoutOpenPolicy.CanCancel(FlyoutOpenReason.TrayClick));
        Assert.Equal(OverflowWaitAction.Wait, FlyoutOpenPolicy.Decide(FlyoutOpenReason.TrayClick, 100, true, false, false, false));
        Assert.Equal(OverflowWaitAction.Show, FlyoutOpenPolicy.Decide(FlyoutOpenReason.TrayClick, 100, false, false, false, false));
        Assert.Equal(OverflowWaitAction.Cancel, FlyoutOpenPolicy.Decide(FlyoutOpenReason.TrayClick, 50, true, false, false, true));
    }

    [Theory]
    [InlineData(true, false, true, true, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, true, false, false)]
    public void Popup_is_genuinely_open_only_when_visible_uncloaked_non_empty_and_on_screen(bool vis, bool cloaked, bool nonEmpty, bool onScreen, bool expected)
        => Assert.Equal(expected, FlyoutOpenPolicy.IsPopupGenuinelyOpen(vis, cloaked, nonEmpty, onScreen));
}
