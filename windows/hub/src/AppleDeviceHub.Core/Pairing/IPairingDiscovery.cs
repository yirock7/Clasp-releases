namespace AppleDeviceHub.Core.Pairing;

/// <summary>Finds AirPods and Beats in pairing mode and pairs one on request.</summary>
public interface IPairingDiscovery : IDisposable
{
    /// <summary>Raised (on any thread) when the candidate list changes.</summary>
    event EventHandler? CandidatesChanged;

    /// <summary>The devices found so far.</summary>
    IReadOnlyList<PairingCandidate> Candidates { get; }

    /// <summary>True while the search is running.</summary>
    bool IsRunning { get; }

    /// <summary>Starts searching. Does nothing when already running.</summary>
    void Start();

    /// <summary>Stops searching. Does nothing when not running.</summary>
    void Stop();

    /// <summary>Pairs the device with this id. Returns the outcome instead of throwing for an expected failure.</summary>
    Task<PairingOutcome> PairAsync(string candidateId, CancellationToken cancellationToken);
}
