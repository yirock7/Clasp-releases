// SPDX-License-Identifier: GPL-3.0-or-later
//
// NOTICE / attribution: the byte layout decoded in this file is described from LibrePods
// (https://github.com/kavishdevar/librepods), commit 4589176f7b7943a6091d5c6907c74f441bcf7780,
// file linux/ble/blemanager.cpp (BleManager::onDeviceDiscovered), licensed GPL-3.0. This is an
// independent re-implementation written from that description, not a copy of the upstream code.
// Every layout fact below is an UPSTREAM ASSUMPTION: none of it has been verified on AirPods Pro 3
// (upstream's model table has no Pro 3 entry at that commit).

namespace AppleDeviceHub.Core.Ble;

/// <summary>
/// One decoded Apple "proximity pairing" broadcast (manufacturer data, company 0x004C, type 0x07).
/// Every field is optional: <c>null</c> means the byte was absent or held the "unknown" value.
/// Values are approximate (battery in 10 % steps) and the sender's identity is never confirmed
/// (that needs the IRK, which only the AAP session provides).
/// </summary>
public sealed record ProximityMessage
{
    /// <summary>Apple's Bluetooth SIG company identifier.</summary>
    public const ushort AppleCompanyId = 0x004C;

    /// <summary>Apple continuity message type for proximity pairing (upstream assumption).</summary>
    public const byte ProximityPairingType = 0x07;

    /// <summary>The TLV body (bytes after type and length) exactly as heard; used for duplicate suppression.</summary>
    public required byte[] Raw { get; init; }

    /// <summary>data[2]. Always 0x01 here: 0x00 (pairing mode) and every other value are refused by the parser.</summary>
    public byte? PairedFlag { get; init; }

    /// <summary>
    /// data[3..4], big-endian: the canonical (wire-order) model id, e.g. bytes 27 20 = 0x2720. Windows
    /// device ids carry it byte-swapped (see <see cref="Connection.AppleBluetoothIdentity"/>). Only a
    /// model hint; never proof of identity.
    /// </summary>
    public ushort? ModelId { get; init; }

    /// <summary>data[5], raw status bits.</summary>
    public byte? Status { get; init; }

    /// <summary>Upstream: status bit 0x20 set = left bud is primary; clear = right bud primary (values flipped).</summary>
    public bool? PrimaryIsLeft { get; init; }

    /// <summary>
    /// What the batteries mean for this model. For <see cref="BatteryLayout.SingleBattery"/> the one battery
    /// is held in <see cref="LeftBatteryPercent"/> and every other battery, charging, in-case and lid field is null.
    /// </summary>
    public BatteryLayout Layout { get; init; } = BatteryLayout.EarbudsWithCase;

    /// <summary>data[6], raw battery nibbles before the left/right flip.</summary>
    public byte? PodBatteryByte { get; init; }

    /// <summary>Left bud battery in percent (nibble x 10); null when the nibble is 15 (unknown) or out of range. The only battery for a single-battery model.</summary>
    public int? LeftBatteryPercent { get; init; }

    public int? RightBatteryPercent { get; init; }

    /// <summary>data[7] lower nibble x 10; null when 15 (unknown) or out of range.</summary>
    public int? CaseBatteryPercent { get; init; }

    /// <summary>data[7] upper nibble, raw charging flags.</summary>
    public byte? ChargingFlags { get; init; }

    public bool? LeftCharging { get; init; }

    public bool? RightCharging { get; init; }

    public bool? CaseCharging { get; init; }

    /// <summary>Status 0x40: the broadcasting bud is in the case.</summary>
    public bool? ThisPodInCase { get; init; }

    /// <summary>Status 0x10: one bud is in the case.</summary>
    public bool? OnePodInCase { get; init; }

    /// <summary>Status 0x04: both buds are in the case.</summary>
    public bool? BothPodsInCase { get; init; }

    /// <summary>Status 0x02 / 0x08 after the upstream XOR flip. Approximate; display only.</summary>
    public bool? LeftInEar { get; init; }

    public bool? RightInEar { get; init; }

    /// <summary>In-ear for the AirPods' current primary / secondary role (derived from the left/right values).</summary>
    public bool? PrimaryInEar { get; init; }

    public bool? SecondaryInEar { get; init; }

    /// <summary>data[8] bits 0..2: lid-open counter.</summary>
    public byte? LidCounter { get; init; }

    /// <summary>data[8] bit 3 (1 = closed per upstream's enum); only reported when the broadcasting bud is in the case.</summary>
    public bool? LidClosed { get; init; }

