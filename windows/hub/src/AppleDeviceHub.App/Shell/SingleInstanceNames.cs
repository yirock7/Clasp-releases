using System.Diagnostics;
using System.Security.Principal;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Names of the single-instance mutex and the command pipe. Both include the current user's SID and
/// the Windows session id, so two users (or two sessions) on one PC never share or squat on each
/// other's names.
/// </summary>
internal static class SingleInstanceNames
{
    private static readonly string Suffix = BuildSuffix();

    public static string MutexName => @"Local\Clasp." + Suffix;

    public static string PipeName => "Clasp.Commands.v2." + Suffix;

    private static string BuildSuffix()
    {
        string sid;
        try { sid = WindowsIdentity.GetCurrent().User?.Value ?? "nosid"; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { sid = "nosid"; }
        int session;
        try { session = Process.GetCurrentProcess().SessionId; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { session = -1; }
        return $"{sid}.{session}";
    }
}
