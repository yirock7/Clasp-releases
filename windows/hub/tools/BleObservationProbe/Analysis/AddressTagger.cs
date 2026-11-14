using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BleObservationProbe.Analysis;

/// <summary>
/// Turns a BLE address into a per-run keyed hash tag ("addr#xxxxxxxx"). The key is random per run
/// and never written anywhere, so tags are stable within one run and cannot be linked across runs
/// or reversed to the address.
/// </summary>
public sealed class AddressTagger
{
    private readonly byte[] _key;

    public AddressTagger(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 16) throw new ArgumentException("The key must be at least 16 bytes.", nameof(key));
        _key = (byte[])key.Clone();
    }

    public static AddressTagger CreateRandom() => new(RandomNumberGenerator.GetBytes(32));

    public string Tag(ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, address);
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(_key, buffer, hash);
        return "addr#" + Convert.ToHexStringLower(hash[..4]);
    }
}
