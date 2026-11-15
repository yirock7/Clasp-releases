using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>
/// Parser table. Expected values follow the UPSTREAM-ASSUMED layout (LibrePods 4589176,
/// linux/ble/blemanager.cpp); they are not verified against AirPods Pro 3 broadcasts.
/// </summary>
public class ProximityMessageParserTests
{
    [Fact]
    public void Primary_left_payload_decodes_every_field()
    {
        var m = ProximityMessageParser.Parse(Payload.Build());

        Assert.NotNull(m);
        Assert.Equal((byte)0x01, m.PairedFlag);
        Assert.Equal(Payload.ProTwoUsbC, m.ModelId);
        Assert.Equal((byte)0x22, m.Status);
        Assert.True(m.PrimaryIsLeft);
        // Not flipped: left = low nibble (8), right = high nibble (5).
        Assert.Equal(80, m.LeftBatteryPercent);
        Assert.Equal(50, m.RightBatteryPercent);
        Assert.Equal(70, m.CaseBatteryPercent);
        Assert.Equal((byte)0x4, m.ChargingFlags);
        Assert.False(m.LeftCharging);
        Assert.False(m.RightCharging);
        Assert.True(m.CaseCharging);
        Assert.False(m.ThisPodInCase);
        Assert.False(m.OnePodInCase);
        Assert.False(m.BothPodsInCase);
        // No swap: bit 0x02 -> left, bit 0x08 -> right.
        Assert.True(m.LeftInEar);
        Assert.False(m.RightInEar);
        Assert.True(m.PrimaryInEar);
        Assert.False(m.SecondaryInEar);
        Assert.True(m.HasEarBits);
        Assert.Equal((byte)3, m.LidCounter);
        Assert.Null(m.LidClosed); // only trusted when the broadcasting bud is in the case
        Assert.Equal((byte)0x01, m.ColorCode);
        Assert.Equal((byte)0x05, m.ConnectionStateCode);
        Assert.NotNull(m.EncryptedPayload);
        Assert.Equal(16, m.EncryptedPayload.Length);
        Assert.Equal(0xA0, m.EncryptedPayload[0]);
    }

    [Fact]
    public void Right_primary_swaps_battery_charging_and_ear_sides()
    {
        // Status 0x02: bit 0x20 clear -> right primary -> values flipped (upstream assumption).
        var m = ProximityMessageParser.Parse(Payload.Build(status: 0x02, pods: 0x58, flagsAndCase: 0x17));

        Assert.NotNull(m);
        Assert.False(m.PrimaryIsLeft);
        Assert.Equal(50, m.LeftBatteryPercent);
        Assert.Equal(80, m.RightBatteryPercent);
        Assert.True(m.RightCharging);   // flags bit0 belongs to the right bud when flipped
        Assert.False(m.LeftCharging);
        Assert.False(m.CaseCharging);
        // Swap (flipped XOR not-in-case): bit 0x02 -> right.
        Assert.True(m.RightInEar);
        Assert.False(m.LeftInEar);
        Assert.True(m.PrimaryInEar);    // primary is the right bud
        Assert.False(m.SecondaryInEar);
    }

    [Fact]
    public void Broadcasting_bud_in_case_swaps_ear_bits_and_reports_lid()
    {
        // 0x20 primary left, 0x40 this bud in case, 0x08 in-ear bit.
        var m = ProximityMessageParser.Parse(Payload.Build(status: 0x68, lid: 0x09));

        Assert.NotNull(m);
        Assert.True(m.ThisPodInCase);
        Assert.True(m.LeftInEar);   // XOR -> bit 0x08 now belongs to the left bud
        Assert.False(m.RightInEar);
        Assert.Equal((byte)1, m.LidCounter);
        Assert.True(m.LidClosed);
    }

