using System.Security.Cryptography;
using System.Text;

namespace GattDiscoveryProbe.Analysis;

/// <summary>Per-run HMAC tags for device names and Windows ids. The random key is never persisted.</summary>
public sealed class DeviceIdentityHasher : IDisposable
{
    private byte[]? _key;

    public DeviceIdentityHasher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 16)
            throw new ArgumentException("The per-run key must be at least 16 bytes.", nameof(key));
        _key = (byte[])key.Clone();
    }

    public static DeviceIdentityHasher CreateRandom()
        => new(RandomNumberGenerator.GetBytes(32));

    public string HashName(string? value) => Hash("name", value ?? string.Empty);

    public string HashDeviceId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Hash("id", value);
    }

    private string Hash(string category, string value)
    {
        ObjectDisposedException.ThrowIf(_key is null, this);
        byte[] message = Encoding.UTF8.GetBytes(category + "\0" + value);
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(_key!, message, digest);
        return Convert.ToHexStringLower(digest[..12]);
    }

    public void Dispose()
    {
        if (_key is null) return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }
}
