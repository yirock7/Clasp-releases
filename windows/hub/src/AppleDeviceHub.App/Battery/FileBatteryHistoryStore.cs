using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Storage;

namespace AppleDeviceHub.App.Battery;

/// <summary>
/// Stores battery history and last-seen data as small text files next to settings.json in
/// %LOCALAPPDATA%\AppleDeviceHub (the folder the app already uses). Writes are atomic: the text
/// goes to a temporary file that then replaces the real one, so a crash cannot leave half a file.
/// Local only, never uploaded.
/// </summary>
internal sealed class FileBatteryHistoryStore : IBatteryHistoryStore
{
    private readonly string _folder;

    public FileBatteryHistoryStore(string? folder = null) =>
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleDeviceHub");

    private string PathFor(string name)
    {
        // Names are fixed constants from Core; refuse anything that could leave the folder.
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Invalid store name.", nameof(name));
        return Path.Combine(_folder, name + ".txt");
    }

    public string? Read(string name)
    {
        try
        {
            var path = PathFor(name);
            var status = BoundedFile.ReadText(path, BoundedFile.HistoryMaxBytes, out var text);
            if (status == BoundedReadStatus.Ok) return text;
            if (status == BoundedReadStatus.TooLarge)
            {
                // Far bigger than the history can ever be. Keep it aside (never read it) and start empty.
                var kept = BoundedFile.MoveAside(path);
                LocalLog.Write(kept is null
                    ? "Battery store file is over the size limit and was ignored."
                    : "Battery store file is over the size limit and was moved aside.");
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalLog.Write($"Battery store read failed: {ex.GetType().Name}");
            return null;
        }
    }

    public bool Write(string name, string content)
    {
        string? temp = null;
        try
        {
            var path = PathFor(name);
            Directory.CreateDirectory(_folder);
            temp = path + ".tmp";
            File.WriteAllText(temp, content);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalLog.Write($"Battery store write failed: {ex.GetType().Name}");
            try { if (temp is not null) File.Delete(temp); } catch (Exception) { /* best effort */ }
            return false;
        }
    }
}
