using System.Text;

namespace AppleDeviceHub.Core.Storage;

/// <summary>How a bounded read of a local file ended.</summary>
public enum BoundedReadStatus
{
    /// <summary>The whole file was read and decoded.</summary>
    Ok = 0,

    /// <summary>There is no such file.</summary>
    Missing = 1,

    /// <summary>The file is bigger than the limit; none of it was decoded or parsed.</summary>
    TooLarge = 2,
}

/// <summary>
/// The one way Clasp's store layer reads its own files (settings, battery history, last-seen data).
/// These files sit in the user's profile folder, where another program running as the same user can
/// swap in a huge one, so a read never trusts the file size: it looks at the length first and also
/// stops reading at the limit, in case the file grows in between. Oversized files are renamed aside
/// (never deleted), the same way unreadable settings files already are, and the newest few are kept.
/// </summary>
public static class BoundedFile
{
    /// <summary>settings.json is a few KB; 256 KB leaves room for long per-app rule text.</summary>
    public const long SettingsMaxBytes = 256 * 1024;

    /// <summary>
    /// The fullest battery history is 8 devices of 20,000 samples at about 30 bytes each, roughly 5 MB;
    /// 6 MB is above that. Normal use is a few hundred KB.
    /// </summary>
    public const long HistoryMaxBytes = 6 * 1024 * 1024;

    /// <summary>Most renamed-aside copies of one file that are kept.</summary>
    public const int MaxKeptAside = 3;

    /// <summary>
    /// Reads <paramref name="path"/> as text if it is at most <paramref name="maxBytes"/> long.
    /// I/O errors other than "not found" are thrown for the caller to handle as it already does.
    /// </summary>
    public static BoundedReadStatus ReadText(string path, long maxBytes, out string? text)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        text = null;
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException) { return BoundedReadStatus.Missing; }
        catch (DirectoryNotFoundException) { return BoundedReadStatus.Missing; }

        using (stream)
        {
            if (stream.Length > maxBytes) return BoundedReadStatus.TooLarge;

            // Read at most one byte past the limit so a file that grew after the length check is caught.
            var buffer = new MemoryStream((int)Math.Min(stream.Length, maxBytes) + 1);
            var chunk = new byte[16 * 1024];
            long total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > maxBytes) return BoundedReadStatus.TooLarge;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            return BoundedReadStatus.Ok;
        }
    }

    /// <summary>
    /// Renames <paramref name="path"/> to <c>name.corrupt-yyyyMMdd-HHmmss.ext</c> beside it, then trims the
    /// older ones to <see cref="MaxKeptAside"/>. Returns the new file name, or null if the rename failed.
    /// </summary>
    public static string? MoveAside(string path)
    {
        try
        {
            var name = AsideName(path);
            var folder = Path.GetDirectoryName(path) ?? "";
            File.Move(path, Path.Combine(folder, name), overwrite: true);
            TrimKeptAside(folder, path, MaxKeptAside);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The timestamped name used for a kept-aside copy of <paramref name="path"/>.</summary>
    public static string AsideName(string path)
        => $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}";

    /// <summary>Deletes all but the newest <paramref name="keep"/> kept-aside copies of <paramref name="path"/>. Best effort.</summary>
    public static void TrimKeptAside(string folder, string path, int keep)
    {
        try
        {
            var pattern = $"{Path.GetFileNameWithoutExtension(path)}.corrupt-*{Path.GetExtension(path)}";
            var old = Directory.GetFiles(folder.Length == 0 ? "." : folder, pattern)
                .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal) // the name carries yyyyMMdd-HHmmss
                .Skip(keep);
            foreach (var file in old)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
}
