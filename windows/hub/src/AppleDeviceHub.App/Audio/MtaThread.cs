using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace AppleDeviceHub.App.Audio;

/// <summary>Runs synchronous COM work on a dedicated, initialized multithreaded apartment.</summary>
internal static class MtaThread
{
    private const uint CoInitMultithreaded = 0;

    internal static T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("An MTA COM thread is available only on Windows.");

        T? result = default;
        ExceptionDispatchInfo? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var initialized = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            if (initialized < 0)
            {
                failure = ExceptionDispatchInfo.Capture(Marshal.GetExceptionForHR(initialized)
                    ?? new InvalidOperationException($"COM could not initialize the MTA thread (0x{initialized:X8})."));
                completed.Set();
                return;
            }

            try { result = action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally
            {
                CoUninitialize();
                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Clasp audio-session MTA",
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        completed.Wait();
        failure?.Throw();
        return result!;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
