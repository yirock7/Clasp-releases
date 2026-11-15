using AppleDeviceHub.Core.Audio;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace AppleDeviceHub.App.Audio;

/// <summary>
/// Lets a phone stream its audio through this PC, which then acts like a Bluetooth speaker. It wraps
/// <c>AudioPlaybackConnection</c>: list candidates, start, open, and stop. Callers never receive an
/// exception; every outcome is a <see cref="PhoneStreamingState"/> with a plain-language message.
/// </summary>
/// <remarks>
/// One start or stop runs at a time. The strong reference to the open connection is kept here, and
/// released on stop or dispose. <see cref="StateChanged"/> can be raised on any thread, so subscribers
/// must dispatch to the UI thread before reading controls. Whether an unpackaged desktop build can
/// use this API has not been verified on hardware. See docs/planning/integration/P4-E.md.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
public sealed class PhoneStreaming : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _sync = new();
    private AudioPlaybackConnection? _connection;
    private PhoneStreamingState _state = PhoneStreamingStates.Idle();
    private bool _disposed;

    /// <summary>The latest state. Safe to read from any thread.</summary>
    public PhoneStreamingState State
    {
        get { lock (_sync) return _state; }
    }

    /// <summary>Raised after every state change. Dispatch to the UI thread before touching controls.</summary>
    public event EventHandler<PhoneStreamingState>? StateChanged;

    /// <summary>Lists devices Windows says can stream audio to this PC. Failures are reported in the result.</summary>
    public static async Task<PhoneDeviceSearch> FindCandidatesAsync()
    {
        try
        {
            var selector = AudioPlaybackConnection.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            var candidates = PhoneDeviceList.Normalize(devices.Select(device => new PhoneStreamingCandidate(device.Id, device.Name)));
            return new PhoneDeviceSearch(candidates, Succeeded: true);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new PhoneDeviceSearch([], Succeeded: false);
        }
    }

    /// <summary>
    /// Connects the phone with <paramref name="deviceId"/>: create the connection, start it, then open it.
    /// Any previous connection is closed first. On failure the connection is closed, and the result says why.
    /// </summary>
    public async Task<PhoneStreamingState> StartAsync(string deviceId, string? deviceName)
    {
        if (IsDisposed()) return Publish(PhoneStreamingStates.Unavailable());
        if (string.IsNullOrWhiteSpace(deviceId)) return Publish(PhoneStreamingStates.NoPhonesFound());

        await _operationGate.WaitAsync();
        try
        {
            if (IsDisposed()) return Publish(PhoneStreamingStates.Unavailable());
            CloseConnection();

            var connection = AudioPlaybackConnection.TryCreateFromId(deviceId.Trim());
            if (connection is null) return Publish(PhoneStreamingStates.NotSupportedByDevice(deviceName));

            lock (_sync)
            {
                // Dispose can run on another thread after the check above. Never keep a connection created after dispose.
                if (_disposed)
                {
                    ReleaseNative(connection);
                    return _state;
                }
                _connection = connection;
            }
            connection.StateChanged += OnConnectionStateChanged;
            Publish(PhoneStreamingStates.Connecting(deviceName));

            await connection.StartAsync();
            var result = await connection.OpenAsync();
            var outcome = MapOutcome(result.Status);
            if (outcome == PhoneOpenOutcome.Accepted)
            {
                return Publish(PhoneStreamingStates.Streaming(deviceName));
            }

            CloseConnection();
            return Publish(PhoneStreamingStates.Rejected(deviceName, outcome));
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            CloseConnection();
            return Publish(PhoneStreamingStates.Rejected(deviceName, PhoneOpenOutcome.Failed));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Closes the stream and releases the connection. Stopping when nothing is open is not an error.</summary>
    public async Task<PhoneStreamingState> StopAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            CloseConnection();
            return Publish(PhoneStreamingStates.Idle());
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Closes any open connection. Safe to call more than once.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CloseConnection();
    }

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        // Ignore events from a connection this object no longer owns (our own Close, or a replaced one).
        string? endedDeviceName;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _connection)) return;
            if (sender.State != AudioPlaybackConnectionState.Closed) return;
            if (_state.Phase != PhoneStreamingPhase.Streaming) return;
            _connection = null;
            endedDeviceName = _state.DeviceName;
        }

        // The phone or Windows ended the stream while we were streaming.
        sender.StateChanged -= OnConnectionStateChanged;
        ReleaseNative(sender);
        Publish(PhoneStreamingStates.Ended(endedDeviceName));
    }

    private void CloseConnection()
    {
        AudioPlaybackConnection? connection;
        lock (_sync)
        {
            connection = _connection;
            _connection = null;
        }
        if (connection is null) return;

        connection.StateChanged -= OnConnectionStateChanged;
        ReleaseNative(connection);
    }

    private static void ReleaseNative(AudioPlaybackConnection connection)
    {
        // The WinRT IClosable.Close is projected to IDisposable.Dispose in C#, which closes the connection.
        try
        {
            if (connection is IDisposable disposable) disposable.Dispose();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // The connection was already closed by the phone or Windows. Nothing else to undo.
        }
    }

    private PhoneStreamingState Publish(PhoneStreamingState state)
    {
        lock (_sync) _state = state;
        StateChanged?.Invoke(this, state);
        return state;
    }

    private bool IsDisposed()
    {
        lock (_sync) return _disposed;
    }

    private static PhoneOpenOutcome MapOutcome(AudioPlaybackConnectionOpenResultStatus status) => status switch
    {
        AudioPlaybackConnectionOpenResultStatus.Success => PhoneOpenOutcome.Accepted,
        AudioPlaybackConnectionOpenResultStatus.DeniedBySystem => PhoneOpenOutcome.DeniedBySystem,
        AudioPlaybackConnectionOpenResultStatus.RequestTimedOut => PhoneOpenOutcome.TimedOut,
        _ => PhoneOpenOutcome.Failed,
    };

    private static bool IsRecoverable(Exception ex) => ex is not OutOfMemoryException;
}
