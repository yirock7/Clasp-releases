using System.Diagnostics;
using AppleDeviceHub.Core.Audio;

namespace AppleDeviceHub.App.Audio;

/// <summary>Lists running processes by executable name for per-app output rules. Read-only.</summary>
public sealed class RunningProcessSource : IRunningProcessSource
{
    public IReadOnlyList<PerAppProcess> GetRunning()
    {
        var result = new List<PerAppProcess>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return result;
        }
        foreach (var process in all)
        {
            try { result.Add(new(process.Id, process.ProcessName + ".exe")); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited */ }
            finally { process.Dispose(); }
        }
        return result;
    }

    /// <summary>Looks up one process name by id, or null when it is gone.</summary>
    public static PerAppProcess? Find(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new(process.Id, process.ProcessName + ".exe");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
