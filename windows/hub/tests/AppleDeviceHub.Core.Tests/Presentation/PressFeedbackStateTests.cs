using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Presentation;

/// <summary>
/// The hover / press / slide-off logic behind every glass button: touch never hovers, a held
/// pointer that leaves goes back to rest but stays held, and releasing springs to hover or rest.
/// </summary>
public sealed class PressFeedbackStateTests
{
    [Fact]
    public void StartsAtRestWithoutSpring()
    {
        var s = new PressFeedbackState();
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.False(s.Spring);
        Assert.False(s.IsPointerHeld);
    }

    [Fact]
    public void MouseEnterHoversAndExitRests()
    {
        var s = new PressFeedbackState();
        Assert.True(s.PointerEntered(isTouch: false));
        Assert.Equal(PressTarget.Hover, s.Target);
        Assert.False(s.Spring);

        Assert.True(s.PointerExited());
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.False(s.Spring);
    }

    [Fact]
    public void TouchNeverGivesHover()
    {
        var s = new PressFeedbackState();
        Assert.False(s.PointerEntered(isTouch: true));
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.True(s.PointerPressed(isTouch: true));
        Assert.Equal(PressTarget.Pressed, s.Target);
        Assert.True(s.PointerReleased());
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.True(s.Spring);
    }

    [Fact]
    public void TouchPressWithoutEnterStillPresses()
    {
        var s = new PressFeedbackState();
        Assert.True(s.PointerPressed(isTouch: true));
        Assert.Equal(PressTarget.Pressed, s.Target);
    }

