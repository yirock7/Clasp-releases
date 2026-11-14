using System.Globalization;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;

namespace AppleDeviceHub.Core.Tests.Ble;

// ATTRIBUTION: most status, pods and flags/case bytes below (data[5] to data[7]) come from the public
// test suites of CAPod (https://github.com/d4rken-org/capod) and MagicPodsCore
// (https://github.com/steam3d/MagicPodsCore), both GPL-3.0, padded here to a full 27-byte frame with
// colour and connection bytes. Some frames are synthetic variations written for this project, not
// taken from those suites: the Max not-worn frame (status 0x42), the Studio Buds+ frame, the unlisted-id
// frames and the Max USB-C variants. None of these models has been tested on hardware for this project.

/// <summary>Frames per model layout, plus catalog checks.</summary>
public class ModelLayoutFrameTests
{
    private static ProximityMessage Parse(ushort model, byte status, byte pods, byte flagsAndCase)
    {
        var frame = Payload.Build(model, status, pods, flagsAndCase, lid: 0x0B, color: 0x01, connection: 0x05);
        Assert.Equal(27, frame.Length);
        var m = ProximityMessageParser.Parse(frame);
        Assert.NotNull(m);
        return m;
    }

    private static void AssertSingle(ProximityMessage m, int percent)
    {
        Assert.Equal(BatteryLayout.SingleBattery, m.Layout);
        Assert.Equal(percent, m.LeftBatteryPercent);
        Assert.Null(m.RightBatteryPercent);
        Assert.Null(m.CaseBatteryPercent);
        Assert.Null(m.PrimaryIsLeft);
        Assert.Null(m.LeftCharging);
        Assert.Null(m.RightCharging);
        Assert.Null(m.CaseCharging);
        Assert.Null(m.ThisPodInCase);
        Assert.Null(m.OnePodInCase);
        Assert.Null(m.BothPodsInCase);
        Assert.Null(m.LidCounter);
        Assert.Null(m.LidClosed);
    }

    [Theory]
    [InlineData((ushort)0x1020, (byte)0x0A, (byte)0xF4, (byte)0x8F, 40)] // Beats Flex
    [InlineData((ushort)0x0520, (byte)0x00, (byte)0xF5, (byte)0x0F, 50)] // BeatsX
    [InlineData((ushort)0x0D20, (byte)0x00, (byte)0x07, (byte)0x80, 70)] // Powerbeats 4
    [InlineData((ushort)0x3820, (byte)0x00, (byte)0x07, (byte)0x80, 70)] // Beats 360
    [InlineData((ushort)0x2520, (byte)0x00, (byte)0x09, (byte)0x80, 90)] // Beats Solo 4
    [InlineData((ushort)0x1720, (byte)0x00, (byte)0x06, (byte)0x00, 60)] // Beats Studio Pro
    public void Single_battery_beats_frames_decode_one_battery_and_no_ears(ushort model, byte status, byte pods, byte fc, int percent)
    {
        var m = Parse(model, status, pods, fc);
        AssertSingle(m, percent);
        Assert.Null(m.LeftInEar);
        Assert.Null(m.RightInEar);
        Assert.False(m.HasEarBits);
    }

    [Fact]
    public void Airpods_max_decodes_one_battery_and_is_worn_from_bit_5()
    {
        var m = Parse(0x0A20, 0x62, 0x04, 0x80);
        AssertSingle(m, 40);
        Assert.True(m.LeftInEar);
        Assert.True(m.RightInEar);
        Assert.True(m.PrimaryInEar);
        Assert.True(m.SecondaryInEar);
        Assert.True(m.HasEarBits);
    }

    [Fact]
    public void Airpods_max_not_worn_when_bit_5_is_clear()
    {
        var m = Parse(0x0A20, 0x42, 0x04, 0x80);
        Assert.False(m.LeftInEar);
        Assert.False(m.RightInEar);
    }

    [Theory]
    [InlineData((byte)0x2B, (byte)0xF6)]
    [InlineData((byte)0x00, (byte)0x06)]
    [InlineData((byte)0x62, (byte)0x04)]
    public void Airpods_max_usb_c_has_no_ear_values_whatever_the_status(byte status, byte pods)
    {
        var m = Parse(0x1F20, status, pods, 0x8F);
        Assert.Equal(BatteryLayout.SingleBattery, m.Layout);
        Assert.Null(m.LeftInEar);
        Assert.Null(m.RightInEar);
        Assert.False(m.HasEarBits);
    }

    [Fact]
    public void Airpods_max_2_decodes_one_battery_with_both_sides_worn()
    {
        var m = Parse(0x2D20, 0x2B, 0xF6, 0x8F);
        AssertSingle(m, 60);
        Assert.True(m.LeftInEar);
        Assert.True(m.RightInEar);
    }

