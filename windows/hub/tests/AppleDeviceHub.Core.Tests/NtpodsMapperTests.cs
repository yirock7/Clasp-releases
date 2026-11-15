using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Interop;
using NTPods.WinUI.Ipc;

namespace AppleDeviceHub.Core.Tests;

/// <summary>
/// Feeds NDJSON lines in the upstream daemon's wire format through the upstream parser
/// (linked unchanged) and then through our mapper. Proves the contract can represent the
/// engine's data; it says nothing about whether the hardware reports these values correctly.
/// </summary>
public class NtpodsMapperTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static DeviceState Map(string line)
    {
        var ev = Assert.IsType<DaemonEvent.State>(Wire.ParseEvent(line));
        return NtpodsSnapshotMapper.ToDeviceState(ev.Snapshot, "selected", 1, T0);
    }

    [Fact]
    public void Maps_a_connected_snapshot_and_keeps_sentinels_unknown()
    {
        var s = Map("""
            {"event":"state","connected":true,"link_stale":false,"dev_name":"AirPods Pro",
             "battery":{"left":80,"right":255,"case":null,"headphone":null,
                        "left_charging":false,"right_charging":true,"case_charging":false,"headphone_charging":false},
             "anc":2,"conversational_awareness":true,"model":"A3064"}
            """.Replace("\n", " "));

        Assert.Equal(DataOrigin.Hardware, s.Origin);
        Assert.Equal(80, s.Battery.Left.Level.Value);
        Assert.False(s.Battery.Right.Level.HasValue);   // 255 sentinel
        Assert.False(s.Battery.Right.Charging.HasValue); // flag without a level is dropped
        Assert.False(s.Battery.Case.Level.HasValue);
        Assert.Equal(ListeningMode.NoiseCancellation, s.Listening.Actual.Value);
        Assert.True(s.Link.ProtocolSession.Value);
        Assert.False(s.Link.Nearby.HasValue);           // the snapshot has no BLE fact
        Assert.False(s.Link.AudioConnected.HasValue);   // nor a Windows audio fact
        Assert.True(s.ConversationAwareness.Value);
    }

    [Fact]
    public void Stale_link_is_not_reported_as_a_working_session()
    {
        var s = Map("""{"event":"state","connected":true,"link_stale":true,"anc":3}""");
        Assert.False(s.Link.ProtocolSession.Value);
        Assert.False(s.ConversationAwareness.HasValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Unknown_anc_values_stay_unknown(int anc)
        => Assert.False(Map($$"""{"event":"state","connected":true,"anc":{{anc}}}""").Listening.Actual.HasValue);
}
