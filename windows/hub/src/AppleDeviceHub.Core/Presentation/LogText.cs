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

    /// <summary>
    /// Describes an exception for the log without its message: exception messages can carry device names,
    /// Bluetooth addresses or file paths. Gives "Type (0xHRESULT)" for the exception and, for each inner
    /// exception, the same again; the outer exception also gets its stack trace (method names only).
    /// </summary>
    public static string Exception(Exception? exception)
    {
        if (exception is null) return "no exception";
        var builder = new StringBuilder();
        AppendKind(builder, exception);
        if (exception.StackTrace is { Length: > 0 } trace) builder.Append(" at ").Append(trace);
        int depth = 0;
        for (var inner = exception.InnerException; inner is not null && depth < 8; inner = inner.InnerException, depth++)
        {
            builder.Append(" <- ");
            AppendKind(builder, inner);
        }
        if (exception is AggregateException aggregate)
        {
            foreach (var child in aggregate.InnerExceptions.Take(8))
            {
                builder.Append(" | ");
                AppendKind(builder, child);
            }
        }
        return builder.ToString();
    }

    /// <summary>Just "Type (0xHRESULT)": a short, message-free description for one-line outcomes.</summary>
    public static string ExceptionKind(Exception? exception)
    {
        if (exception is null) return "no exception";
        var builder = new StringBuilder();
        AppendKind(builder, exception);
        return builder.ToString();
    }

    private static void AppendKind(StringBuilder builder, Exception exception) =>
        builder.Append(exception.GetType().FullName ?? exception.GetType().Name)
            .Append(" (0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture)).Append(')');
}
