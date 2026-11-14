using System.Security.Cryptography;
using System.Text;

namespace AppleDeviceHub.Core.Updates;

/// <summary>
/// Release signatures. The release owner signs each installer with a private ECDSA P-256 key that
/// never leaves their PC; the app carries only the public key (<see cref="PublicKey"/>) and installs
/// nothing it cannot verify. The signed message names the version and file as well as the file's
/// SHA-256, so a genuine older installer cannot be passed off as a newer release.
/// The <c>.sig</c> file is the Base64 of the 64-byte (r || s) signature over <see cref="Message"/>.
/// </summary>
public static class UpdateSignature
{
    /// <summary>
    /// Base64 SubjectPublicKeyInfo of the Clasp release-signing key. Changing it means older builds
    /// can no longer verify new releases, so it changes only together with a manual-install release.
    /// </summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9I0IiAy9w48j6+xZHSMC7v2Lgi7LC+WfSeD4bmWdepJhBFAn/DzxL9dOqomxFzk6tRH7Ztw4SDv9nngHDTE2YQ==";

    public static byte[] Message(ClaspVersion version, string fileName, ReadOnlySpan<byte> sha256)
        => Encoding.UTF8.GetBytes(
            $"clasp-update-v1\nversion={version}\nfile={fileName}\nsha256={Convert.ToHexStringLower(sha256)}\n");

    /// <summary>Signs with the owner's private key (release tooling only).</summary>
    public static string Sign(ECDsa privateKey, ClaspVersion version, string fileName, ReadOnlySpan<byte> sha256)
        => Convert.ToBase64String(privateKey.SignData(Message(version, fileName, sha256), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>
    /// True only when <paramref name="signatureText"/> is a valid signature by
    /// <paramref name="publicKeySpki"/> (Base64) over this version, file name and hash.
    /// Malformed input of any kind is a failed verification, never an exception.
    /// </summary>
    public static bool Verify(string publicKeySpki, ClaspVersion version, string fileName, ReadOnlySpan<byte> sha256, string? signatureText)
    {
        if (sha256.Length != SHA256.HashSizeInBytes || string.IsNullOrWhiteSpace(signatureText)) return false;
        try
        {
            var signature = Convert.FromBase64String(signatureText.Trim());
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            if (key.KeySize != 256) return false;
            return key.VerifyData(Message(version, fileName, sha256), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
