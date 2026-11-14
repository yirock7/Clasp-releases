using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// Makes text safe for the local diagnostics log: one record stays one line (control characters are
/// escaped, so a device name or exception message cannot forge extra log lines) and a record is capped
/// at about 2 KB. Also gives short opaque references for things the log must not name.
/// </summary>
public static class LogText
{
    public const int MaxRecordChars = 2048;
    private const string TruncatedMark = "...[truncated]";

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var builder = new StringBuilder(Math.Min(message.Length, MaxRecordChars) + 16);
        bool truncated = false;
        foreach (char c in message)
        {
            if (builder.Length >= MaxRecordChars) { truncated = true; break; }
            switch (c)
            {
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || c is '\u2028' or '\u2029')
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        if (truncated) builder.Append(TruncatedMark);
        return builder.ToString();
    }

    /// <summary>A short stable reference ("ref:1a2b3c4d") for an id or name the log must not contain, or "ref:none".</summary>
    public static string OpaqueRef(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "ref:none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "ref:" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