    [Fact]
    public void TouchMovesDoNotCreateHover()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(isTouch: true);
        s.PointerMoved(isInside: true);
        Assert.Equal(PressTarget.Rest, s.Target);
    }

    [Fact]
    public void MousePressGoesPressedFromHover()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        Assert.True(s.PointerPressed());
        Assert.Equal(PressTarget.Pressed, s.Target);
        Assert.True(s.IsPointerHeld);
        Assert.False(s.Spring);
    }

    [Fact]
    public void MouseReleaseInsideSpringsBackToHover()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        Assert.True(s.PointerReleased());
        Assert.Equal(PressTarget.Hover, s.Target);
        Assert.True(s.Spring);
        Assert.False(s.IsPointerHeld);
    }

    [Fact]
    public void PenBehavesLikeMouse()
    {
        // The App passes isTouch only for PointerDeviceType.Touch, so a pen is a non-touch pointer.
        var s = new PressFeedbackState();
        s.PointerEntered(isTouch: false);
        s.PointerPressed(isTouch: false);
        s.PointerReleased();
        Assert.Equal(PressTarget.Hover, s.Target);
    }

    [Fact]
    public void SlidingOutWhileHeldRisesToRestWithoutSpring()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        Assert.True(s.PointerMoved(isInside: false));
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.False(s.Spring);
        Assert.True(s.IsPointerHeld);
    }

    [Fact]
    public void SlidingBackInPressesAgain()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        s.PointerMoved(false);
        Assert.True(s.PointerMoved(true));
        Assert.Equal(PressTarget.Pressed, s.Target);
        Assert.False(s.Spring);
    }

    [Fact]
    public void ExitWhileHeldKeepsTheHold()
    {
        // Under pointer capture Exited arrives when the pointer leaves the bounds; it must not release.
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        Assert.True(s.PointerExited());
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.True(s.IsPointerHeld);

        Assert.True(s.PointerEntered(false));
        Assert.Equal(PressTarget.Pressed, s.Target);
    }

    [Fact]
    public void ReleaseOutsideStaysAtRestAndDoesNotSpring()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        s.PointerMoved(false);
        Assert.False(s.PointerReleased());
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.False(s.Spring);
        Assert.False(s.IsPointerHeld);
    }

    [Fact]
    public void MouseReenteringAfterOutsideReleaseHoversAgain()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        s.PointerMoved(false);
        s.PointerReleased();
        Assert.True(s.PointerEntered(false));
        Assert.Equal(PressTarget.Hover, s.Target);
    }

    [Fact]
    public void TouchSlidingOutThenReleasingGivesRest()
    {
        var s = new PressFeedbackState();
        s.PointerPressed(isTouch: true);
        s.PointerMoved(false);
        Assert.Equal(PressTarget.Rest, s.Target);
        s.PointerReleased();
        Assert.Equal(PressTarget.Rest, s.Target);
    }

    [Fact]
    public void ReleaseAfterSlidingOutAndBackIsAHoverRelease()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        s.PointerMoved(false);
        s.PointerMoved(true);
        Assert.True(s.PointerReleased());
        Assert.Equal(PressTarget.Hover, s.Target);
        Assert.True(s.Spring);
    }

    [Fact]
    public void CancelReleasesWithSpring()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        Assert.True(s.PointerCanceled());
        Assert.False(s.IsPointerHeld);
        Assert.True(s.Spring);
        // A mouse that is still over the control keeps hovering.
        Assert.Equal(PressTarget.Hover, s.Target);
    }

    [Fact]
    public void CancelOfATouchGoesToRest()
    {
        var s = new PressFeedbackState();
        s.PointerPressed(isTouch: true);
        Assert.True(s.PointerCanceled());
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.True(s.Spring);
        Assert.False(s.IsPointerInside);
    }

    [Fact]
    public void StrayReleaseOrCancelDoesNothing()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        Assert.False(s.PointerReleased());
        Assert.False(s.PointerCanceled());
        Assert.Equal(PressTarget.Hover, s.Target);
        Assert.False(s.Spring);
    }

    [Fact]
    public void SpringFlagDescribesOnlyTheLatestCall()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        s.PointerReleased();
        Assert.True(s.Spring);
        s.PointerExited();
        Assert.False(s.Spring);
    }

    [Fact]
    public void DoublePressIsHarmless()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        Assert.False(s.PointerPressed());
        Assert.Equal(PressTarget.Pressed, s.Target);
    }

    [Fact]
    public void KeyDownPressesAndKeyUpSprings()
    {
        var s = new PressFeedbackState();
        Assert.True(s.KeyDown());
        Assert.Equal(PressTarget.Pressed, s.Target);
        Assert.True(s.KeyUp());
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.True(s.Spring);
    }

    [Fact]
    public void KeyUpReturnsToHoverWhileMouseIsInside()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.KeyDown();
        s.KeyUp();
        Assert.Equal(PressTarget.Hover, s.Target);
        Assert.True(s.Spring);
    }

    [Fact]
    public void RepeatedKeyDownDoesNotChangeAnything()
    {
        var s = new PressFeedbackState();
        s.KeyDown();
        Assert.False(s.KeyDown());
        Assert.False(s.Spring);
    }

    [Fact]
    public void KeyHoldSurvivesPointerSlidingOut()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        s.KeyDown();
        s.PointerMoved(false);
        Assert.Equal(PressTarget.Pressed, s.Target);
        s.KeyUp();
        Assert.Equal(PressTarget.Rest, s.Target);
    }

    [Fact]
    public void StrayKeyUpDoesNothing()
    {
        var s = new PressFeedbackState();
        Assert.False(s.KeyUp());
        Assert.Equal(PressTarget.Rest, s.Target);
    }

    [Fact]
    public void FocusLostReleasesAHeldKey()
    {
        var s = new PressFeedbackState();
        s.KeyDown();
        Assert.True(s.FocusLost());
        Assert.Equal(PressTarget.Rest, s.Target);
        Assert.True(s.Spring);
    }

    [Fact]
    public void FocusLostReleasesAHeldPointerAndKeepsMouseHover()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        s.PointerPressed();
        Assert.True(s.FocusLost());
        Assert.Equal(PressTarget.Hover, s.Target);
        Assert.True(s.Spring);
        Assert.False(s.IsPointerHeld);
    }

    [Fact]
    public void FocusLostWhenNothingHeldDoesNothing()
    {
        var s = new PressFeedbackState();
        s.PointerEntered(false);
        Assert.False(s.FocusLost());
        Assert.Equal(PressTarget.Hover, s.Target);
        Assert.False(s.Spring);
    }

    [Fact]
    public void MoveInsideWithoutPriorEnterHovers()
    {
        // Attached while the mouse was already over the control: the first move reveals it.
        var s = new PressFeedbackState();
        Assert.True(s.PointerMoved(true));
        Assert.Equal(PressTarget.Hover, s.Target);
    }
}