    [Theory]
    [InlineData((byte)0x30, true, false)]  // one in case (0x10), primary left (0x20)
    [InlineData((byte)0x04, false, true)]  // both in case
    public void Case_placement_bits_decode(byte status, bool one, bool both)
    {
        var m = ProximityMessageParser.Parse(Payload.Build(status: status));
        Assert.NotNull(m);
        Assert.Equal(one, m.OnePodInCase);
        Assert.Equal(both, m.BothPodsInCase);
    }

    [Theory]
    [InlineData((byte)0xFF, (byte)0x0F)]   // 15 = unknown (upstream)
    [InlineData((byte)0xBC, (byte)0x0B)]   // 11..14: not a level; treated as unknown here (our choice)
    public void Unknown_nibbles_are_unknown_not_zero(byte pods, byte flagsAndCase)
    {
        var m = ProximityMessageParser.Parse(Payload.Build(pods: pods, flagsAndCase: flagsAndCase));
        Assert.NotNull(m);
        Assert.Null(m.LeftBatteryPercent);
        Assert.Null(m.RightBatteryPercent);
        Assert.Null(m.CaseBatteryPercent);
    }

    [Fact]
    public void Zero_and_full_nibbles_are_real_levels()
    {
        var m = ProximityMessageParser.Parse(Payload.Build(pods: 0xA0, flagsAndCase: 0x0A));
        Assert.NotNull(m);
        Assert.Equal(0, m.LeftBatteryPercent);
        Assert.Equal(100, m.RightBatteryPercent);
        Assert.Equal(100, m.CaseBatteryPercent);
    }

    [Fact]
    public void Short_but_well_formed_message_is_refused()
    {
        // type, len=4, paired, model hi, model lo, status: a real broadcast declares 25 bytes.
        byte[] data = [0x07, 0x04, 0x01, 0x24, 0x20, 0x22];
        Assert.Null(ProximityMessageParser.Parse(data));
    }

    [Fact]
    public void Message_without_encrypted_tail_is_refused()
        => Assert.Null(ProximityMessageParser.Parse(Payload.Build(encrypted: false)));

    [Theory]
    [InlineData(18)]
    [InlineData(24)]
    [InlineData(26)]
    public void Body_length_other_than_25_is_refused(int bodyLength)
    {
        // Same bytes as a good frame, but declaring a different length (padded so it is never truncated).
        var good = Payload.Build();
        var data = new byte[40];
        good.CopyTo(data, 0);
        data[1] = (byte)bodyLength;
        Assert.Null(ProximityMessageParser.Parse(data));
    }

    public static TheoryData<byte[]> Rejected => new()
    {
        Array.Empty<byte>(),
        new byte[] { 0x07 },
        new byte[] { 0x07, 0x02, 0x01, 0x24 },                 // too short for a model id
        new byte[] { 0x07, 0x19, 0x01, 0x24, 0x20, 0x22 },     // declared 25 bytes, only 4 present (truncated)
        new byte[] { 0x10, 0x05, 0x01, 0x24, 0x20, 0x22, 0x00 }, // other Apple type (nearby info)
        new byte[] { 0x07, 0x05, 0x00, 0x24, 0x20, 0x22, 0x00 }, // pairing mode: different layout, not decoded
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Malformed_short_or_other_type_is_null(byte[] data) => Assert.Null(ProximityMessageParser.Parse(data));

    [Fact]
    public void Other_company_id_is_null_even_with_a_valid_body()
        => Assert.Null(ProximityMessageParser.Parse(0x0006, Payload.Build()));

    [Fact]
    public void Apple_company_id_overload_parses()
        => Assert.NotNull(ProximityMessageParser.Parse(ProximityMessage.AppleCompanyId, Payload.Build()));

    [Fact]
    public void Random_garbage_never_throws()
    {
        var random = new Random(4589176);
        for (int i = 0; i < 5000; i++)
        {
            var data = new byte[random.Next(0, 40)];
            random.NextBytes(data);
            if (i % 2 == 0 && data.Length > 1) data[0] = 0x07; // exercise the proximity path too
            _ = ProximityMessageParser.Parse(data);
        }
    }
}
