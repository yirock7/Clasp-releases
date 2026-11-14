using AppleDeviceHub.Core.Presentation;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Presentation;

public sealed class OverflowCloseWaitTests
{
    [Fact]
    public void Shows_immediately_when_the_popup_is_already_gone() => Assert.True(OverflowCloseWait.ShouldShow(0, popupStillVisible: false));

    [Fact]
    public void Keeps_waiting_while_the_popup_is_visible_within_budget() => Assert.False(OverflowCloseWait.ShouldShow(200, popupStillVisible: true));

    [Fact]
    public void Decide_waits_then_shows_when_nothing_else_happened()
    {
        Assert.Equal(OverflowWaitAction.Wait, OverflowCloseWait.Decide(100, true, false, false, false));
        Assert.Equal(OverflowWaitAction.Show, OverflowCloseWait.Decide(100, false, false, false, false));
        Assert.Equal(OverflowWaitAction.Show, OverflowCloseWait.Decide(OverflowCloseWait.MaxWaitMs, true, false, false, false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Decide_cancels_on_escape_click_elsewhere_or_another_app_even_if_the_popup_closed_or_time_is_up(bool esc, bool click, bool other)
    {
        Assert.Equal(OverflowWaitAction.Cancel, OverflowCloseWait.Decide(50, true, esc, click, other));
        Assert.Equal(OverflowWaitAction.Cancel, OverflowCloseWait.Decide(50, false, esc, click, other));
        Assert.Equal(OverflowWaitAction.Cancel, OverflowCloseWait.Decide(OverflowCloseWait.MaxWaitMs + 100, true, esc, click, other));
    }

    [Fact]
    public void Shows_anyway_once_the_budget_is_spent() => Assert.True(OverflowCloseWait.ShouldShow(OverflowCloseWait.MaxWaitMs, popupStillVisible: true));

    [Fact]
    public void Describes_both_outcomes()
    {
        Assert.Equal("Flyout open: waited 48 ms for hidden-icons popup to close", OverflowCloseWait.Describe(48, false));
        Assert.Equal("Flyout open: hidden-icons popup still visible after 350 ms", OverflowCloseWait.Describe(350, true));
    }
}
