namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Tiny local diagnostics log (%LOCALAPPDATA%\Clasp\hub.log). Local only, never
/// uploaded. It records UI events such as window placement and, in hardware mode, engine and
/// session events that name a device only by its opaque local device_id (never its pairing name
/// or Bluetooth address).
/// </summary>
internal static class LocalLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static string? _lastFailure;

    public static string PathOnDisk { get; } = System.IO.Path.Combine(AppleDeviceHub.Core.Storage.AppDataFolder.DefaultRoot, "hub.log");

    /// <summary>Used only when hub.log cannot be written, so such a run still leaves a trace and its reason.</summary>
    public static string FallbackPathOnDisk { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Clasp-hub-fallback.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate) Append(PathOnDisk, message);
        }
        catch (Exception ex)
        {
            WriteFallback(message, ex);
        }
    }

    private static void WriteFallback(string message, Exception failure)
    {
        try
        {
            lock (Gate)
            {
                // Name the reason once per kind of failure rather than on every line.
                var reason = $"{failure.GetType().Name} 0x{failure.HResult:X8}";
                if (!string.Equals(reason, _lastFailure, StringComparison.Ordinal))
                {
                    _lastFailure = reason;
                    Append(FallbackPathOnDisk, $"hub.log could not be written ({PathOnDisk}): {reason}");
                }
                Append(FallbackPathOnDisk, message);
            }
        }
        catch
        {
            // Diagnostics must never break the app.
        }
    }

    private static void Append(string path, string message)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxBytes) info.Delete(); // keep it small
        File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {AppleDeviceHub.Core.Presentation.LogText.Sanitize(message)}{Environment.NewLine}");
    }
}
