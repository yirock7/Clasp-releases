using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;

namespace AppleDeviceHub.Core.Engine;

/// <summary>Where and how to reach the engine. Every timeout is bounded.</summary>
public sealed record EngineConnectionSettings(string PipeName, string EnginePath, string? EngineSha256)
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public string ClientName { get; init; } = "Clasp";

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>The engine could not be used. <see cref="Exception.Message"/> is the user-readable reason.</summary>
public sealed class EngineUnavailableException : Exception
{
    public EngineUnavailableException(string message)
        : base(message)
    {
    }

    public EngineUnavailableException()
        : base("The Clasp engine is not available.")
    {
    }

    public EngineUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The connection closed while a request was outstanding.</summary>
public sealed class EngineConnectionClosedException : Exception
{
    public EngineConnectionClosedException(string message)
        : base(message)
    {
    }

    public EngineConnectionClosedException()
        : base("The engine connection is closed.")
    {
    }

    public EngineConnectionClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One authenticated clasp-engine v1 connection: pipe connect, server identity check, then
/// hello/hello_ack, then a read loop that drops non-increasing <c>seq</c> and matches replies to
/// requests by <c>request_id</c>. Any protocol violation closes the connection (fail closed).
/// </summary>
internal sealed class EngineConnection : IAsyncDisposable
{
    private const string PipePrefix = "Clasp.Engine.v1.";

    private readonly NamedPipeClientStream _pipe;
    private readonly EngineConnectionSettings _settings;
    private readonly Action<EngineConnection, EngineMessage> _onMessage;
    private readonly Action<EngineConnection, string> _onClosed;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<EngineMessage>> _pending = new(StringComparer.Ordinal);
    private long _lastSeq;
    private int _closed;
    private string _closeReason = "The engine connection is closed.";
    private Task _readLoop = Task.CompletedTask;

    private EngineConnection(
        NamedPipeClientStream pipe, EngineConnectionSettings settings, HelloAckMessage ack, long lastSeq,
        Action<EngineConnection, EngineMessage> onMessage, Action<EngineConnection, string> onClosed)
    {
        _pipe = pipe;
        _settings = settings;
        Ack = ack;
        _lastSeq = lastSeq;
        _onMessage = onMessage;
        _onClosed = onClosed;
    }

    public HelloAckMessage Ack { get; }

    public string Epoch => Ack.EngineEpoch;

    /// <summary>The highest <c>seq</c> accepted on this connection.</summary>
    public long LastSeq => Interlocked.Read(ref _lastSeq);

    /// <summary>Messages dropped because their <c>seq</c> was not greater than the last accepted one.</summary>
    public int DroppedStale { get; private set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>The per-user pipe name: <c>Clasp.Engine.v1.&lt;current user SID&gt;</c>.</summary>
    public static string DefaultPipeName()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Clasp engine requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
        return PipePrefix + sid;
    }

    /// <summary>
    /// Connect, verify the server, and complete the handshake. Nothing is sent before the server
    /// identity check passes. Throws <see cref="EngineUnavailableException"/> with a precise reason.
    /// </summary>
    /// <param name="settings">Pipe name, expected engine image and timeouts.</param>
    /// <param name="previousEpoch">Epoch of the last connection, if any.</param>
    /// <param name="previousSeq">Last accepted seq of that epoch; still binding if the epoch is unchanged.</param>
    /// <param name="onMessage">Called on the read loop, in seq order, for every accepted message (before its reply completes).</param>
    /// <param name="onClosed">Called once with the reason when the connection closes.</param>
    /// <param name="cancellationToken">Cancels the connect and handshake.</param>
    public static async Task<EngineConnection> OpenAsync(
        EngineConnectionSettings settings,
        string? previousEpoch,
        long previousSeq,
        Action<EngineConnection, EngineMessage> onMessage,
        Action<EngineConnection, string> onClosed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!OperatingSystem.IsWindows()) throw new EngineUnavailableException("The Clasp engine requires Windows.");

