using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AppleDeviceHub.Core.Engine;

/// <summary>
/// Checks who serves the engine pipe before anything is sent on it (protocol section 1):
/// the server process's full image path must equal the configured engine path
/// (case-insensitive, full path), and, when an expected SHA-256 is configured, the image
/// file's hash must match. Any doubt is a refusal.
/// </summary>
internal static class EngineServerIdentity
{
    /// <summary>Returns null when the server is the configured engine, otherwise the reason it is not.</summary>
    [SupportedOSPlatform("windows")]
    public static string? Verify(SafePipeHandle pipe, string expectedPath, string? expectedSha256)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint pid))
            return $"Engine identity check failed: the pipe server's process could not be identified (error {Marshal.GetLastPInvokeError()}).";

        string? imagePath = QueryImagePath(pid);
        if (imagePath is null)
            return "Engine identity check failed: the pipe server's executable path could not be read.";

        if (!PathsEqual(imagePath, expectedPath))
            return $"Engine identity check failed: the pipe is served by '{imagePath}', not the configured engine '{expectedPath}'.";

        if (expectedSha256 is not null)
        {
            string actual;
            try
            {
                using var file = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                actual = Convert.ToHexString(SHA256.HashData(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Engine identity check failed: the engine file could not be read for hashing ({ex.GetType().Name}).";
            }
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return "Engine identity check failed: the engine file's SHA-256 does not match the expected hash.";
        }
        return null;
    }

    internal static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? QueryImagePath(uint pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid) return null;
        var buffer = new char[32768];
        uint size = (uint)buffer.Length;
        if (!QueryFullProcessImageNameW(process, 0, buffer, ref size)) return null;
        return new string(buffer, 0, (int)size);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] exeName, ref uint size);
}
