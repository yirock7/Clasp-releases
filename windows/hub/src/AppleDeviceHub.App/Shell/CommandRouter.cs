using System.IO.Pipes;
using System.Text;
using AppleDeviceHub.Core.Commands;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Hands a command from a second copy of the app to the copy that is already running.
/// The running app calls <see cref="StartServer"/>; a second launch that finds the single-instance
/// mutex taken calls <see cref="TryForward"/> and then exits. The channel is a named pipe created
/// with <see cref="PipeOptions.CurrentUserOnly"/> (only the same Windows user can connect), and only
/// a short canonical command name travels over it, never the original link or arguments, so the
/// receiver re-validates with the same allow-list parser.
/// (Windows App SDK AppInstance redirection is not used: this app is unpackaged and already uses a
/// named mutex, and the pipe needs no change to the generated entry point.)
/// </summary>
internal sealed class CommandRouter : IDisposable
{
    internal static readonly TimeSpan ForwardDeadline = TimeSpan.FromSeconds(2);
    private const int MaxMessageBytes = 64;

    private readonly Func<ClaspCommand, Task> _onCommand;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private CommandRouter(Func<ClaspCommand, Task> onCommand) => _onCommand = onCommand;

    /// <summary>
    /// Starts listening. <paramref name="onCommand"/> runs on a thread-pool thread for each valid
    /// command; the caller must marshal to the UI thread. Exceptions from it are logged, not rethrown.
    /// </summary>
    public static CommandRouter StartServer(Func<ClaspCommand, Task> onCommand)
    {
        ArgumentNullException.ThrowIfNull(onCommand);
        var router = new CommandRouter(onCommand);
        router._loop = Task.Run(() => router.ListenAsync(router._cts.Token));
        return router;
    }

    /// <summary>
    /// For the second instance: sends the command found in <paramref name="args"/> (--uri= or
    /// --command=) to the running app. Returns (true, null) only when the running app acknowledged it.
    /// Retries briefly because the first instance may still be starting its listener, but the whole
    /// attempt (connect, write, read) shares one <see cref="ForwardDeadline"/>.
    /// </summary>
    public static async Task<(bool Forwarded, string? FailureReason)> TryForwardAsync(IEnumerable<string> args)
    {
        var parsed = ClaspCommandParser.ParseArgs(args);
        if (parsed is null) return (false, "No command was given.");
        if (parsed.Command is not { } command) return (false, parsed.Reason);
        return await TryForwardAsync(command).ConfigureAwait(false);
    }

    /// <summary>Sends one already-validated command to the running app.</summary>
    public static async Task<(bool Forwarded, string? FailureReason)> TryForwardAsync(ClaspCommand command)
    {
        var payload = Encoding.UTF8.GetBytes(ClaspCommandText.CanonicalName(command) + "\n");
        string? failureReason = null;
        using var deadline = new CancellationTokenSource(ForwardDeadline);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", SingleInstanceNames.PipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    await client.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                    await client.FlushAsync(deadline.Token).ConfigureAwait(false);
                    // The only valid answer is exactly "ok" followed by the pipe closing.
                    var buffer = new byte[8];
                    var total = 0;
                    while (total < buffer.Length)
                    {
                        var read = await client.ReadAsync(buffer.AsMemory(total), deadline.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        total += read;
                    }
                    if (IsAcknowledgement(buffer.AsSpan(0, total))) return (true, null);
                    return (false, "Clasp didn't accept the command.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failureReason = $"Couldn't reach Clasp ({ex.GetType().Name}).";
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            failureReason = "Clasp didn't answer in time.";
        }
        return (false, failureReason);
    }

    /// <summary>True only for exactly the two bytes "ok".</summary>
    internal static bool IsAcknowledgement(ReadOnlySpan<byte> reply)
        => reply.Length == 2 && reply[0] == (byte)'o' && reply[1] == (byte)'k';

    private async Task ListenAsync(CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(SingleInstanceNames.PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                failures = 0;
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                await HandleClientAsync(server, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A name held by something we cannot share (another process, other permissions) will not free up by
                // retrying forever: give up and carry on without forwarding.
                if (++failures >= 5)
                {
                    LocalLog.Write($"Command pipe name is unavailable ({ex.GetType().Name}); links from a second launch will not be forwarded.");
                    break;
                }
                LocalLog.Write($"Command pipe error ({ex.GetType().Name}); retrying.");
                try { await Task.Delay(500, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2)); // a stalled client must not block later ones
        var buffer = new byte[MaxMessageBytes];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = await server.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0) break;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }

        var text = Encoding.UTF8.GetString(buffer, 0, total).Trim();
        var parsed = ClaspCommandParser.ParseName(text);
        if (parsed.Command is not { } command)
        {
            LocalLog.Write($"Command pipe: rejected a message ({parsed.Reason}).");
            await TryReplyAsync(server, "no", token).ConfigureAwait(false);
            return;
        }
        await TryReplyAsync(server, "ok", token).ConfigureAwait(false);
        // Not awaited: a slow command (connecting) must not keep the pipe closed to the next client.
        _ = RunAsync(command);
    }

    private async Task RunAsync(ClaspCommand command)
    {
        try { await _onCommand(command).ConfigureAwait(false); }
        catch (Exception ex) { LocalLog.Write($"Command '{ClaspCommandText.CanonicalName(command)}' failed ({ex.GetType().Name})."); }
    }

    private static async Task TryReplyAsync(NamedPipeServerStream server, string reply, CancellationToken token)
    {
        try
        {
            var bytes = Encoding.ASCII.GetBytes(reply);
            await server.WriteAsync(bytes, token).ConfigureAwait(false);
            await server.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
