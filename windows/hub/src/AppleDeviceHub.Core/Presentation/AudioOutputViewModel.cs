using System.Collections.ObjectModel;
using System.ComponentModel;
using AppleDeviceHub.Core.Audio;

namespace AppleDeviceHub.Core.Presentation;

public sealed class AudioOutputChoice : ObservableObject
{
    private string _label = "";
    private string _name = "";
    private bool _isAvailable;
    public AudioOutputChoice(AudioOutputEndpoint endpoint) { Id = endpoint.Id; Update(endpoint); }
    public string Id { get; }
    public string? ContainerId { get; private set; }
    public string Label { get => _label; private set => Set(ref _label, value); }
    public bool IsAvailable { get => _isAvailable; private set => Set(ref _isAvailable, value); }
    internal void Update(AudioOutputEndpoint endpoint)
    {
        ContainerId = endpoint.ContainerId;
        _name = endpoint.Name;
        IsAvailable = endpoint.IsAvailable;
        Label = _name + (IsAvailable ? "" : " (disconnected)");
    }

    internal void MarkUnavailable()
    {
        IsAvailable = false;
        Label = _name + " (disconnected)";
    }

    // Picker items show this text; it is also the name screen readers announce for the item.
    public override string ToString() => Label;
}

/// <summary>Settings and coordinator for previous-output restoration, independent of the AirPods driver.</summary>
public sealed class AudioOutputViewModel : ObservableObject, IDisposable
{
    private readonly IAudioOutputController _controller;
    private readonly HubSettings _settings;
    private readonly Action<Action> _dispatch;
    private readonly Action<string>? _restorationLog;
    private readonly AudioOutputRestorationPolicy _policy = new();
    private bool _changingSelection;
    private bool _disposed;
    private long _historyEpoch;
    private long _appliedHistoryEpoch;
    private string _statusText = "Not started";
    private string _lastActionText = "";
    private string _selectedTargetStatusText = "Choose an output.";
    private string _volumeStatusText = "Choose an output to adjust its volume.";
    private float? _selectedOutputVolume;
    private string? _currentWindowsOutputId;
    private string _currentWindowsOutputName = "";
    private float? _currentWindowsOutputVolume;
    private bool _canAdjustCurrentOutput;
    private string _currentWindowsOutputVolumeStatusText = "Volume isn't available right now.";
    private long _currentWindowsOutputGeneration;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _log;
    private ITimer? _rereadTimer;
    private int _rereadToken;
    private int _rereadStep;
    private bool _rereadsActive;
    private DateTimeOffset _rereadStartedAt;

