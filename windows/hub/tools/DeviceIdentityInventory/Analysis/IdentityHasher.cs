using System.Security.Cryptography;
using System.Text;

namespace DeviceIdentityInventory.Analysis;

/// <summary>Keyed per-run tags. The random key is kept in memory and erased on disposal.</summary>
public sealed class IdentityHasher : IDisposable
{
    private byte[]? _key;

    private IdentityHasher(byte[] key) => _key = key;

    public static IdentityHasher CreateRandom()
        => new(RandomNumberGenerator.GetBytes(32));

    public string HashName(string? value)
        => Hash("name", string.Empty, value ?? string.Empty);

    public string HashId(string? value)
        => Hash("id", string.Empty, value ?? string.Empty);

    public string HashContainerId(string? value)
        => Hash("container", string.Empty, value ?? string.Empty);

    public string HashAddress(string? value)
        => Hash("address", string.Empty, value ?? string.Empty);

    public string HashProperty(string propertyKey, string? value)
        => Hash("property", propertyKey, value ?? string.Empty);

    private string Hash(string category, string field, string value)
    {
        ObjectDisposedException.ThrowIf(_key is null, this);
        byte[] message = Encoding.UTF8.GetBytes(category + "\0" + field + "\0" + value);
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(_key!, message, digest);
        return Convert.ToHexStringLower(digest[..12]);
    }

    public void Dispose()
    {
        if (_key is null)
            return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }
}
