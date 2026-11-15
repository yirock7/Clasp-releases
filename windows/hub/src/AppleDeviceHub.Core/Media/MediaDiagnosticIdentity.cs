using System.Security.Cryptography;
using System.Text;

namespace AppleDeviceHub.Core.Media;

/// <summary>Creates a short opaque reference for a media session without logging its app identity.</summary>
internal static class MediaDiagnosticIdentity
{
    public static string SessionReference(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return "none";

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        return Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
    }
}
