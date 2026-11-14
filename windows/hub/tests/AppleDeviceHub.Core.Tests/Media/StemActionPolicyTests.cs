using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Media;

public class StemActionPolicyTests
{
    private static readonly StemContext Active = new(true, true);
    private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private StemActionPolicy Create(StemActionSettings? settings = null)
    {
        var policy = new StemActionPolicy(() => _now);
        if (settings is not null) policy.Configure(settings);
        return policy;
    }

    private static StemActionSettings On(StemAction next = StemAction.MuteMicrophone) =>
        new(true, StemAction.PassThrough, next, StemAction.PassThrough);

    [Fact]
    public void Default_is_off_and_everything_passes_through()
    {
        var policy = Create();
        foreach (var gesture in Enum.GetValues<StemGesture>())
        {
            Assert.False(policy.OnKeyDown(gesture, Active).Swallow);
            Assert.Equal(StemAction.PassThrough, StemActionSettings.Default.For(gesture));
        }
        Assert.False(StemActionSettings.Default.Enabled);
    }

    [Fact]
    public void Enabled_with_default_mappings_still_passes_through()
    {
        var policy = Create(new StemActionSettings(Enabled: true));
        Assert.False(policy.OnKeyDown(StemGesture.Next, Active).Swallow);
    }

    [Fact]
    public void Remapped_gesture_is_swallowed_and_runs_the_action()
    {
        var decision = Create(On()).OnKeyDown(StemGesture.Next, Active);
        Assert.True(decision.Swallow);
        Assert.True(decision.ShouldRun);
        Assert.Equal(StemAction.MuteMicrophone, decision.Action);
    }

    [Fact]
    public void Other_gestures_are_left_alone()
    {
        var policy = Create(On());
        Assert.False(policy.OnKeyDown(StemGesture.PlayPause, Active).Swallow);
        Assert.False(policy.OnKeyDown(StemGesture.Previous, Active).Swallow);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Inactive_context_never_swallows(bool connected, bool output)
    {
        var decision = Create(On()).OnKeyDown(StemGesture.Next, new StemContext(connected, output));
        Assert.False(decision.Swallow);
        Assert.False(decision.ShouldRun);
    }

    [Fact]
    public void Disabled_setting_never_swallows()
    {
        var settings = On() with { Enabled = false };
        Assert.False(Create(settings).OnKeyDown(StemGesture.Next, Active).Swallow);
    }

    [Fact]
    public void Mapping_a_gesture_to_itself_passes_through()
    {
        var policy = Create(new StemActionSettings(true, Next: StemAction.Next));
        Assert.False(policy.OnKeyDown(StemGesture.Next, Active).Swallow);
    }

    [Fact]
    public void None_swallows_without_running_anything()
    {
        var decision = Create(On(StemAction.None)).OnKeyDown(StemGesture.Next, Active);
        Assert.True(decision.Swallow);
        Assert.False(decision.ShouldRun);
    }

    [Fact]
    public void Same_key_within_150ms_is_a_duplicate_and_runs_once()
    {
        var policy = Create(On());
        Assert.True(policy.OnKeyDown(StemGesture.Next, Active).ShouldRun);
        _now += TimeSpan.FromMilliseconds(149);
        var repeat = policy.OnKeyDown(StemGesture.Next, Active);
        Assert.True(repeat.Swallow);
        Assert.True(repeat.IsDuplicate);
        Assert.False(repeat.ShouldRun);
    }

    [Fact]
    public void Same_key_at_150ms_or_later_is_a_new_press()
    {
        var policy = Create(On());
        policy.OnKeyDown(StemGesture.Next, Active);
        _now += TimeSpan.FromMilliseconds(150);
        Assert.True(policy.OnKeyDown(StemGesture.Next, Active).ShouldRun);
    }

    [Fact]
    public void Burst_cannot_extend_the_window()
    {
        var policy = Create(On());
        policy.OnKeyDown(StemGesture.Next, Active);
        _now += TimeSpan.FromMilliseconds(100);
        Assert.True(policy.OnKeyDown(StemGesture.Next, Active).IsDuplicate);
        _now += TimeSpan.FromMilliseconds(100);
        Assert.True(policy.OnKeyDown(StemGesture.Next, Active).ShouldRun);
    }

    [Fact]
    public void Different_keys_do_not_debounce_each_other()
    {
        var policy = Create(new StemActionSettings(true, StemAction.MuteMicrophone, StemAction.MuteMicrophone));
        Assert.True(policy.OnKeyDown(StemGesture.PlayPause, Active).ShouldRun);
        Assert.True(policy.OnKeyDown(StemGesture.Next, Active).ShouldRun);
    }

    [Fact]
    public void Duplicate_of_a_passed_through_press_passes_through()
    {
        var policy = Create(On());
        Assert.False(policy.OnKeyDown(StemGesture.Next, new StemContext(true, false)).Swallow);
        _now += TimeSpan.FromMilliseconds(50);
        // The first press passed through (output was not the AirPods), so a repeat must not be half-handled.
        Assert.False(policy.OnKeyDown(StemGesture.Next, Active).Swallow);
    }

    [Fact]
    public void Key_up_is_swallowed_only_after_a_swallowed_key_down()
    {
        var policy = Create(On());
        Assert.False(policy.OnKeyUp(StemGesture.Next));
        policy.OnKeyDown(StemGesture.Next, Active);
        Assert.True(policy.OnKeyUp(StemGesture.Next));
        Assert.False(policy.OnKeyUp(StemGesture.Next));
    }

    [Fact]
    public void Key_up_of_a_passed_through_press_is_not_swallowed()
    {
        var policy = Create(On());
        policy.OnKeyDown(StemGesture.Next, new StemContext(true, false));
        Assert.False(policy.OnKeyUp(StemGesture.Next));
    }

    [Fact]
    public void Key_up_follows_duplicate_key_downs()
    {
        var policy = Create(On());
        policy.OnKeyDown(StemGesture.Next, Active);
        _now += TimeSpan.FromMilliseconds(10);
        policy.OnKeyDown(StemGesture.Next, Active);
        Assert.True(policy.OnKeyUp(StemGesture.Next));
    }

    [Fact]
    public void Turning_the_feature_off_forgets_debounce()
    {
        var policy = Create(On());
        policy.OnKeyDown(StemGesture.Next, Active);
        policy.Configure(StemActionSettings.Default);
        policy.Configure(On());
        Assert.True(policy.OnKeyDown(StemGesture.Next, Active).ShouldRun);
    }
}
