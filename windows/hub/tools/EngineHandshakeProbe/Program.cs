// Manual, hello-only interoperability check. No list_devices/open_session/setting request.
// Reflection reaches the real internal client without expanding the product's public API.
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.IO.Pipes;
using AppleDeviceHub.Core.Engine;
using AppleDeviceHub.Core.Interop;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
if (args.Length != 3) throw new ArgumentException("Expected: <reviewed engine.exe> <expected SHA256> <fresh evidence directory>");
var enginePath = Path.GetFullPath(args[0]);
var expectedHash = args[1];
if (!ClaspEngineOptions.IsValidSha256(expectedHash)) throw new ArgumentException("Expected a recorded SHA256.");
using (var image = File.OpenRead(enginePath))
{
    if (!string.Equals(Convert.ToHexString(SHA256.HashData(image)), expectedHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Engine bytes differ from the reviewed candidate.");
}
var evidence = Path.GetFullPath(args[2]);
if (Directory.Exists(evidence)) throw new ArgumentException("Use a fresh evidence directory.");
Directory.CreateDirectory(evidence);
var existing = Process.GetProcessesByName("clasp-engine");
if (existing.Length > 0)
{
    foreach (var running in existing) running.Dispose();
    throw new InvalidOperationException("Close an existing engine yourself before running this isolated probe.");
}
var connectionType = typeof(NtpodsDaemonTransport).Assembly.GetType("AppleDeviceHub.Core.Engine.EngineConnection", throwOnError: true)!;
var pipeName = (string)(connectionType.GetMethod("DefaultPipeName")!.Invoke(null, null) ?? throw new InvalidOperationException("No pipe name."));
if ((bool)connectionType.GetMethod("PipeExists", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [pipeName])!)
    throw new InvalidOperationException("The production pipe name is already occupied; this probe will not attach to an existing server.");
var start = new ProcessStartInfo(enginePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add("--serve");
start.ArgumentList.Add("--allowed-client");
start.ArgumentList.Add(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify probe executable."));
start.ArgumentList.Add("--log-dir");
start.ArgumentList.Add(Path.Combine(evidence, "logs"));
// Isolate native salt/log state; leave the owner's normal Clasp directories untouched.
start.Environment["LOCALAPPDATA"] = Path.Combine(evidence, "local");
using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start reviewed engine.");
var stdout = process.StandardOutput.ReadToEndAsync();
var stderr = process.StandardError.ReadToEndAsync();
try
{
    var type = connectionType;
    var open = type.GetMethod("OpenAsync", BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException("EngineConnection.OpenAsync");
    var parameters = open.GetParameters();
    if (process.HasExited) throw new InvalidOperationException("The isolated engine exited before the probe could connect.");
    static Delegate NoOp(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var args = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        return Expression.Lambda(delegateType, Expression.Empty(), args).Compile();
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var settings = new EngineConnectionSettings(pipeName, enginePath, expectedHash) { ClientName = "Clasp hello-only probe", ConnectTimeout = TimeSpan.FromSeconds(3) };
    var task = (Task)(open.Invoke(null, [settings, null, 0L, NoOp(parameters[3].ParameterType), NoOp(parameters[4].ParameterType), timeout.Token])
        ?? throw new InvalidOperationException("No handshake task."));
    await task.ConfigureAwait(false);
    var connection = task.GetType().GetProperty("Result")!.GetValue(task) ?? throw new InvalidOperationException("No connection.");
    await using var connectionLifetime = (IAsyncDisposable)connection;
    var pipe = (NamedPipeClientStream)type.GetField("_pipe", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
    var identityType = type.Assembly.GetType("AppleDeviceHub.Core.Engine.EngineServerIdentity", throwOnError: true)!;
    object[] pidArguments = [pipe.SafePipeHandle, 0u];
    var identified = (bool)identityType.GetMethod("GetNamedPipeServerProcessId", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, pidArguments)!;
    if (!identified || (uint)pidArguments[1] != (uint)process.Id)
        throw new InvalidOperationException("The authenticated server is not this probe's isolated child.");
    Console.WriteLine("PASS: actual Rust engine and actual C# client completed identity verification and protocol hello with CurrentUserOnly. No device list, driver open, Bluetooth scan or session request was sent.");
}
finally
{
    // This exact child was started by this probe and has never opened a session.
    if (!process.HasExited) process.Kill(entireProcessTree: false);
    await process.WaitForExitAsync().ConfigureAwait(false);
    await File.WriteAllTextAsync(Path.Combine(evidence, "stdout.txt"), await stdout.ConfigureAwait(false)).ConfigureAwait(false);
    await File.WriteAllTextAsync(Path.Combine(evidence, "stderr.txt"), await stderr.ConfigureAwait(false)).ConfigureAwait(false);
}
