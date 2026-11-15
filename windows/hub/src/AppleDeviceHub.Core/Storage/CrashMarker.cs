using System.Globalization;
using System.Text;

namespace AppleDeviceHub.Core.Storage;

/// <summary>What a crash marker says: when Clasp died and the exception type (never its message).</summary>
public sealed record CrashMarkerInfo(DateTimeOffset When, string ExceptionType);

/// <summary>
/// A tiny file written when Clasp is about to die from an unhandled exception, so the next launch can tell
/// the person that Clasp closed unexpectedly. It holds a timestamp and the exception type name only: no
/// message, no stack, nothing about devices. Everything here is best effort and never throws, because it
/// runs while the process is already failing.
/// </summary>
public static class CrashMarker
{
    public const string FileName = "crash.marker";

    /// <summary>A marker older than this is ignored (and removed): it no longer describes "last time".</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private const int MaxTypeChars = 200;
    private const long MaxFileBytes = 1024;

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>The marker text for an exception type name.</summary>
    public static string Format(DateTimeOffset when, string? exceptionType)
        => "time=" + when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
           + "\ntype=" + CleanType(exceptionType) + "\n";

    /// <summary>Parses marker text; null when it is not a marker Clasp wrote.</summary>
    public static CrashMarkerInfo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        DateTimeOffset? when = null;
        string? type = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("time=", StringComparison.Ordinal)
                && DateTimeOffset.TryParse(line.AsSpan(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                when = parsed;
            else if (line.StartsWith("type=", StringComparison.Ordinal))
                type = CleanType(line[5..]);
        }
        return when is { } w ? new CrashMarkerInfo(w, type ?? "unknown") : null;
    }

    /// <summary>Writes the marker into <paramref name="folder"/>. Returns false (never throws) when it could not.</summary>
    public static bool TryWrite(string folder, DateTimeOffset when, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(PathIn(folder), Format(when, exception?.GetType().FullName), new UTF8Encoding(false));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The marker to tell the person about, or null. A missing marker gives null. A marker that is unreadable,
    /// oversized, not ours, dated in the future or older than <see cref="MaxAge"/> is deleted and gives null.
    /// A good marker is left in place; call <see cref="Clear"/> once the notice has actually been shown.
    /// </summary>
    public static CrashMarkerInfo? ReadPending(string folder, DateTimeOffset now)
    {
        var path = PathIn(folder);
        try
        {
            if (!File.Exists(path)) return null;
            string? text = null;
            if (new FileInfo(path).Length <= MaxFileBytes) text = File.ReadAllText(path);
            var info = Parse(text);
            if (info is not null && info.When <= now + TimeSpan.FromMinutes(5) && now - info.When <= MaxAge) return info;
            Clear(folder);
            return null;
        }
        catch (Exception)
        {
            Clear(folder);
            return null;
        }
    }

    /// <summary>Deletes the marker. Returns true when none remains.</summary>
    public static bool Clear(string folder)
    {
        try
        {
            var path = PathIn(folder);
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A type name is code, not user data, but the file is read back later, so keep it to plain identifier characters.
    private static string CleanType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "unknown";
        var builder = new StringBuilder(Math.Min(type.Length, MaxTypeChars));
        foreach (var c in type.Trim())
        {
            if (builder.Length >= MaxTypeChars) break;
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '`' or '<' or '>' or ',' or '-' ? c : '?');
        }
        return builder.ToString();
    }
}
