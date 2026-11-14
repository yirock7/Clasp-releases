using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.App.Automation;

/// <summary>
/// Performs the actions chosen by <see cref="ConnectionActionsPolicy"/> through the existing
/// audio and playback interfaces. Plans run one at a time (a second plan waits for the first),
/// each action is attempted at most once, and every failure is logged instead of thrown.
/// </summary>
/// <remarks>
/// <see cref="IAudioOutputController"/> must be used on its owning UI thread, so the audio calls
/// go through <c>runOnAudioThread</c> (the app passes a DispatcherQueue-backed marshaller). The
/// playback controller is thread-safe. A failed SwitchOutput skips the Play that follows it,
/// because playing would then come out of the PC speakers; the volume step still runs since it
/// only touches the AirPods endpoint.
/// </remarks>
public sealed class ConnectionActionsRunner : IDisposable
{
    private readonly IAudioOutputController? _audio;
    private readonly IPlaybackController _playback;
    private readonly Func<Func<AudioOutputWriteResult>, Task<AudioOutputWriteResult>> _runOnAudioThread;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public ConnectionActionsRunner(
        IAudioOutputController? audio,
        IPlaybackController playback,
        Func<Func<AudioOutputWriteResult>, Task<AudioOutputWriteResult>> runOnAudioThread,
        Action<string>? log = null,
        TimeProvider? timeProvider = null)
    {
        _audio = audio;
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _runOnAudioThread = runOnAudioThread ?? throw new ArgumentNullException(nameof(runOnAudioThread));
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs the plan's actions in order. <paramref name="isStillApplicable"/> is checked before
    /// every action (after its delay) so a plan made stale by a disconnect stops quietly.
    /// Never throws; cancellation simply ends the run.
    /// </summary>
    public async Task RunAsync(
        ConnectionActionPlan plan,
        Func<bool>? isStillApplicable = null,
        IPlaybackController? playbackController = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Actions.Count == 0 || Volatile.Read(ref _disposed) != 0) return;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var token = linked.Token;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bool switchFailed = false;
                foreach (var action in plan.Actions)
                {
                    if (action.Delay > TimeSpan.Zero)
                        await Task.Delay(action.Delay, _time, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (isStillApplicable is not null && !SafeApplicable(isStillApplicable))
                    {
                        Log($"event=skipped reason=no longer applicable action={action.Kind}");
                        return;
                    }

                    if (action.Kind == ConnectionActionKind.Play && switchFailed)
                    {
                        Log("event=skipped action=Play reason=output switch failed");
                        continue;
                    }

                    bool ok = await RunOneAsync(action, isStillApplicable, playbackController ?? _playback, token).ConfigureAwait(false);
                    if (!ok && action.Kind == ConnectionActionKind.SwitchOutput) switchFailed = true;
                }
            }
            finally
            {
                try { _gate.Release(); }
                catch (ObjectDisposedException) { /* Disposed while a plan was finishing. */ }
            }
        }
        catch (OperationCanceledException) { /* Shutdown or a newer decision ended this run. */ }
        catch (ObjectDisposedException) { /* Shutdown. */ }
        catch (Exception exception)
        {
            Log($"event=failed reason=unexpected error={exception.GetType().Name}");
        }
    }

    private async Task<bool> RunOneAsync(
        ConnectionAction action,
        Func<bool>? isStillApplicable,
        IPlaybackController playback,
        CancellationToken token)
    {
        try
        {
            switch (action.Kind)
            {
                case ConnectionActionKind.SwitchOutput when !string.IsNullOrWhiteSpace(action.EndpointId):
                {
                    if (_audio is null) return Report(action, false, "Windows audio output control is unavailable.");
                    var result = await _runOnAudioThread(() => SafeApplicable(isStillApplicable)
                        ? _audio.TrySetDefaultOutput(action.EndpointId!)
                        : new AudioOutputWriteResult(false, "The connection changed before the audio change could run.")).ConfigureAwait(false);
                    return Report(action, result.Applied, result.Detail);
                }
                case ConnectionActionKind.SetVolume when !string.IsNullOrWhiteSpace(action.EndpointId) && action.VolumePercent is >= 0 and <= 100:
                {
                    if (_audio is null) return Report(action, false, "Windows audio output control is unavailable.");
                    float scalar = action.VolumePercent.Value / 100f;
                    var result = await _runOnAudioThread(() => SafeApplicable(isStillApplicable)
                        ? _audio.TrySetVolume(action.EndpointId!, scalar)
                        : new AudioOutputWriteResult(false, "The connection changed before the volume change could run.")).ConfigureAwait(false);
                    return Report(action, result.Applied, result.Detail);
                }
                case ConnectionActionKind.Play:
                {
                    if (!SafeApplicable(isStillApplicable)) return Report(action, false, "The connection changed before playback could be requested.");
                    // Play was only planned because the AirPods were the default output. The delay before it
                    // is long enough for the user (or Windows) to move the sound elsewhere, so look again.
                    if (!string.IsNullOrWhiteSpace(action.EndpointId) && !await StillDefaultOutputAsync(action.EndpointId!).ConfigureAwait(false))
                    {
                        Log("event=skipped action=Play reason=default output changed");
                        return false;
                    }
                    var result = await playback.PlayAsync(token).ConfigureAwait(false);
                    return Report(action, result.WasSent, result.Message);
                }
                case ConnectionActionKind.Pause:
                {
                    if (!SafeApplicable(isStillApplicable)) return Report(action, false, "The connection changed before playback could be requested.");
                    var result = await playback.PauseAsync(token).ConfigureAwait(false);
                    return Report(action, result.WasSent, result.Message);
                }
                default:
                    return Report(action, false, "The action was missing its target.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Report(action, false, $"{exception.GetType().Name} while performing the action.");
        }
    }

    /// <summary>
    /// Reads the Windows default output right now (on the audio thread) and says whether it is still
    /// <paramref name="expectedEndpointId"/>. Any doubt (no audio control, a failed read) counts as no,
    /// because playing to the wrong speakers is worse than not playing.
    /// </summary>
    private async Task<bool> StillDefaultOutputAsync(string expectedEndpointId)
    {
        if (_audio is null) return false;
        try
        {
            var result = await _runOnAudioThread(() =>
            {
                var defaults = _audio.Read().Defaults;
                // Same preference as the Sound page: Multimedia first, then Console.
                string? current = null;
                if (!defaults.TryGetValue(AudioOutputRole.Multimedia, out current) || string.IsNullOrWhiteSpace(current))
                    defaults.TryGetValue(AudioOutputRole.Console, out current);
                var same = !string.IsNullOrWhiteSpace(current)
                    && string.Equals(current, expectedEndpointId, StringComparison.OrdinalIgnoreCase);
                return new AudioOutputWriteResult(same, same ? "unchanged" : "changed");
            }).ConfigureAwait(false);
            return result.Applied;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }

    private bool Report(ConnectionAction action, bool ok, string detail)
    {
        // The result text can name the user's device or output (shown in the UI), so the log keeps only a
        // stable code and an opaque reference to the target.
        Log($"event={(ok ? "done" : "failed")} action={action.Kind} target={AppleDeviceHub.Core.Presentation.LogText.OpaqueRef(action.EndpointId)}");
        return ok;
    }

    private static bool SafeApplicable(Func<bool>? check)
    {
        if (check is null) return true;
        try { return check(); }
        catch (Exception) { return false; }
    }

    private void Log(string message)
    {
        if (_log is null) return;
        try { _log($"origin=connection-actions {message}"); }
        catch (Exception) { /* Diagnostics must never interfere with the action run. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); }
        catch (AggregateException) { /* A cancellation callback cannot block shutdown. */ }
    }
}