    /// <summary>data[9], colour code (upstream table; not decoded to a name here).</summary>
    public byte? ColorCode { get; init; }

    /// <summary>data[10], connection-state code (upstream: 0x00 disconnected, 0x04 idle, 0x05 music, ...). Raw only.</summary>
    public byte? ConnectionStateCode { get; init; }

    /// <summary>Trailing 16 bytes (encrypted; needs the device's key, which this app never has). Null when absent.</summary>
    public byte[]? EncryptedPayload { get; init; }

    /// <summary>True when wear bits were decoded (the model has a wear rule and the status byte is present).</summary>
    public bool HasEarBits => LeftInEar.HasValue && RightInEar.HasValue;
}

/// <summary>
/// Decodes Apple proximity-pairing manufacturer data into a <see cref="ProximityMessage"/>.
/// Pure and total: any malformed, wrong-length, truncated, pairing-mode or other-type input yields
/// <c>null</c>; it never throws on bad bytes. Layout is an upstream assumption (see file header).
/// </summary>
public static class ProximityMessageParser
{
    /// <summary>Header (type, length) plus paired flag and the two model bytes.</summary>
    private const int MinimumTotalLength = 5;

    /// <summary>
    /// Declared body length of a real proximity broadcast (paired flag through the 16-byte encrypted block).
    /// Other lengths, such as known 19-byte clones, are refused.
    /// </summary>
    private const int ExpectedBodyLength = 25;

    private const int EncryptedLength = 16;

    /// <summary>data[2] of the paired-state layout this decoder understands (observed on the owner's AirPods Pro 3).</summary>
    private const byte PairedBroadcastFlag = 0x01;

    /// <summary>Highest nibble that is a battery level (10 x 10 = 100 %). 15 = unknown upstream; 11..14 treated as unknown here.</summary>
    private const int MaxBatteryNibble = 10;

    /// <summary>Parse manufacturer data for <paramref name="companyId"/>; null unless it is Apple's.</summary>
    public static ProximityMessage? Parse(ushort companyId, ReadOnlySpan<byte> manufacturerData)
        => companyId == ProximityMessage.AppleCompanyId ? Parse(manufacturerData) : null;