    [Theory]
    [InlineData((byte)0x21, false, false)] // none worn
    [InlineData((byte)0x23, true, false)]  // bit 1: left only
    [InlineData((byte)0x29, false, true)]  // bit 3: right only
    public void Airpods_max_2_wear_comes_from_bits_1_and_3(byte status, bool left, bool right)
    {
        var m = Parse(0x2D20, status, 0xF6, 0x8F);
        Assert.Equal(left, m.LeftInEar);
        Assert.Equal(right, m.RightInEar);
        Assert.Equal(left, m.PrimaryInEar);
        Assert.Equal(right, m.SecondaryInEar);
    }

    [Fact]
    public void Beats_solo_buds_have_two_buds_and_no_case_battery()
    {
        var m = Parse(0x2620, 0x33, 0x89, 0x80);
        Assert.Equal(BatteryLayout.EarbudsNoCaseBattery, m.Layout);
        Assert.Equal(90, m.LeftBatteryPercent);
        Assert.Equal(80, m.RightBatteryPercent);
        Assert.Null(m.CaseBatteryPercent);
        Assert.Null(m.CaseCharging);
        Assert.Null(m.LidCounter);
        Assert.Null(m.LidClosed);
        Assert.False(m.LeftCharging);
        Assert.False(m.RightCharging);
        Assert.Null(m.ThisPodInCase);
        Assert.Null(m.OnePodInCase);
        Assert.Null(m.BothPodsInCase);
        Assert.False(m.HasEarBits);
    }

    [Fact]
    public void Powerbeats_pro_decodes_buds_case_and_charging()
    {
        var m = Parse(0x0B20, 0x54, 0xAA, 0xB5);
        Assert.Equal(BatteryLayout.EarbudsWithCase, m.Layout);
        Assert.Equal(100, m.LeftBatteryPercent);
        Assert.Equal(100, m.RightBatteryPercent);
        Assert.Equal(50, m.CaseBatteryPercent);
        Assert.True(m.LeftCharging);
        Assert.True(m.RightCharging);
        Assert.False(m.CaseCharging);
        Assert.True(m.HasEarBits);
    }

    [Fact]
    public void Beats_fit_pro_leaves_unreported_right_and_case_null()
    {
        var m = Parse(0x1220, 0x20, 0xFA, 0x8F);
        Assert.Equal(BatteryLayout.EarbudsWithCase, m.Layout);
        Assert.Equal(100, m.LeftBatteryPercent);
        Assert.Null(m.RightBatteryPercent);
        Assert.Null(m.CaseBatteryPercent);
    }

    [Fact]
    public void Case_model_without_a_wear_rule_keeps_batteries_and_case_but_no_ear_fields()
    {
        // Beats Studio Buds+: two buds and a case, no wear rule.
        var m = Parse(0x1620, 0x22, 0x58, 0x47);
        Assert.Equal(BatteryLayout.EarbudsWithCase, m.Layout);
        Assert.Equal(80, m.LeftBatteryPercent);
        Assert.Equal(50, m.RightBatteryPercent);
        Assert.Equal(70, m.CaseBatteryPercent);
        Assert.True(m.CaseCharging);
        Assert.NotNull(m.ThisPodInCase);
        Assert.NotNull(m.LidCounter);
        Assert.Null(m.LeftInEar);
        Assert.Null(m.RightInEar);
        Assert.Null(m.PrimaryInEar);
        Assert.Null(m.SecondaryInEar);
        Assert.False(m.HasEarBits);
    }

    [Fact]
    public void Unlisted_id_with_only_the_low_nibble_is_a_single_battery_without_ears_or_lid()
    {
        var m = Parse(Payload.Unlisted, 0x00, 0x07, 0x80);
        AssertSingle(m, 70);
        Assert.Null(m.LeftInEar);
        Assert.Null(m.RightInEar);
        Assert.False(m.HasEarBits);
    }

    [Fact]
    public void Unlisted_id_with_buds_and_case_uses_the_earbud_layout_without_ears_or_lid()
    {
        var m = Parse(Payload.Unlisted, 0x55, 0xAA, 0xB7);
        Assert.Equal(BatteryLayout.EarbudsWithCase, m.Layout);
        Assert.Equal(100, m.LeftBatteryPercent);
        Assert.Equal(100, m.RightBatteryPercent);
        Assert.Equal(70, m.CaseBatteryPercent);
        Assert.NotNull(m.LeftCharging);
        Assert.Null(m.ThisPodInCase);
        Assert.Null(m.OnePodInCase);
        Assert.Null(m.BothPodsInCase);
        Assert.Null(m.LidCounter);
        Assert.Null(m.LidClosed);
        Assert.Null(m.LeftInEar);
        Assert.Null(m.RightInEar);
        Assert.False(m.HasEarBits);
    }

