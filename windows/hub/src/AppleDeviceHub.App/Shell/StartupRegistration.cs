using System.Security;
using AppleDeviceHub.Core.Startup;
using Microsoft.Win32;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Adds, removes and reads the per-user "start Clasp when I sign in" entry. Only
/// HKEY_CURRENT_USER is touched, and only the "Clasp" value under the Run key. The StartupApproved
/// key (Task Manager's switch) is read but never written, so a user's choice there is respected.
/// Decision logic lives in <see cref="StartupRegistrationPolicy"/>. Registry failures are returned
/// as results, not thrown.
/// </summary>
public static class StartupRegistration
{
    /// <summary>True only when the entry exists and Task Manager has not disabled it.</summary>
    public static bool IsEnabled() => GetStatus() == StartupStatus.On;

    /// <summary>The current status, or null when the registry could not be read.</summary>
    public static StartupStatus? GetStatus()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(StartupRegistrationPolicy.RunKeyPath, writable: false);
            var present = run?.GetValue(StartupRegistrationPolicy.ValueName) is string;
            return StartupRegistrationPolicy.Evaluate(present, ReadApproval());
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the Run value for <paramref name="exePath"/>. If Task Manager has the entry turned off,
    /// the value is still written but the result reports <see cref="StartupStatus.OffInTaskManager"/>.
    /// Clasp does not switch it back on.
    /// </summary>
    public static StartupChangeResult Enable(string exePath)
    {
        var previous = GetStatus() ?? StartupStatus.Off;
        try
        {
            var command = StartupRegistrationPolicy.BuildCommand(exePath);
            using var run = Registry.CurrentUser.CreateSubKey(StartupRegistrationPolicy.RunKeyPath, writable: true);
            run.SetValue(StartupRegistrationPolicy.ValueName, command, RegistryValueKind.String);
            var status = StartupRegistrationPolicy.Evaluate(true, ReadApproval());
            return StartupMessages.Succeeded(status);
        }
        catch (Exception ex) when (ex is ArgumentException || IsRegistryFailure(ex))
        {
            return StartupMessages.Failed(previous);
        }
    }

    /// <summary>Removes the Run value. A missing value is already the desired state.</summary>
    public static StartupChangeResult Disable()
    {
        var previous = GetStatus() ?? StartupStatus.Off;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(StartupRegistrationPolicy.RunKeyPath, writable: true);
            run?.DeleteValue(StartupRegistrationPolicy.ValueName, throwOnMissingValue: false);
            return StartupMessages.Succeeded(StartupStatus.Off);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return StartupMessages.Failed(previous);
        }
    }

    /// <summary>
    /// After the rename from AppleDeviceHub.exe to Clasp.exe: if the sign-in value still points at the old program in this
    /// folder, point it at the running one (arguments kept). Never creates the value and never touches one that names anything
    /// else, so the user's choice to have it off (or on) is kept. Returns true when it rewrote the value.
    /// </summary>
    public static bool RepairLegacyEntry(string? exePath)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(StartupRegistrationPolicy.RunKeyPath, writable: true);
            if (run?.GetValue(StartupRegistrationPolicy.ValueName) is not string current) return false;
            if (LegacyExecutable.Rewrite(current, exePath) is not { } repaired) return false;
            run.SetValue(StartupRegistrationPolicy.ValueName, repaired, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    private static byte[]? ReadApproval()
    {
        using var approval = Registry.CurrentUser.OpenSubKey(StartupRegistrationPolicy.ApprovalKeyPath, writable: false);
        return approval?.GetValue(StartupRegistrationPolicy.ValueName) as byte[];
    }

    private static bool IsRegistryFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or SecurityException;
}
