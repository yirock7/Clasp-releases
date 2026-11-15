using AppleDeviceHub.Core.Audio;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AppleDeviceHub.Core.Tests;

public sealed class KeepAlivePolicyTests
{
    private const string Id = "{0.0.0.00000000}.{abc}";

    [Fact]
    public void RunsOnlyWhenEverythingLinesUp()
    {
        var p = new KeepAlivePolicy(new FakeTimeProvider());
        Assert.Equal(KeepAliveReason.Disabled, p.Evaluate(false, true, Id, Id).Reason);
        Assert.Equal(KeepAliveReason.NotConnected, p.Evaluate(true, false, Id, Id).Reason);
        Assert.Equal(KeepAliveReason.NoEndpoint, p.Evaluate(true, true, null, Id).Reason);
        Assert.Equal(KeepAliveReason.NotDefaultOutput, p.Evaluate(true, true, Id, "other").Reason);
        Assert.Equal(KeepAliveReason.NotDefaultOutput, p.Evaluate(true, true, Id, null).Reason);
        Assert.True(p.Evaluate(true, true, Id, Id.ToUpperInvariant()).ShouldRun);
    }

    [Fact]
    public void FailureBacksOffThenAllowsRetry()
    {
        var time = new FakeTimeProvider();
        var p = new KeepAlivePolicy(time);
        Assert.True(p.Evaluate(true, true, Id, Id).ShouldRun);
        p.ReportFailure();
        var d = p.Evaluate(true, true, Id, Id);
        Assert.False(d.ShouldRun);
        Assert.Equal(KeepAliveReason.BackingOff, d.Reason);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(30), d.RetryAt);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(p.Evaluate(true, true, Id, Id).ShouldRun);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(p.Evaluate(true, true, Id, Id).ShouldRun);
    }

    [Fact]
    public void GivesUpAfterMaxRetriesUntilReconnect()
    {
        var time = new FakeTimeProvider();
        var p = new KeepAlivePolicy(time, maxRetriesPerConnection: 2);
        Assert.True(p.Evaluate(true, true, Id, Id).ShouldRun);
        for (var i = 0; i < 3; i++)
        {
            p.ReportFailure();
            time.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(KeepAliveReason.RetriesExhausted, p.Evaluate(true, true, Id, Id).Reason);

        Assert.Equal(KeepAliveReason.NotConnected, p.Evaluate(true, false, Id, Id).Reason);
        Assert.True(p.Evaluate(true, true, Id, Id).ShouldRun);
        Assert.Equal(0, p.FailureCount);
    }

    [Fact]
    public void ResetClearsBackoff()
    {
        var p = new KeepAlivePolicy(new FakeTimeProvider());
        Assert.True(p.Evaluate(true, true, Id, Id).ShouldRun);
        p.ReportFailure();
        p.Reset();
        Assert.True(p.Evaluate(true, true, Id, Id).ShouldRun);
    }

    [Fact]
    public void ZeroRetriesStopsAfterFirstFailure()
    {
        var time = new FakeTimeProvider();
        var p = new KeepAlivePolicy(time, maxRetriesPerConnection: 0);
        Assert.True(p.Evaluate(true, true, Id, Id).ShouldRun);
        p.ReportFailure();
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(KeepAliveReason.RetriesExhausted, p.Evaluate(true, true, Id, Id).Reason);
    }
}