    [Fact]
    public void Unlisted_id_treats_a_zero_nibble_as_absent()
    {
        // Low bud 9, high bud 0 (absent), case 5: not single, right bud absent.
        var m = Parse(Payload.Unlisted, 0x20, 0x09, 0x05);
        Assert.Equal(BatteryLayout.EarbudsWithCase, m.Layout);
        Assert.Equal(90, m.LeftBatteryPercent);
        Assert.Null(m.RightBatteryPercent);
        Assert.Equal(50, m.CaseBatteryPercent);
    }

    [Fact]
    public void Listed_earbud_zero_nibble_is_still_a_real_zero()
    {
        var m = Parse(0x2720, 0x22, 0x50, 0x07);
        Assert.Equal(0, m.LeftBatteryPercent);
        Assert.Equal(50, m.RightBatteryPercent);
    }

    // ---- catalog ----

    private static readonly ushort[] ExpectedIds =
    [
        0x0220, 0x0F20, 0x1320, 0x1920, 0x1B20, 0x3620, 0x3020, 0x0E20, 0x1420, 0x2420, 0x2720,
        0x0A20, 0x1F20, 0x2D20,
        0x0320, 0x0D20, 0x0B20, 0x1D20, 0x2F20, 0x0520, 0x1020, 0x0620, 0x0C20, 0x2520, 0x2620,
        0x0920, 0x1720, 0x3820, 0x1120, 0x1620, 0x1220,
    ];

    [Fact]
    public void Catalog_lists_the_31_models_once_each()
    {
        Assert.Equal(31, ExpectedIds.Length);
        Assert.Equal(31, AppleModelCatalog.ListedIds.Count);
        Assert.Equal(31, AppleModelCatalog.ListedIds.Distinct().Count());
        Assert.Equal(ExpectedIds.OrderBy(i => i), AppleModelCatalog.ListedIds.OrderBy(i => i));
    }

    [Fact]
    public void Every_windows_pid_converts_back_to_its_wire_id()
    {
        foreach (var id in AppleModelCatalog.ListedIds)
        {
            // Windows shows the id byte-swapped: wire 0x2720 is PID 2027.
            string pid = ((ushort)((id << 8) | (id >> 8))).ToString("X4", CultureInfo.InvariantCulture);
            string windowsId = $@"BTHENUM\DEV_AABBCCDDEEFF\VID&0001004C_PID&{pid}";
            Assert.Equal(id, AppleBluetoothIdentity.TryParseModelId(windowsId));
        }
    }

    [Theory]
    [InlineData((ushort)0x0220, "AirPods (1st generation)")]
    [InlineData((ushort)0x0F20, "AirPods (2nd generation)")]
    [InlineData((ushort)0x1320, "AirPods (3rd generation)")]
    [InlineData((ushort)0x1920, "AirPods 4")]
    [InlineData((ushort)0x1B20, "AirPods 4 (ANC)")]
    [InlineData((ushort)0x0A20, "AirPods Max (Lightning)")]
    [InlineData((ushort)0x1F20, "AirPods Max (USB-C)")]
    [InlineData((ushort)0x0E20, "AirPods Pro")]
    [InlineData((ushort)0x1420, "AirPods Pro 2 (Lightning)")]
    [InlineData((ushort)0x2420, "AirPods Pro 2 (USB-C)")]
    [InlineData((ushort)0x2720, "AirPods Pro 3")]
    public void Original_model_names_are_unchanged(ushort id, string name)
        => Assert.Equal(name, AppleModelCatalog.NameOf(id));

    [Fact]
    public void Find_returns_null_for_an_unlisted_id_and_the_row_for_a_listed_one()
    {
        Assert.Null(AppleModelCatalog.Find(Payload.Unlisted));
        var row = AppleModelCatalog.Find(0x1720);
        Assert.NotNull(row);
        Assert.Equal("Beats Studio Pro", row.Name);
        Assert.Equal(ModelFamily.Beats, row.Family);
        Assert.Equal(BatteryLayout.SingleBattery, row.Layout);
        Assert.Equal(WearRule.None, row.Wear);
    }

    [Fact]
    public void Unrecognised_name_and_family_noun()
    {
        Assert.Equal("Unrecognised Apple audio device", AppleModelCatalog.UnrecognisedName);
        Assert.Equal("AirPods", AppleModelCatalog.FamilyNounOf(0x2720));
        Assert.Equal("Beats", AppleModelCatalog.FamilyNounOf(0x1720));
        Assert.Equal("headphones", AppleModelCatalog.FamilyNounOf(Payload.Unlisted));
        Assert.Equal("headphones", AppleModelCatalog.FamilyNounOf(null));
    }
}
