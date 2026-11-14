using System.Collections.ObjectModel;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Pairing;

/// <summary>Where the in-app pairing flow is.</summary>
public enum PairingState
{
    Idle,
    Searching,
    Pairing,
    Connecting,
    Paired,
    Failed,
}

/// <summary>
/// Drives the "Ready to pair" list in the welcome guide: searches while active, pairs the device the user
/// picks, then hands the new device to Clasp so it is selected and connected. Nothing is paired automatically.
/// </summary>
public sealed class PairingSessionViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the pairing attempt may take before it counts as timed out.</summary>
    public static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for Clasp to pick up the newly paired device.</summary>
    public static readonly TimeSpan AdoptTimeout = TimeSpan.FromSeconds(20);

    private const string SearchingText = "Looking for AirPods in pairing mode…";

    private readonly IPairingDiscovery _discovery;
    private readonly Action<Action> _dispatch;
    private readonly Func<PairingCandidate, CancellationToken, Task<bool>>? _adoptPairedDevice;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _log;

    private PairingState _state = PairingState.Idle;
    private string _statusText = "";
    private string? _pairingCandidateId;
    private bool _active;
    private bool _disposed;
    private int _generation;
    private CancellationTokenSource? _userCts;

    public PairingSessionViewModel(
        IPairingDiscovery discovery,
        Action<Action> dispatch,
        Func<PairingCandidate, CancellationToken, Task<bool>>? adoptPairedDevice = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _adoptPairedDevice = adoptPairedDevice;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
        _discovery.CandidatesChanged += OnCandidatesChanged;
        SyncCandidates();
    }

    /// <summary>Devices ready to pair, strongest signal first.</summary>
    public ObservableCollection<PairingCandidate> Candidates { get; } = new();

    public PairingState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsBusy));
            Raise(nameof(CanPair));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>The id of the device being paired right now, or null.</summary>
    public string? PairingCandidateId
    {
        get => _pairingCandidateId;
        private set => Set(ref _pairingCandidateId, value);
    }

    /// <summary>True while pairing or setting up a device.</summary>
    public bool IsBusy => _state is PairingState.Pairing or PairingState.Connecting;

    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>True when the user may press Pair.</summary>
    public bool CanPair => !IsBusy;

    /// <summary>Starts searching. Does nothing when already active.</summary>
    public void Activate()
    {
        if (_disposed || _active) return;
        _active = true;
        _generation++;
        SyncCandidates();
        StartSearching();
    }

    /// <summary>Stops searching and cancels any pairing in progress. Does nothing when not active.</summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _generation++;
        CancelUser();
        SafeStop();
        PairingCandidateId = null;
        State = PairingState.Idle;
    }

    /// <summary>Pairs the chosen device, then asks Clasp to adopt it. Ignored while busy.</summary>
    public async Task PairAsync(string candidateId)
    {
        if (_disposed || !_active || IsBusy || string.IsNullOrEmpty(candidateId)) return;

        var candidate = FindCandidate(candidateId);
        var name = candidate?.Name ?? "your AirPods";
        var generation = ++_generation;
        var userCts = new CancellationTokenSource();
        _userCts = userCts;

        SafeStop();
        PairingCandidateId = candidateId;
        StatusText = $"Pairing with {name}…";
        State = PairingState.Pairing;

        PairingOutcome outcome;
        try
        {
            using var timeoutCts = new CancellationTokenSource();
            using var timeoutTimer = _timeProvider.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), timeoutCts, PairTimeout, Timeout.InfiniteTimeSpan);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, timeoutCts.Token);
            try
            {
                outcome = await _discovery.PairAsync(candidateId, linked.Token);
            }
            catch (OperationCanceledException)
            {
                outcome = PairingOutcome.Canceled;
            }
            if (outcome == PairingOutcome.Canceled && timeoutCts.IsCancellationRequested && !userCts.IsCancellationRequested)
            {
                outcome = PairingOutcome.TimedOut;
            }
        }
        catch (Exception ex)
        {
            Log($"Pairing {name} failed: {ex.Message}");
            outcome = PairingOutcome.Failed;
        }

        if (generation != _generation || _disposed) return;

        if (outcome is PairingOutcome.Paired or PairingOutcome.AlreadyPaired)
        {
            await AdoptAsync(candidate ?? new PairingCandidate(candidateId, name, null), name, generation, userCts);
            return;
        }

        PairingCandidateId = null;
        if (outcome == PairingOutcome.Canceled)
        {
            StartSearching();
            return;
        }
        StatusText = PairingMessages.Describe(outcome, name);
        State = PairingState.Failed;
        SafeStart();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Deactivate();
        _disposed = true;
        _discovery.CandidatesChanged -= OnCandidatesChanged;
    }

    private async Task AdoptAsync(PairingCandidate candidate, string name, int generation, CancellationTokenSource userCts)
    {
        StatusText = $"Setting up {name}…";
        State = PairingState.Connecting;

        var adopted = false;
        using var timeoutCts = new CancellationTokenSource(AdoptTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, timeoutCts.Token);
        try
        {
            if (_adoptPairedDevice is not null)
            {
                adopted = await _adoptPairedDevice(candidate, linked.Token).WaitAsync(AdoptTimeout, _timeProvider, userCts.Token);
            }
        }
        catch (TimeoutException)
        {
            Log($"Setting up {name} timed out.");
            linked.Cancel();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !userCts.IsCancellationRequested)
        {
            Log($"Setting up {name} timed out.");
            linked.Cancel();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log($"Setting up {name} failed: {ex.Message}");
            if (generation != _generation || _disposed) return;
            PairingCandidateId = null;
            StatusText = PairingMessages.Describe(PairingOutcome.Failed, name);
            State = PairingState.Failed;
            SafeStart();
            return;
        }

        if (generation != _generation || _disposed) return;
        PairingCandidateId = null;
        StatusText = adopted
            ? $"{name} is paired and ready."
            : $"{name} is paired. If it doesn't show up below, click Refresh.";
        State = PairingState.Paired;
    }

    private void StartSearching()
    {
        SafeStart();
        UpdateSearchingStatus();
        State = PairingState.Searching;
    }

    private void UpdateSearchingStatus()
    {
        StatusText = Candidates.Count switch
        {
            0 => SearchingText,
            1 => $"{Candidates[0].Name} is ready to pair.",
            var n => $"Found {n} ready to pair.",
        };
    }

    private PairingCandidate? FindCandidate(string id)
    {
        foreach (var c in Candidates)
        {
            if (c.Id == id) return c;
        }
        return null;
    }

    private void OnCandidatesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _dispatch(() =>
        {
            if (_disposed) return;
            SyncCandidates();
            if (_active && _state == PairingState.Searching) UpdateSearchingStatus();
        });
    }

    private void SyncCandidates()
    {
        var ordered = PairingCandidateFilter.Order(_discovery.Candidates);
        if (!ordered.SequenceEqual(Candidates))
        {
            // Sync in place so devices that remain never disappear from the list mid-update (a Clear would
            // make the UI destroy and rebuild every row, dropping keyboard focus).
            var keep = new HashSet<string>(ordered.Select(c => c.Id));
            for (int i = Candidates.Count - 1; i >= 0; i--)
                if (!keep.Contains(Candidates[i].Id)) Candidates.RemoveAt(i);

            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                if (i < Candidates.Count && Candidates[i].Id == c.Id)
                {
                    if (Candidates[i] != c) Candidates[i] = c;
                    continue;
                }
                int j = -1;
                for (int k = i + 1; k < Candidates.Count; k++)
                    if (Candidates[k].Id == c.Id) { j = k; break; }
                if (j >= 0)
                {
                    Candidates.Move(j, i);
                    if (Candidates[i] != c) Candidates[i] = c;
                }
                else Candidates.Insert(i, c);
            }
            while (Candidates.Count > ordered.Count) Candidates.RemoveAt(Candidates.Count - 1);
        }
        Raise(nameof(HasCandidates));
    }

    private void CancelUser()
    {
        var cts = _userCts;
        _userCts = null;
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (Exception ex) { Log($"Cancel failed: {ex.Message}"); }
    }

    private void SafeStart()
    {
        try { _discovery.Start(); }
        catch (Exception ex) { Log($"Starting the search failed: {ex.Message}"); }
    }

    private void SafeStop()
    {
        try { _discovery.Stop(); }
        catch (Exception ex) { Log($"Stopping the search failed: {ex.Message}"); }
    }

    private void Log(string message) => _log?.Invoke(message);
}
