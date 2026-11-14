using System.Text;
using System.Text.Json.Nodes;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;

namespace AppleDeviceHub.Core.Tests.Engine;

/// <summary>Framing and message fixtures from windows/protocol/clasp-engine-v1 (client side).</summary>
public class EngineCodecTests
{
    public static TheoryData<string> FrameFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in EngineFixtures.Files("frames")) data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    public static TheoryData<string> MessageFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in EngineFixtures.Files("messages")) data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    [Fact]
    public void All_fixture_folders_are_present()
    {
        // At least the v1.0 set (12 frame cases, 22 messages, 5 sequences); more may be added.
        Assert.True(FrameFixtures().Count >= 12);
        Assert.True(MessageFixtures().Count >= 22);
        Assert.True(EngineFixtures.Files("sequences").Count() >= 5);
    }

    /// <summary>Read the bytes as a stream (frame layer), then parse the body (message layer).</summary>
    private static async Task<string> Classify(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var body = await EngineFraming.ReadFrameAsync(stream, CancellationToken.None);
            if (body is null) return "reject_truncated";
            EngineCodec.Parse(body);
            return "ok";
        }
        catch (EngineProtocolException ex)
        {
            return ex.Error switch
            {
                EngineFrameError.Oversized => "reject_oversized",
                EngineFrameError.ZeroLength => "reject_zero_length",
                EngineFrameError.Truncated => "reject_truncated",
                EngineFrameError.UnknownType => "reject_unknown_type",
                _ => "reject_malformed",
            };
        }
    }

    [Theory]
    [MemberData(nameof(FrameFixtures))]
    public async Task Frame_fixture_gives_the_expected_verdict(string name)
    {
        var fixture = EngineFixtures.Load("frames", name);
        var bytes = EngineFixtures.FromHex((string)fixture["bytes_hex"]!);
        Assert.Equal((string)fixture["expect"]!, await Classify(bytes));
    }

    [Fact]
    public async Task An_oversized_length_is_refused_before_reading_the_body()
    {
        // Header claims 65537 bytes; a stream that would block forever after the header proves no
        // body read (and no allocation of the claimed size) is attempted.
        var stream = new HeaderThenBlockStream(EngineFixtures.FromHex("01000100"));
        var ex = await Assert.ThrowsAsync<EngineProtocolException>(() => EngineFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Equal(EngineFrameError.Oversized, ex.Error);
    }

    [Fact]
    public async Task Partial_reads_are_reassembled()
    {
        var body = Encoding.UTF8.GetBytes(EngineFixtures.Message("hello").ToJsonString());
        var stream = new TrickleStream(EngineFraming.Encode(body));
        var read = await EngineFraming.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal(body, read);
        Assert.Null(await EngineFraming.ReadFrameAsync(stream, CancellationToken.None)); // clean end between frames
    }

    [Theory]
    [MemberData(nameof(MessageFixtures))]
    public void Message_fixture_parses_and_client_messages_round_trip(string name)
    {
        var fixture = EngineFixtures.Load("messages", name);
        var message = fixture["message"]!.AsObject();
        var parsed = EngineCodec.Parse(Encoding.UTF8.GetBytes(message.ToJsonString()));
        Assert.Equal((string)message["type"]!, parsed.Type);

        if ((string)fixture["direction"]! == "engine_to_client")
        {
            var engine = Assert.IsAssignableFrom<EngineMessage>(parsed);
            Assert.Equal((long)message["seq"]!, engine.Seq);
            return;
        }

        // Our own direction: what we produce parses back to an equal message...
        var client = Assert.IsAssignableFrom<ClientMessage>(parsed);
        var produced = client.ToUtf8Json();
        Assert.Equal(client, EngineCodec.Parse(produced));
        // ...and, apart from fields we do not know (hello_newer_minor's future_field), is the same JSON.
        var expected = message.DeepClone().AsObject();
        expected.Remove("future_field");
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(produced)), $"{name}: {Encoding.UTF8.GetString(produced)}");
    }

    [Fact]
    public void Observation_fields_keep_absent_and_unknown_apart()
    {
        var battery = Assert.IsType<ObservationMessage>(EngineCodec.Parse(Bytes("observation_battery")));
        Assert.Equal(new ObservedBattery(82, false), battery.Fields.BatteryLeft);
        Assert.Equal(new ObservedBattery(null, null), battery.Fields.BatteryCase); // reported, value unknown
        Assert.Null(battery.Fields.ListeningMode);                                 // not in this packet
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 18, 4, 5, 123, TimeSpan.Zero), battery.ObservedAt);
        Assert.Equal(TimeSpan.Zero, battery.ObservedAt.Offset);

        var mode = Assert.IsType<ObservationMessage>(EngineCodec.Parse(Bytes("observation_mode_unknown")));
        Assert.Equal(EngineListeningMode.Unrecognised, mode.Fields.ListeningMode);
        Assert.Null(mode.Fields.BatteryLeft);
    }

    [Fact]
    public void Device_entries_keep_unknown_windows_link_unknown()
    {
        var devices = Assert.IsType<DevicesMessage>(EngineCodec.Parse(Bytes("devices_unknown_link")));
        var device = Assert.Single(devices.Devices);
        Assert.Equal(DriverInterfaceStatus.NotFound, device.DriverInterface);
        Assert.Null(device.WindowsConnected);
    }

    [Fact]
    public void Session_writable_modes_map_to_contract_modes()
    {
        var session = Assert.IsType<SessionMessage>(EngineCodec.Parse(Bytes("session_open_control")));
        Assert.Equal(new[] { ListeningMode.NoiseCancellation, ListeningMode.Transparency, ListeningMode.Adaptive }, session.WritableModes);
        Assert.Equal(EngineSessionState.Open, session.State);
    }

    [Fact]
    public void A_client_direction_type_from_the_engine_is_refused()
    {
        var ex = Assert.Throws<EngineProtocolException>(() => EngineCodec.ParseEngineMessage(Bytes("hello")));
        Assert.Equal(EngineFrameError.UnknownType, ex.Error);
    }

    private static byte[] Bytes(string name) => Encoding.UTF8.GetBytes(EngineFixtures.Message(name).ToJsonString());

    /// <summary>Returns at most one byte per read.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }

    /// <summary>Serves a header, then blocks forever: any body read would hang the test.</summary>
    private sealed class HeaderThenBlockStream(byte[] header) : MemoryStream(header)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
