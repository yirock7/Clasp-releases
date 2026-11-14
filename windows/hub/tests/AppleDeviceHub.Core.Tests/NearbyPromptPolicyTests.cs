using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

public sealed class NearbyPromptPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_unique_open_source_must_stay_stable_before_prompting()
    {
        var policy = new NearbyPromptPolicy();
        var source = OpenSource();

        Assert.False(Evaluate(policy, [source], Start).ShouldPrompt);
        Assert.False(Evaluate(policy, [source], Start.AddMilliseconds(750)).ShouldPrompt);
        var settled = Evaluate(policy, [source], Start.AddSeconds(1));

        Assert.True(settled.ShouldPrompt);
        Assert.True(settled.ShouldNotifyNearbyEpisodePresent);
        Assert.False(Evaluate(policy, [source], Start.AddSeconds(2)).ShouldPrompt);
    }

    [Fact]
    public void Ambiguity_and_staleness_reset_the_stability_window()
    {
        var policy = new NearbyPromptPolicy();
        var source = OpenSource();
        Assert.False(Evaluate(policy, [source], Start).ShouldPrompt);

        var other = OpenSource();
        var ambiguous = Evaluate(policy, [source, other], Start.AddMilliseconds(500));
        Assert.False(ambiguous.HasUniqueFreshOpenSource);
        Assert.False(ambiguous.ShouldPrompt);

        Assert.False(Evaluate(policy, [source], Start.AddSeconds(1)).ShouldPrompt);
        var stale = source with { LastHeardAt = Start.AddSeconds(-20) };
        Assert.False(Evaluate(policy, [stale], Start.AddSeconds(1.5), BroadcastPhase.Stale).ShouldPrompt);

        Assert.False(Evaluate(policy, [source], Start.AddSeconds(2)).ShouldPrompt);
        Assert.True(Evaluate(policy, [source], Start.AddSeconds(3)).ShouldPrompt);
    }

    [Fact]
    public void A_source_identity_change_restarts_the_settle_window()
    {
        var policy = new NearbyPromptPolicy();
        var first = OpenSource();
        var second = OpenSource();
        Assert.False(Evaluate(policy, [first], Start).ShouldPrompt);
        Assert.False(Evaluate(policy, [second with { LastHeardAt = Start.AddMilliseconds(750) }], Start.AddMilliseconds(750)).ShouldPrompt);
        Assert.False(Evaluate(policy, [second with { LastHeardAt = Start.AddMilliseconds(1500) }], Start.AddMilliseconds(1500)).ShouldPrompt);
        Assert.True(Evaluate(policy, [second with { LastHeardAt = Start.AddMilliseconds(1750) }], Start.AddMilliseconds(1750)).ShouldPrompt);
    }

    [Fact]
    public void Closed_case_and_rotated_addresses_do_not_create_repeated_open_prompts()
    {
        var policy = new NearbyPromptPolicy();
        var source = OpenSource();
        Assert.False(Evaluate(policy, [source], Start).ShouldPrompt);
        Assert.True(Evaluate(policy, [source], Start.AddSeconds(1)).ShouldPrompt);

        var rotated = OpenSource();
        Assert.False(Evaluate(policy, [rotated], Start.AddSeconds(2)).ShouldPrompt);
        Assert.False(Evaluate(policy, [rotated], Start.AddSeconds(3)).ShouldPrompt);

        var closed = rotated with { LidClosed = true };
        Assert.False(Evaluate(policy, [closed], Start.AddSeconds(4)).ShouldPrompt);
        Assert.False(Evaluate(policy, [rotated], Start.AddSeconds(5)).ShouldPrompt);
        Assert.False(Evaluate(policy, [rotated], Start.AddSeconds(6)).ShouldPrompt);
    }

    [Fact]
    public void Global_absence_cooldown_rearms_a_new_stable_open_episode()
    {
        var policy = new NearbyPromptPolicy();
        var source = OpenSource();
        Assert.False(Evaluate(policy, [source], Start).ShouldPrompt);
        var first = Evaluate(policy, [source], Start.AddSeconds(1));
        Assert.True(first.ShouldPrompt);

        var absent = Evaluate(policy, [], Start.AddSeconds(2));
        Assert.True(absent.ShouldNotifyNearbyEpisodeAbsent);
        var reappearedSource = source with { LastHeardAt = Start.AddSeconds(32) };
        var reappeared = Evaluate(policy, [reappearedSource], Start.AddSeconds(32));
        Assert.False(reappeared.ShouldPrompt);
        var refreshed = reappearedSource with { LastHeardAt = Start.AddSeconds(33) };
        var settled = Evaluate(policy, [refreshed], Start.AddSeconds(33));
        Assert.True(settled.ShouldPrompt);
        Assert.True(settled.ShouldNotifyNearbyEpisodePresent);
    }

    private static NearbyPromptDecision Evaluate(
        NearbyPromptPolicy policy,
        IReadOnlyList<BroadcastSource> sources,
        DateTimeOffset now,
        BroadcastPhase phase = BroadcastPhase.Fresh)
        => policy.Evaluate(sources, phase, observing: true, now);

    private static BroadcastSource OpenSource(Guid? id = null)
        => new(id ?? Guid.NewGuid(), 1, 0x200F, "AirPods", -55, 70, 60, 80, Start,
            ThisPodInCase: true, LidClosed: false, LidCounter: 1);
}