        var pipe = new NamedPipeClientStream(".", settings.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
        try
        {
            try
            {
                await pipe.ConnectAsync((int)settings.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A missing pipe and a pipe whose only instance is serving another client both
                // time out here; the pipe namespace tells them apart.
                throw new EngineUnavailableException(PipeExists(settings.PipeName)
                    ? "The Clasp engine is unavailable or busy with another Clasp window. Close the other window and try again."
                    : "The Clasp engine is not running (its pipe was not found). Start clasp-engine, then restart Clasp.");
            }
            catch (IOException ex)
            {
                throw new EngineUnavailableException($"The Clasp engine pipe could not be opened ({ex.Message.TrimEnd('.')}).", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new EngineUnavailableException("Access to the Clasp engine pipe was denied.", ex);
            }

            // Identity first: a squatter on the pipe name never sees a single byte from us.
            var refusal = EngineServerIdentity.Verify(pipe.SafePipeHandle, settings.EnginePath, settings.EngineSha256);
            if (refusal is not null) throw new EngineUnavailableException(refusal);

            var hello = new HelloMessage(EngineWire.Protocol, EngineWire.Major, EngineWire.Minor, settings.ClientName);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(settings.HandshakeTimeout);
            HelloAckMessage ack;
            try
            {
                await EngineFraming.WriteFrameAsync(pipe, hello.ToUtf8Json(), handshake.Token).ConfigureAwait(false);
                var body = await EngineFraming.ReadFrameAsync(pipe, handshake.Token).ConfigureAwait(false)
                    ?? throw new EngineUnavailableException("The engine closed the connection during the handshake.");
                ack = EngineCodec.ParseEngineMessage(body) switch
                {
                    HelloAckMessage a when a.Protocol == EngineWire.Protocol && a.Major == EngineWire.Major => a,
                    HelloAckMessage a => throw new EngineUnavailableException(
                        $"Incompatible engine protocol: the engine speaks {a.Protocol} {a.Major}.{a.Minor}; Clasp needs {EngineWire.Protocol} {EngineWire.Major}.x."),
                    ErrorMessage { Code: "incompatible_version" } e => throw new EngineUnavailableException(
                        $"Incompatible engine protocol version: {e.Detail}"),
                    ErrorMessage { Code: "busy" } => throw new EngineUnavailableException(
                        "The Clasp engine is busy with another client."),
                    ErrorMessage { Code: "not_authenticated" } => throw new EngineUnavailableException(
                        "The Clasp engine refused this app (not_authenticated). Check the engine's --allowed-client setting."),
                    ErrorMessage e => throw new EngineUnavailableException($"The engine refused the handshake ({e.Code}: {e.Detail})."),
                    var other => throw new EngineUnavailableException($"Engine handshake failed: expected hello_ack, got {other.Type}."),
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EngineUnavailableException(
                    $"The engine did not answer the handshake within {settings.HandshakeTimeout.TotalSeconds:0} seconds.");
            }
            catch (EngineProtocolException ex)
            {
                throw new EngineUnavailableException($"Engine handshake failed: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                throw new EngineUnavailableException($"Engine handshake failed: {ex.Message}", ex);
            }

            long lastSeq = ack.Seq;
            if (previousEpoch is not null && string.Equals(previousEpoch, ack.EngineEpoch, StringComparison.Ordinal))
            {
                // Same engine process: its sequence keeps counting across client reconnects.
                if (ack.Seq <= previousSeq)
                    throw new EngineUnavailableException("Engine handshake failed: the message sequence went backwards within one engine run.");
            }

            var connection = new EngineConnection(pipe, settings, ack, lastSeq, onMessage, onClosed);
            connection._readLoop = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
            return connection;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// True when a pipe with this name exists in the local pipe namespace. Listing the namespace
    /// does not connect to (or consume) any pipe instance.
    /// </summary>
    internal static bool PipeExists(string pipeName)
    {
        char sep = (char)92; // backslash, built at run time
        var root = $"{sep}{sep}.{sep}pipe{sep}";
        try
        {
            foreach (var path in Directory.EnumerateFiles(root))
            {
                // AppContainer/sandbox namespaces can include Sessions/... between the pipe
                // root and this flat engine name. This is only an availability hint: the
                // connected server must still pass CurrentUserOnly and image/hash checks.
                var name = Path.GetFileName(path);
                if (string.Equals(name, pipeName, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return false;
    }

    /// <summary>Send one request and wait for its reply (matched by request_id).</summary>
    /// <exception cref="TimeoutException">No reply within <paramref name="timeout"/>.</exception>
    /// <exception cref="EngineConnectionClosedException">The connection closed first.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<EngineMessage> RequestAsync(ClientRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tcs = new TaskCompletionSource<EngineMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.RequestId, tcs)) throw new InvalidOperationException("Duplicate request id.");
        try
        {
            if (IsClosed) throw new EngineConnectionClosedException(_closeReason);
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout, _settings.Time, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    /// <summary>Best-effort send with no reply expected (for example <c>cancel</c>). Never throws.</summary>
    public async Task TrySendAsync(ClientMessage message)
    {
        try
        {
            await WriteAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EngineConnectionClosedException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task WriteAsync(ClientMessage message, CancellationToken cancellationToken)
    {
        var body = message.ToUtf8Json();
        if (IsClosed) throw new EngineConnectionClosedException(_closeReason);
        CancellationTokenSource linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        }
        catch (ObjectDisposedException)
        {
            throw new EngineConnectionClosedException(_closeReason);
        }
        using var linkedScope = linked;
        try
        {
            await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EngineConnectionClosedException(_closeReason);
        }
        catch (ObjectDisposedException)
        {
            throw new EngineConnectionClosedException(_closeReason);
        }
        try
        {
            if (IsClosed) throw new EngineConnectionClosedException(_closeReason);
            await EngineFraming.WriteFrameAsync(_pipe, body, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EngineConnectionClosedException(_closeReason);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close($"writing to the engine failed: {ex.Message.TrimEnd('.')}");
            throw new EngineConnectionClosedException(_closeReason, ex);
        }
        finally
        {
            try { _writeLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var body = await EngineFraming.ReadFrameAsync(_pipe, _stop.Token).ConfigureAwait(false);
                if (body is null)
                {
                    Close("the engine closed the connection");
                    return;
                }
                var message = EngineCodec.ParseEngineMessage(body);
                if (message is HelloAckMessage)
                {
                    Close("protocol error: a second hello_ack");
                    return;
                }
                if (message.Seq <= _lastSeq)
                {
                    DroppedStale++;
                    continue; // stale or duplicate: never processed
                }
                Interlocked.Exchange(ref _lastSeq, message.Seq);
                _onMessage(this, message);
                if (message.ReplyTo is { } id && _pending.TryRemove(id, out var waiter)) waiter.TrySetResult(message);
                if (message is ErrorMessage { RequestId: null } fatal)
                {
                    // A connection-level error is the engine's last word: close our end at once
                    // instead of waiting for the engine to drop the pipe.
                    Close($"engine error {fatal.Code}: {fatal.Detail.TrimEnd('.')}");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            Close("disconnected by Clasp");
        }
        catch (EngineProtocolException ex)
        {
            Close($"protocol error ({ex.Error}): {ex.Message.TrimEnd('.')}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close(IsClosed ? _closeReason : $"the connection failed: {ex.Message.TrimEnd('.')}");
        }
        catch (Exception ex)
        {
            Close($"internal error: {ex.GetType().Name}");
        }
    }

    /// <summary>Close once: stop the loop, drop the pipe, fail every outstanding request, report why.</summary>
    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _closeReason = reason;
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        try { _pipe.Dispose(); } catch (IOException) { }
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var waiter)) waiter.TrySetException(new EngineConnectionClosedException(reason));
        }
        _onClosed(this, reason);
    }

    public async ValueTask DisposeAsync()
    {
        Close("disconnected by Clasp");
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        _stop.Dispose();
        _writeLock.Dispose();
    }
}