    /// <summary>
    /// Windows can report a missing or zero volume for a moment after the default output changes
    /// (a Bluetooth endpoint is still coming up). After a change, or an unreadable volume, the
    /// displayed volume is re-read at these offsets. Display only: a re-read never writes a volume.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> VolumeRereadOffsets =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    public AudioOutputViewModel(IAudioOutputController controller, HubSettings settings, Action<Action> dispatch,
        Action<string>? restorationLog = null, TimeProvider? timeProvider = null, Action<string>? log = null)
    {
        _controller = controller;
        _settings = settings;
        _dispatch = dispatch;
        _restorationLog = restorationLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
        _controller.Changed += OnChanged;
        _settings.PropertyChanged += OnSettingChanged;
        ApplyConfiguration();
    }

    public ObservableCollection<AudioOutputChoice> Outputs { get; } = new();

    /// <summary>
    /// When the user last picked a Windows output with Clasp's own "Use ... output" buttons, or
    /// null. Connection-triggered actions leave the output alone for a while after this.
    /// </summary>
    public DateTimeOffset? LastManualOutputChoiceAt { get; private set; }

    /// <summary>When restore-previous-output last asked Windows to restore an output, or null.</summary>
    public DateTimeOffset? LastRestoreRequestedAt { get; private set; }

    /// <summary>
    /// The output Clasp remembered from before the AirPods became the default (Multimedia, then
    /// Console), else the chosen fallback output, else null. Never a guess.
    /// </summary>
    public string? PreviousOutputEndpointId
        => _policy.RestoreDestination(AudioOutputRole.Multimedia)
            ?? _policy.RestoreDestination(AudioOutputRole.Console)
            ?? (string.IsNullOrWhiteSpace(_settings.AudioFallbackEndpointId) ? null : _settings.AudioFallbackEndpointId);

    public AudioOutputChoice? SelectedTarget => Outputs.FirstOrDefault(output => output.Id == _settings.AudioTargetEndpointId);
    public AudioOutputChoice? SelectedFallback => Outputs.FirstOrDefault(output => output.Id == _settings.AudioFallbackEndpointId);
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string LastActionText { get => _lastActionText; private set => Set(ref _lastActionText, value); }
    /// <summary>Availability and default status from Windows render endpoints only; no BLE state is used.</summary>
    public string SelectedTargetStatusText { get => _selectedTargetStatusText; private set => Set(ref _selectedTargetStatusText, value); }
    public float? SelectedOutputVolume { get => _selectedOutputVolume; private set => Set(ref _selectedOutputVolume, value); }
    public string VolumeStatusText { get => _volumeStatusText; private set => Set(ref _volumeStatusText, value); }
    /// <summary>The active Windows render output, based on Multimedia default (Console when Multimedia has no default).</summary>
    public string? CurrentWindowsOutputId { get => _currentWindowsOutputId; private set => Set(ref _currentWindowsOutputId, value); }
    /// <summary>Changes when this view model observes a different current Windows output ID.</summary>
    /// <remarks>A switch away and back between all snapshots is not observable through the current audio contract.</remarks>
    public long CurrentWindowsOutputGeneration { get => _currentWindowsOutputGeneration; private set => Set(ref _currentWindowsOutputGeneration, value); }
    /// <summary>The Windows name of the current output, or empty when its metadata is unavailable.</summary>
    public string CurrentWindowsOutputName { get => _currentWindowsOutputName; private set => Set(ref _currentWindowsOutputName, value); }
    /// <summary>Master volume for the current Windows output, clamped to 0 through 1 when readable.</summary>
    public float? CurrentWindowsOutputVolume { get => _currentWindowsOutputVolume; private set => Set(ref _currentWindowsOutputVolume, value); }
    /// <summary>Whether the current output is available and its master volume can be read.</summary>
    public bool CanAdjustCurrentOutput { get => _canAdjustCurrentOutput; private set => Set(ref _canAdjustCurrentOutput, value); }
    public string CurrentWindowsOutputVolumeStatusText
    {
        get => _currentWindowsOutputVolumeStatusText;
        private set => Set(ref _currentWindowsOutputVolumeStatusText, value);
    }

    public void SelectTarget(string id)
    {
        if (_disposed) return;
        var target = Outputs.FirstOrDefault(output => output.Id == id);
        if (string.Equals(_settings.AudioTargetEndpointId, target?.Id ?? "", StringComparison.OrdinalIgnoreCase)
            && string.Equals(_settings.AudioTargetContainerId, target?.ContainerId ?? "", StringComparison.OrdinalIgnoreCase)) return;
        _changingSelection = true;
        try
        {
            _settings.AudioTargetEndpointId = target?.Id ?? "";
            _settings.AudioTargetContainerId = target?.ContainerId ?? "";
        }
        finally { _changingSelection = false; }
        ApplyConfiguration();
    }

    public void SelectFallback(string id)
    {
        if (_disposed || _settings.AudioFallbackEndpointId == id) return;
        _settings.AudioFallbackEndpointId = Outputs.Any(output => output.Id == id) ? id : "";
    }

    /// <summary>Explicit UI action to make an available Windows output the default for every output role.</summary>
    public void UseOutput(string endpointId)
    {
        if (_disposed) return;
        var target = Outputs.FirstOrDefault(output => output.Id == endpointId);
        if (target is null || !target.IsAvailable)
        {
            LastActionText = "That output is disconnected, so nothing was changed.";
            return;
        }
        try
        {
            var result = _controller.TrySetDefaultOutput(target.Id);
            LastActionText = result.Detail;
            if (result.Applied)
            {
                LastManualOutputChoiceAt = DateTimeOffset.UtcNow;
                Refresh();
            }
        }
        catch (Exception)
        {
            LastActionText = "Couldn't switch the sound output.";
        }
    }

    /// <summary>Explicit UI action for the selected Windows output, with a normalized value from 0 to 1.</summary>
    public void SetOutputVolume(string endpointId, float scalar)
    {
        if (_disposed) return;
        if (!float.IsFinite(scalar) || scalar < 0f || scalar > 1f)
        {
            VolumeStatusText = "Choose a volume from 0% to 100%.";
            return;
        }
        if (!string.Equals(endpointId, _settings.AudioTargetEndpointId, StringComparison.OrdinalIgnoreCase))
        {
            VolumeStatusText = "Choose this output first, then adjust its volume.";
            return;
        }
        if (SelectedOutputVolume is float current && Math.Abs(current - scalar) < 0.001f) return;
        if (SelectedTarget is not { IsAvailable: true })
        {
            VolumeStatusText = "This output is disconnected, so its volume wasn't changed.";
            return;
        }
        try
        {
            var result = _controller.TrySetVolume(endpointId, scalar);
            VolumeStatusText = result.Detail;
            if (result.Applied)
            {
                SelectedOutputVolume = ReadClampedVolume(endpointId);
                if (string.Equals(CurrentWindowsOutputId, endpointId, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentWindowsOutputVolume = SelectedOutputVolume;
                    CanAdjustCurrentOutput = CurrentWindowsOutputVolume is not null;
                    CurrentWindowsOutputVolumeStatusText = CurrentWindowsOutputVolume is null
                        ? "Can't read the volume for this output."
                        : result.Detail;
                }
            }
        }
        catch (Exception)
        {
            VolumeStatusText = "Volume isn't available right now.";
            SelectedOutputVolume = null;
        }
    }

    /// <summary>Explicit flyout action scoped to the Windows default output captured by that interaction.</summary>
    public void SetCurrentOutputVolume(string expectedEndpointId, float scalar)
        => SetCurrentOutputVolume(expectedEndpointId, CurrentWindowsOutputGeneration, scalar);

    /// <summary>Explicit flyout action fenced by the endpoint and output generation captured at interaction start.</summary>
    /// <remarks>The generation fences output changes observed in snapshots; unseen change-away-and-back transitions cannot be detected.</remarks>
    public void SetCurrentOutputVolume(string expectedEndpointId, long expectedGeneration, float scalar)
    {
        if (_disposed) return;
        if (!float.IsFinite(scalar) || scalar < 0f || scalar > 1f)
        {
            CurrentWindowsOutputVolumeStatusText = "Choose a volume from 0% to 100%.";
            return;
        }
        if (string.IsNullOrWhiteSpace(expectedEndpointId))
        {
            CurrentWindowsOutputVolumeStatusText = "No sound output to adjust.";
            return;
        }

        AudioOutputSnapshot snapshot;
        try
        {
            // Recheck the current Windows role before acting. The flyout may have been
            // open while Windows switched outputs and before its Changed event was dispatched.
            snapshot = _controller.Read();
        }
        catch (Exception)
        {
            ClearCurrentWindowsOutput("Volume isn't available right now.");
            return;
        }

        try { UpdateCurrentWindowsOutput(snapshot); }
        catch (Exception)
        {
            ClearCurrentWindowsOutput("Volume isn't available right now.");
            return;
        }
        if (!string.Equals(expectedEndpointId, CurrentWindowsOutputId, StringComparison.OrdinalIgnoreCase)
            || expectedGeneration != CurrentWindowsOutputGeneration)
        {
            CurrentWindowsOutputVolumeStatusText = "The output changed, so the volume wasn't adjusted.";
            return;
        }
        if (!CanAdjustCurrentOutput)
        {
            CurrentWindowsOutputVolumeStatusText = "Volume can't be adjusted for this output.";
            return;
        }
        if (CurrentWindowsOutputVolume is float current && Math.Abs(current - scalar) < 0.001f) return;

        try
        {
            var result = _controller.TrySetVolume(expectedEndpointId, scalar);
            CurrentWindowsOutputVolumeStatusText = result.Detail;
            if (result.Applied)
            {
                var readback = ReadClampedVolume(expectedEndpointId);
                CurrentWindowsOutputVolume = readback;
                CanAdjustCurrentOutput = readback is not null;
                if (string.Equals(_settings.AudioTargetEndpointId, expectedEndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedOutputVolume = readback;
                    VolumeStatusText = result.Detail;
                }
                if (readback is null)
                    CurrentWindowsOutputVolumeStatusText = "Volume changed, but Windows can't confirm the new level.";
            }
        }
        catch (Exception)
        {
            CurrentWindowsOutputVolumeStatusText = "Volume isn't available right now.";
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_changingSelection || _disposed) return;
        if (e.PropertyName is nameof(HubSettings.RestorePreviousAudioOutput) or nameof(HubSettings.AudioTargetEndpointId)
            or nameof(HubSettings.AudioTargetContainerId) or nameof(HubSettings.AudioFallbackEndpointId)) ApplyConfiguration();
    }

    private void ApplyConfiguration()
    {
        _appliedHistoryEpoch = Interlocked.Increment(ref _historyEpoch);
        _policy.Configure(_settings.RestorePreviousAudioOutput, _settings.AudioTargetEndpointId,
            _settings.AudioTargetContainerId, _settings.AudioFallbackEndpointId);
        LastActionText = "";
        Raise(nameof(SelectedTarget));
        Raise(nameof(SelectedFallback));
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => _dispatch(Refresh);

    public void Refresh()
    {
        if (_disposed) return;
        var epoch = Interlocked.Read(ref _historyEpoch);
        if (_appliedHistoryEpoch != epoch)
        {
            _policy.Forget();
            _appliedHistoryEpoch = epoch;
        }
        try
        {
            var snapshot = _controller.Read();
            if (Interlocked.Read(ref _historyEpoch) != epoch) return;
            var retainedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_settings.AudioTargetEndpointId)) retainedIds.Add(_settings.AudioTargetEndpointId);
            if (!string.IsNullOrWhiteSpace(_settings.AudioFallbackEndpointId)) retainedIds.Add(_settings.AudioFallbackEndpointId);
            if (!string.IsNullOrWhiteSpace(_settings.EarRemovedOutputEndpointId)) retainedIds.Add(_settings.EarRemovedOutputEndpointId);
            var visibleEndpoints = snapshot.Endpoints
                .Where(endpoint => endpoint.IsAvailable || retainedIds.Contains(endpoint.Id))
                .ToArray();
            foreach (var old in Outputs.Where(item => !visibleEndpoints.Any(endpoint => endpoint.Id == item.Id)).ToArray())
            {
                if (!snapshot.Endpoints.Any(endpoint => endpoint.Id == old.Id) && retainedIds.Contains(old.Id))
                    old.MarkUnavailable();
                else Outputs.Remove(old);
            }
            AddSavedUnavailableChoice(_settings.AudioTargetEndpointId, "Saved output", _settings.AudioTargetContainerId, snapshot);
            AddSavedUnavailableChoice(_settings.AudioFallbackEndpointId, "Saved return output", null, snapshot);
            AddSavedUnavailableChoice(_settings.EarRemovedOutputEndpointId, "Saved speaker output", null, snapshot);
            foreach (var endpoint in visibleEndpoints)
            {
                var item = Outputs.FirstOrDefault(output => output.Id == endpoint.Id);
                if (item is null) Outputs.Add(new AudioOutputChoice(endpoint));
                else item.Update(endpoint);
            }
            SortOutputsActiveFirst();
            Raise(nameof(SelectedTarget));
            Raise(nameof(SelectedFallback));
            UpdateSelectedTargetStatus(snapshot);
            UpdateSelectedOutputVolume();
            UpdateCurrentWindowsOutput(snapshot);
            var requests = _policy.Observe(snapshot);
            if (requests.Count > 0)
            {
                LastRestoreRequestedAt = DateTimeOffset.UtcNow;
                var outcomes = new List<string>(requests.Count);
                foreach (var request in requests)
                {
                    AudioOutputWriteResult result;
                    try
                    {
                        result = _controller.TryRestore(request with
                        {
                            IsCurrent = () => !_disposed && Interlocked.Read(ref _historyEpoch) == epoch,
                        });
                    }
                    catch (Exception)
                    {
                        result = new(false, "Couldn't restore the previous output.");
                    }

                    outcomes.Add($"{request.Role}: {result.Detail}");
                    LogRestoration(request, result);
                }
                LastActionText = string.Join("\n", outcomes);
            }
            StatusText = !_settings.RestorePreviousAudioOutput ? "Automatic restore is off."
                : string.IsNullOrWhiteSpace(_settings.AudioTargetEndpointId) ? "Choose your AirPods output below."
                : _policy.HasRememberedOutput ? "Your previous output is saved. Clasp switches back when your AirPods disconnect."
                : "Watching for your AirPods. If they're already your output, choose where sound should return.";
        }
        catch (Exception)
        {
            StatusText = "Automatic restore isn't available right now.";
            SelectedOutputVolume = null;
            VolumeStatusText = "Volume isn't available right now.";
            ClearCurrentWindowsOutput("Volume isn't available right now.");
        }
    }

    /// <summary>Refresh only the current Windows default output and its volume, without running restoration policy.</summary>
    public void RefreshCurrentWindowsOutput()
    {
        if (_disposed) return;
        try
        {
            UpdateCurrentWindowsOutput(_controller.Read());
        }
        catch (Exception)
        {
            ClearCurrentWindowsOutput("Volume isn't available right now.");
        }
    }

    private void LogRestoration(AudioOutputRestoreRequest request, AudioOutputWriteResult result)
    {
        try
        {
            _restorationLog?.Invoke(
                $"Audio restore role={request.Role}; expectedCurrent={request.ExpectedCurrentEndpointId ?? "<none>"}; " +
                $"restoreTarget={request.RestoreEndpointId}; applied={result.Applied}; result={result.Detail}");
        }
        catch (Exception)
        {
            // Diagnostics must not interfere with Windows routing or UI refresh.
        }
    }

    private void UpdateSelectedTargetStatus(AudioOutputSnapshot snapshot)
    {
        var target = SelectedTarget;
        if (target is null)
        {
            SelectedTargetStatusText = string.IsNullOrWhiteSpace(_settings.AudioTargetEndpointId)
                ? "Choose your AirPods output."
                : "Your saved output isn't available right now.";
            return;
        }
        if (!target.IsAvailable)
        {
            SelectedTargetStatusText = "This output is disconnected. Your choice is saved.";
            return;
        }

        var targetEndpointIds = snapshot.Endpoints.Where(endpoint =>
            string.Equals(endpoint.Id, target.Id, StringComparison.OrdinalIgnoreCase)
            || (target.ContainerId is not null && string.Equals(endpoint.ContainerId, target.ContainerId, StringComparison.OrdinalIgnoreCase)))
            .Select(endpoint => endpoint.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SelectedTargetStatusText = snapshot.Defaults.Values.Any(id => id is not null && targetEndpointIds.Contains(id))
            ? "Ready, and currently in use."
            : "Ready, but not currently in use.";
    }

    private void AddSavedUnavailableChoice(string id, string label, string? containerId, AudioOutputSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(id)
            || Outputs.Any(output => string.Equals(output.Id, id, StringComparison.OrdinalIgnoreCase))
            || snapshot.Endpoints.Any(endpoint => string.Equals(endpoint.Id, id, StringComparison.OrdinalIgnoreCase))) return;
        Outputs.Add(new AudioOutputChoice(new(id, label, false, containerId)));
    }

    private void SortOutputsActiveFirst()
    {
        var ordered = Outputs.OrderByDescending(output => output.IsAvailable)
            .ThenBy(output => output.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var currentIndex = Outputs.IndexOf(ordered[index]);
            if (currentIndex != index) Outputs.Move(currentIndex, index);
        }
    }

    private void UpdateSelectedOutputVolume()
    {
        var target = SelectedTarget;
        if (target is not { IsAvailable: true })
        {
            SelectedOutputVolume = null;
            VolumeStatusText = "Choose an output to adjust its volume.";
            return;
        }
        try
        {
            SelectedOutputVolume = ReadClampedVolume(target.Id);
            VolumeStatusText = SelectedOutputVolume is null
                ? "Volume can't be adjusted for this output."
                : "Volume for this output.";
        }
        catch (Exception)
        {
            SelectedOutputVolume = null;
            VolumeStatusText = "Volume isn't available right now.";
        }
    }

    private void UpdateCurrentWindowsOutput(AudioOutputSnapshot snapshot, bool isReread = false)
    {
        string? endpointId;
        if (!snapshot.Defaults.TryGetValue(AudioOutputRole.Multimedia, out endpointId) || string.IsNullOrWhiteSpace(endpointId))
            snapshot.Defaults.TryGetValue(AudioOutputRole.Console, out endpointId);
        if (string.IsNullOrWhiteSpace(endpointId)) endpointId = null;

        var endpoint = string.IsNullOrWhiteSpace(endpointId) ? null : snapshot.Endpoints.FirstOrDefault(item =>
            string.Equals(item.Id, endpointId, StringComparison.OrdinalIgnoreCase));
        var outputChanged = !string.Equals(CurrentWindowsOutputId, endpointId, StringComparison.OrdinalIgnoreCase);
        SetCurrentWindowsOutputId(endpointId);
        CurrentWindowsOutputName = endpoint?.Name ?? "";
        var available = endpoint is { IsAvailable: true };
        var readStarted = _timeProvider.GetTimestamp();
        var volume = available ? ReadClampedVolume(endpoint!.Id) : null;
        var readMs = (long)_timeProvider.GetElapsedTime(readStarted).TotalMilliseconds;
        var previousVolume = CurrentWindowsOutputVolume;
        CurrentWindowsOutputVolume = volume;
        CanAdjustCurrentOutput = available && volume is not null;
        TrackVolumeSettling(outputChanged, available, isReread, previousVolume, volume, readMs);
        CurrentWindowsOutputVolumeStatusText = !available
            ? string.IsNullOrWhiteSpace(endpointId)
                ? "No sound output is set."
                : "The current output isn't available."
            : volume is null
                ? "Volume can't be adjusted for this output."
                : "Volume for this output.";
    }

    /// <summary>
    /// Decides whether the displayed volume needs re-reading, and logs what Windows reported (no
    /// device names or ids). Display only: nothing here writes a volume.
    /// </summary>
    private void TrackVolumeSettling(
        bool outputChanged, bool available, bool isReread, float? previous, float? volume, long readMs)
    {
        if (!available)
        {
            CancelVolumeRereads();
            return;
        }
        if (outputChanged)
        {
            Log($"Audio volume: default output changed; first read {FormatVolume(volume)} ({readMs} ms).");
            StartVolumeRereads();
            return;
        }
        if (isReread)
        {
            if (!VolumesMatch(previous, volume))
                Log($"Audio volume: re-read {_rereadStep + 1} at {ElapsedSinceRereadStart()} ms now {FormatVolume(volume)}, was {FormatVolume(previous)}.");
            return;
        }
        if (volume is null && !_rereadsActive) StartVolumeRereads();
    }

    private long ElapsedSinceRereadStart()
        => (long)(_timeProvider.GetUtcNow() - _rereadStartedAt).TotalMilliseconds;

    private static bool VolumesMatch(float? a, float? b)
        => a is null || b is null ? a is null && b is null : Math.Abs(a.Value - b.Value) < 0.005f;

    private static string FormatVolume(float? volume)
        => volume is float scalar
            ? $"{Math.Round(scalar * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}%"
            : "unknown";

    private void Log(string message)
    {
        try { _log?.Invoke(message); }
        catch (Exception) { /* diagnostics must not interfere with the UI */ }
    }

    private void StartVolumeRereads()
    {
        CancelVolumeRereads();
        if (_disposed) return;
        var token = _rereadToken;
        _rereadsActive = true;
        _rereadStep = 0;
        _rereadStartedAt = _timeProvider.GetUtcNow();
        _rereadTimer = _timeProvider.CreateTimer(
            _ => OnRereadTimer(token), null, VolumeRereadOffsets[0], Timeout.InfiniteTimeSpan);
    }

    private void CancelVolumeRereads()
    {
        _rereadToken++;
        _rereadsActive = false;
        var timer = _rereadTimer;
        _rereadTimer = null;
        try { timer?.Dispose(); } catch (Exception) { /* already gone */ }
    }

    private void OnRereadTimer(int token)
    {
        if (_disposed) return;
        try { _dispatch(() => RunVolumeReread(token)); }
        catch (Exception) { /* the owner is closing */ }
    }

    private void RunVolumeReread(int token)
    {
        if (_disposed || token != _rereadToken) return;
        try { UpdateCurrentWindowsOutput(_controller.Read(), isReread: true); }
        catch (Exception) { /* a transient read failure keeps the last display; the next step tries again */ }
        // A change seen by this read starts a fresh schedule (new token); this one is then stale.
        if (_disposed || token != _rereadToken) return;

        _rereadStep++;
        if (_rereadStep >= VolumeRereadOffsets.Count)
        {
            CancelVolumeRereads();
            return;
        }
        _rereadTimer?.Change(VolumeRereadOffsets[_rereadStep] - VolumeRereadOffsets[_rereadStep - 1], Timeout.InfiniteTimeSpan);
    }

    private void ClearCurrentWindowsOutput(string status)
    {
        SetCurrentWindowsOutputId(null);
        CurrentWindowsOutputName = "";
        CurrentWindowsOutputVolume = null;
        CanAdjustCurrentOutput = false;
        CurrentWindowsOutputVolumeStatusText = status;
    }

    private float? ReadClampedVolume(string endpointId)
    {
        try
        {
            var volume = _controller.ReadVolume(endpointId);
            return volume is float scalar && float.IsFinite(scalar) ? Math.Clamp(scalar, 0f, 1f) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SetCurrentWindowsOutputId(string? endpointId)
    {
        if (string.Equals(CurrentWindowsOutputId, endpointId, StringComparison.OrdinalIgnoreCase)) return;
        CurrentWindowsOutputGeneration++;
        CurrentWindowsOutputId = endpointId;
    }

    /// <summary>Do not carry audio routing intent through sleep/resume.</summary>
    public void ForgetHistory()
    {
        // Invalidate synchronously on the power callback thread. A refresh already queued for
        // after wake must not consume pre-sleep intent before a later UI callback forgets it.
        Interlocked.Increment(ref _historyEpoch);
        _dispatch(() =>
        {
            if (_disposed) return;
            _policy.Forget();
            _appliedHistoryEpoch = Interlocked.Read(ref _historyEpoch);
            LastActionText = "";
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelVolumeRereads();
        Interlocked.Increment(ref _historyEpoch);
        _controller.Changed -= OnChanged;
        _settings.PropertyChanged -= OnSettingChanged;
        _policy.Forget();
        try { _controller.Dispose(); }
        catch (Exception) { StatusText = "Couldn't finish cleaning up audio."; }
    }
}
