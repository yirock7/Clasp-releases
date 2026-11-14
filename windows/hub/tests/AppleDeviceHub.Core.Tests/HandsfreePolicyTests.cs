using AppleDeviceHub.Core.Audio;
using Xunit;

namespace AppleDeviceHub.Core.Tests;

public sealed class HandsfreePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static HandsfreeDecision Eval(HandsfreePolicy p, HandsfreeMode mode, bool mic, bool connected,
        HandsfreeState state, double s)
        => p.Evaluate(mode, mic, connected, state, At(s));

    [Fact]
    public void AlwaysOn_NeverActsWhenNothingWasChanged()
    {
        var p = new HandsfreePolicy();
        foreach (var state in Enum.GetValues<HandsfreeState>())
        {
            Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOn, true, true, state, 0).Action);
            Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOn, false, true, state, 1).Action);
        }
    }

    [Fact]
    public void AlwaysOff_DisablesOnceThenStaysQuiet()
    {
        var p = new HandsfreePolicy();
        var d = Eval(p, HandsfreeMode.AlwaysOff, true, true, HandsfreeState.Unknown, 0);
        Assert.Equal(HandsfreeAction.Disable, d.Action);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOff, true, true, HandsfreeState.Unknown, 1).Action);
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.Succeeded);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 2).Action);
    }

    [Fact]
    public void AlwaysOff_AlreadyOffKnown_DoesNothing()
        => Assert.Equal(HandsfreeAction.None,
            Eval(new HandsfreePolicy(), HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Off, 0).Action);

    [Fact]
    public void Smart_EnablesImmediatelyWhenMicStarts()
    {
        var p = new HandsfreePolicy();
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.Succeeded);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Off, 0).Action);
        Assert.Equal(HandsfreeAction.Enable, Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Off, 1).Action);
    }

    [Fact]
    public void Smart_DisablesOnlyAfterGracePeriod()
    {
        var p = new HandsfreePolicy();
        Assert.Equal(HandsfreeAction.Enable, Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Off, 0).Action);
        p.ReportOutcome(HandsfreeAction.Enable, HandsfreeOutcome.Succeeded);

        var d = Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 10);
        Assert.Equal(HandsfreeAction.None, d.Action);
        Assert.Equal(At(30), d.RecheckAt);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 29.9).Action);
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 30).Action);
    }

    [Fact]
    public void Smart_MicReturningDuringGrace_CancelsDisableAndRestartsGrace()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Off, 0);
        p.ReportOutcome(HandsfreeAction.Enable, HandsfreeOutcome.Succeeded);
        Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 5);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Unknown, 15).Action);
        // New idle period begins at 20: grace runs to 40.
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 20).Action);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 39).Action);
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 40).Action);
    }

    [Fact]
    public void Smart_RapidMicFlapping_ProducesNoRepeatedActions()
    {
        var p = new HandsfreePolicy();
        var actions = new List<HandsfreeAction>();
        void Step(bool mic, double s)
        {
            var d = Eval(p, HandsfreeMode.Smart, mic, true, HandsfreeState.Unknown, s);
            actions.Add(d.Action);
            if (d.Action != HandsfreeAction.None) p.ReportOutcome(d.Action, HandsfreeOutcome.Succeeded);
        }
        Step(true, 0);
        for (var i = 1; i <= 10; i++)
        {
            Step(false, i * 2);
            Step(true, i * 2 + 1);
        }
        Assert.Equal(1, actions.Count(a => a == HandsfreeAction.Enable));
        Assert.DoesNotContain(HandsfreeAction.Disable, actions);
    }

    [Fact]
    public void Smart_NoMicEverSeen_DisablesImmediately()
        => Assert.Equal(HandsfreeAction.Disable,
            Eval(new HandsfreePolicy(), HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 0).Action);

    [Fact]
    public void Smart_CustomGracePeriodIsHonoured()
    {
        var p = new HandsfreePolicy(TimeSpan.FromSeconds(3));
        Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.On, 0);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.On, 1).Action);
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.On, 4).Action);
    }

    [Fact]
    public void AtMostOneAttemptPerTransition_FailureIsNotRetried()
    {
        var p = new HandsfreePolicy();
        Assert.Equal(HandsfreeAction.Enable, Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Off, 0).Action);
        p.ReportOutcome(HandsfreeAction.Enable, HandsfreeOutcome.Failed);
        for (var s = 1; s < 10; s++)
            Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Off, s).Action);
    }

    [Fact]
    public void DeviceNotFound_IsNotRetriedEither()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 0);
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.DeviceNotFound);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 5).Action);
    }

    [Fact]
    public void FailedAttempt_IsRetriedOnceAfterReconnect()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 0);
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.Failed);
        Eval(p, HandsfreeMode.AlwaysOff, false, false, HandsfreeState.Unknown, 5);
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 10).Action);
    }

    [Fact]
    public void AccessDenied_StopsAllFurtherAttemptsAndSurfacesReason()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 0);
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.AccessDenied);
        Assert.True(p.IsBlocked);
        Assert.Contains("administrator", p.BlockedReason);
        foreach (var mode in Enum.GetValues<HandsfreeMode>())
        {
            var d = Eval(p, mode, true, true, HandsfreeState.Unknown, 100);
            Assert.Equal(HandsfreeAction.None, d.Action);
        }
        // Even after a reconnect.
        Eval(p, HandsfreeMode.Smart, false, false, HandsfreeState.Unknown, 200);
        var after = Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Unknown, 201);
        Assert.Equal(HandsfreeAction.None, after.Action);
        Assert.Contains("administrator", after.Reason);
    }

    [Fact]
    public void ResetBlock_AllowsActingAgain()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 0);
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.AccessDenied);
        p.ResetBlock();
        Assert.False(p.IsBlocked);
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 1).Action);
    }

    [Fact]
    public void Reset_ForgetsStateOwnedByThePreviouslySelectedDevice()
    {
        var p = new HandsfreePolicy();
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 0).Action);
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.Succeeded);

        p.Reset();

        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOn, false, true, HandsfreeState.Unknown, 1).Action);
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 2).Action);
    }

    [Fact]
    public void Disconnected_NeverActsInAnyMode()
    {
        var p = new HandsfreePolicy();
        foreach (var mode in Enum.GetValues<HandsfreeMode>())
            foreach (var mic in new[] { true, false })
                foreach (var state in Enum.GetValues<HandsfreeState>())
                    Assert.Equal(HandsfreeAction.None, Eval(p, mode, mic, false, state, 0).Action);
    }

    [Fact]
    public void MicActivityWhileDisconnected_StillStartsGraceCorrectly()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.Smart, true, false, HandsfreeState.Unknown, 0);
        Eval(p, HandsfreeMode.Smart, false, false, HandsfreeState.Unknown, 10);
        // Reconnect inside grace: no Disable yet, holds target on.
        var d = Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.Unknown, 15);
        Assert.NotEqual(HandsfreeAction.Disable, d.Action);
    }

    [Fact]
    public void SwitchingFromOffToAlwaysOn_RestoresWindowsDefaultExactlyOnce()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 0);
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.Succeeded);

        var d = Eval(p, HandsfreeMode.AlwaysOn, false, true, HandsfreeState.Unknown, 5);
        Assert.Equal(HandsfreeAction.Enable, d.Action);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOn, false, true, HandsfreeState.Unknown, 6).Action);
        p.ReportOutcome(HandsfreeAction.Enable, HandsfreeOutcome.Succeeded);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOn, false, true, HandsfreeState.Unknown, 7).Action);
    }

    [Fact]
    public void SwitchingFromSmartToAlwaysOn_WhenAlreadyOn_DoesNothing()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Off, 0);
        p.ReportOutcome(HandsfreeAction.Enable, HandsfreeOutcome.Succeeded);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOn, false, true, HandsfreeState.Unknown, 1).Action);
    }

    [Fact]
    public void SwitchingToAlwaysOn_WhileDisconnected_WaitsForConnection()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.AlwaysOff, false, true, HandsfreeState.Unknown, 0);
        p.ReportOutcome(HandsfreeAction.Disable, HandsfreeOutcome.Succeeded);
        Assert.Equal(HandsfreeAction.None, Eval(p, HandsfreeMode.AlwaysOn, false, false, HandsfreeState.Unknown, 1).Action);
        Assert.Equal(HandsfreeAction.Enable, Eval(p, HandsfreeMode.AlwaysOn, false, true, HandsfreeState.Unknown, 2).Action);
    }

    [Fact]
    public void SwitchingFromAlwaysOnToSmart_TakesOverWithoutWaiting()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.AlwaysOn, false, true, HandsfreeState.On, 0);
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.Smart, false, true, HandsfreeState.On, 1).Action);
    }

    [Fact]
    public void SmartToAlwaysOff_AfterEnable_DisablesEvenWithMicInUse()
    {
        var p = new HandsfreePolicy();
        Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Off, 0);
        p.ReportOutcome(HandsfreeAction.Enable, HandsfreeOutcome.Succeeded);
        Assert.Equal(HandsfreeAction.Disable, Eval(p, HandsfreeMode.AlwaysOff, true, true, HandsfreeState.Unknown, 1).Action);
    }

    [Fact]
    public void KnownStateOverridesAssumption()
    {
        var p = new HandsfreePolicy();
        p.ReportOutcome(HandsfreeAction.Enable, HandsfreeOutcome.Succeeded);
        // Caller now knows it is Off (someone else changed it): Smart with mic enables again.
        Assert.Equal(HandsfreeAction.Enable, Eval(p, HandsfreeMode.Smart, true, true, HandsfreeState.Off, 0).Action);
    }

    [Fact]
    public void NegativeGracePeriod_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new HandsfreePolicy(TimeSpan.FromSeconds(-1)));
}