    /// <summary>
    /// Parse Apple manufacturer data (company id already stripped, as Windows and Qt deliver it):
    /// data[0] = type, data[1] = length of the following body.
    /// </summary>
    public static ProximityMessage? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumTotalLength) return null;
        if (data[0] != ProximityMessage.ProximityPairingType) return null;

        // The declared length must fit in what was heard: a truncated message is refused rather
        // than decoded from whatever bytes follow (stricter than upstream, which checks size >= 10).
        int bodyLength = data[1];
        if (bodyLength != ExpectedBodyLength) return null;
        if (bodyLength + 2 > data.Length) return null;
        var tlv = data[..(bodyLength + 2)];

        byte paired = tlv[2];
        // Upstream skips pairing-mode messages (0x00): their body is structured differently. A real paired
        // broadcast was captured with 0x01 only. Anything else, notably the short encrypted variant
        // (07 11 06 ...) whose "model" changes on every message, is not this layout and is refused.
        if (paired != PairedBroadcastFlag) return null;

        ushort model = (ushort)((tlv[3] << 8) | tlv[4]);
        byte? status = At(tlv, 5);
        byte? pods = At(tlv, 6);
        byte? flagsAndCase = At(tlv, 7);
        byte? lid = At(tlv, 8);

        var info = AppleModelCatalog.Find(model);
        var layout = info?.Layout ?? BatteryLayout.EarbudsWithCase;
        var wear = info?.Wear ?? WearRule.None;
        bool listed = info is not null;

        // An unlisted id has no table row, so judge the layout from the nibbles: a lone low
        // nibble with nothing else filled in looks like a single-battery device.
        if (!listed && pods is byte up && flagsAndCase is byte uf
            && LevelOrAbsent(up & 0x0F) is not null
            && LevelOrAbsent((up >> 4) & 0x0F) is null
            && LevelOrAbsent(uf & 0x0F) is null)
        {
            layout = BatteryLayout.SingleBattery;
        }

        bool single = layout == BatteryLayout.SingleBattery;
        bool hasCaseBattery = layout == BatteryLayout.EarbudsWithCase;

        bool? primaryLeft = !single && status is byte s0 ? (s0 & 0x20) != 0 : null;
        // Upstream assumption: values are swapped when the right bud is primary (bit 0x20 clear).
        bool flipped = primaryLeft == false;

        // Unlisted ids read a 0 nibble as "not there" rather than "empty", because the id cannot say which it is.
        Func<int, int?> level = listed ? Percent : LevelOrAbsent;

        int? left = null, right = null;
        if (pods is byte p)
        {
            int low = p & 0x0F, high = (p >> 4) & 0x0F;
            if (single)
            {
                left = level(low);
            }
            else
            {
                left = level(flipped ? high : low);
                right = level(flipped ? low : high);
            }
        }

        int? casePercent = null;
        byte? flags = null;
        bool? leftCharging = null, rightCharging = null, caseCharging = null;
        if (flagsAndCase is byte fc && !single)
        {
            if (hasCaseBattery) casePercent = level(fc & 0x0F);
            flags = (byte)((fc >> 4) & 0x0F);
            leftCharging = flipped ? (flags & 0x02) != 0 : (flags & 0x01) != 0;
            rightCharging = flipped ? (flags & 0x01) != 0 : (flags & 0x02) != 0;
            if (hasCaseBattery) caseCharging = (flags & 0x04) != 0;
        }

        // In-case, lid and ear fields come only from a listed earbud row. Unlisted ids get none of them.
        bool? thisInCase = null, oneInCase = null, bothInCase = null;
        if (listed && hasCaseBattery && status is byte sc)
        {
            thisInCase = (sc & 0x40) != 0;
            oneInCase = (sc & 0x10) != 0;
            bothInCase = (sc & 0x04) != 0;
        }

        bool? leftEar = null, rightEar = null, primaryEar = null, secondaryEar = null;
        if (status is byte s)
        {
            switch (wear)
            {
                case WearRule.EarbudBits when !single:
                    // Upstream XOR rule: which in-ear bit belongs to which side depends on both the
                    // primary flip and whether the broadcasting bud is in the case.
                    bool swapEarBits = flipped ^ (thisInCase ?? false);
                    bool bit08 = (s & 0x08) != 0, bit02 = (s & 0x02) != 0;
                    leftEar = swapEarBits ? bit08 : bit02;
                    rightEar = swapEarBits ? bit02 : bit08;
                    primaryEar = primaryLeft == true ? leftEar : rightEar;
                    secondaryEar = primaryLeft == true ? rightEar : leftEar;
                    break;
                case WearRule.HeadsetBit5:
                    leftEar = rightEar = (s & 0x20) != 0;
                    primaryEar = leftEar;
                    secondaryEar = rightEar;
                    break;
                case WearRule.HeadsetBits1And3:
                    leftEar = (s & 0x02) != 0;
                    rightEar = (s & 0x08) != 0;
                    primaryEar = leftEar;
                    secondaryEar = rightEar;
                    break;
            }
        }

        byte? lidCounter = null;
        bool? lidClosed = null;
        if (lid is byte l && hasCaseBattery && listed)
        {
            lidCounter = (byte)(l & 0x07);
            // Upstream only trusts the lid bit when the broadcasting bud is in the case.
            if (thisInCase == true) lidClosed = ((l >> 3) & 0x01) != 0;
        }

        byte[]? encrypted = null;
        // Only the bytes after data[10] can be the encrypted block; never overlap decoded fields.
        if (tlv.Length >= 11 + EncryptedLength) encrypted = tlv[^EncryptedLength..].ToArray();

        return new ProximityMessage
        {
            Raw = tlv[2..].ToArray(),
            PairedFlag = paired,
            ModelId = model,
            Status = status,
            PrimaryIsLeft = primaryLeft,
            Layout = layout,
            PodBatteryByte = pods,
            LeftBatteryPercent = left,
            RightBatteryPercent = right,
            CaseBatteryPercent = casePercent,
            ChargingFlags = flags,
            LeftCharging = leftCharging,
            RightCharging = rightCharging,
            CaseCharging = caseCharging,
            ThisPodInCase = thisInCase,
            OnePodInCase = oneInCase,
            BothPodsInCase = bothInCase,
            LeftInEar = leftEar,
            RightInEar = rightEar,
            PrimaryInEar = primaryEar,
            SecondaryInEar = secondaryEar,
            LidCounter = lidCounter,
            LidClosed = lidClosed,
            ColorCode = At(tlv, 9),
            ConnectionStateCode = At(tlv, 10),
            EncryptedPayload = encrypted,
        };
    }

    private static byte? At(ReadOnlySpan<byte> data, int index) => index < data.Length ? data[index] : null;

    private static int? Percent(int nibble) => nibble <= MaxBatteryNibble ? nibble * 10 : null;

    /// <summary>Like <see cref="Percent"/>, but a 0 nibble also counts as absent (unlisted ids only).</summary>
    private static int? LevelOrAbsent(int nibble) => nibble == 0 ? null : Percent(nibble);
}
