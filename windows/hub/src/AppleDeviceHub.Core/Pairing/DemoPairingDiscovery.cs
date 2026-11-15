namespace AppleDeviceHub.Core.Pairing;

/// <summary>A pretend discovery for demo mode and screenshots. It never touches real Bluetooth.</summary>
public sealed class DemoPairingDiscovery : IPairingDiscovery
{
    private static readonly TimeSpan AppearDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PairDelay = TimeSpan.FromSeconds(1.5);
    private static readonly PairingCandidate DemoCandidate = new("demo-airpods-pro", "AirPods Pro", -48);

    private readonly TimeProvider _timeProvider;
    private readonly bool _appearImmediately;
    private readonly object _gate = new();
    private List<PairingCandidate> _candidates = [];
    private ITimer? _appearTimer;
    private bool _running;
    private bool _paired;
    private bool _disposed;

    public DemoPairingDiscovery(TimeProvider? timeProvider = null, bool appearImmediately = false)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _appearImmediately = appearImmediately;
    }

    public event EventHandler? CandidatesChanged;

    public IReadOnlyList<PairingCandidate> Candidates
    {
        get { lock (_gate) return _candidates; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public void Start()
    {
        bool appearNow = false;
        lock (_gate)
        {
            if (_disposed || _running) return;
            _running = true;
            if (_paired || _candidates.Count > 0) return;
            if (_appearImmediately) appearNow = true;
            else _appearTimer = _timeProvider.CreateTimer(_ => Appear(), null, AppearDelay, Timeout.InfiniteTimeSpan);
        }
        if (appearNow) Appear();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _appearTimer?.Dispose();
            _appearTimer = null;
        }
    }

    public async Task<PairingOutcome> PairAsync(string candidateId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PairDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PairingOutcome.Canceled;
        }
        lock (_gate)
        {
            _paired = true;
            _candidates = _candidates.Where(c => c.Id != candidateId).ToList();
        }
        CandidatesChanged?.Invoke(this, EventArgs.Empty);
        return PairingOutcome.Paired;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _running = false;
            _appearTimer?.Dispose();
            _appearTimer = null;
        }
    }

    private void Appear()
    {
        lock (_gate)
        {
            if (_disposed || _paired || _candidates.Count > 0) return;
            _candidates = [DemoCandidate];
        }
        CandidatesChanged?.Invoke(this, EventArgs.Empty);
    }
}
