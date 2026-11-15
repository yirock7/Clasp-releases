using System.Runtime.InteropServices.WindowsRuntime;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Pairing;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace AppleDeviceHub.App.Connection;

/// <summary>
/// Finds unpaired AirPods and Beats with a Windows device watcher (the same inquiry Windows Settings runs) and pairs one
/// on request with the "just works" ceremony. Anything that asks for a code is not accepted: it is reported so the
/// caller can point the user to Windows Settings. Nothing is paired without a call to <see cref="PairAsync"/>.
/// </summary>
internal sealed class WindowsPairingDiscovery : IPairingDiscovery
{
    private const string IsPresentKey = "System.Devices.Aep.IsPresent";
    private const string SignalStrengthKey = "System.Devices.Aep.SignalStrength";
    private const string CodMajorKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string ProtocolIdKey = "System.Devices.Aep.ProtocolId";
    private const string ClassicProtocolId = "e0cbf06c-cd8b-4647-bb8a-263b43f0f974";

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _devices = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;
    private List<PairingCandidate> _candidates = [];
    private bool _running;
    private bool _disposed;

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
        bool changed;
        lock (_gate)
        {
            if (_disposed || _running) return;

            // A watcher can only be started once and can only restart after it has fully stopped. So a restart always
            // gets a fresh watcher, and the old one is unhooked so a late event from it cannot touch the list.
            DropWatcher();
            _devices.Clear();
            changed = RebuildCandidates();

            var watcher = DeviceInformation.CreateWatcher(
                BluetoothDevice.GetDeviceSelectorFromPairingState(false),
                new[] { IsPresentKey, SignalStrengthKey, CodMajorKey, ProtocolIdKey },
                DeviceInformationKind.AssociationEndpoint);
            watcher.Added += OnAdded;
            watcher.Updated += OnUpdated;
            watcher.Removed += OnRemoved;
            watcher.EnumerationCompleted += OnEnumerationCompleted;
            watcher.Stopped += OnStopped;
            _watcher = watcher;
            try
            {
                watcher.Start();
                _running = true;
            }
            catch
            {
                DropWatcher();
                throw;
            }
        }
        if (changed) CandidatesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            StopWatcher(_watcher);
        }
    }

    public async Task<PairingOutcome> PairAsync(string candidateId, CancellationToken cancellationToken)
    {
        DeviceInformation? info;
        lock (_gate) _devices.TryGetValue(candidateId, out info);
        if (info is null) return PairingOutcome.NotFound;

        var outcome = await PairCoreAsync(info, cancellationToken).ConfigureAwait(false);
        if (outcome is PairingOutcome.Paired or PairingOutcome.AlreadyPaired)
        {
            bool changed;
            lock (_gate)
            {
                _devices.Remove(candidateId);
                changed = RebuildCandidates();
            }
            if (changed) CandidatesChanged?.Invoke(this, EventArgs.Empty);
        }
        return outcome;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
            DropWatcher();
        }
    }

    private static async Task<PairingOutcome> PairCoreAsync(DeviceInformation info, CancellationToken cancellationToken)
    {
        try
        {
            if (info.Pairing.IsPaired) return PairingOutcome.AlreadyPaired;
            if (!info.Pairing.CanPair) return PairingOutcome.NotFound;

            var custom = info.Pairing.Custom;
            bool codeRequested = false;
            void OnPairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
            {
                if (args.PairingKind == DevicePairingKinds.ConfirmOnly)
                {
                    args.Accept();
                }
                else
                {
                    // A PIN or number to compare is never shown here, and accepting it blind would remove its protection.
                    codeRequested = true;
                }
            }

            custom.PairingRequested += OnPairingRequested;
            try
            {
                var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Default)
                    .AsTask(cancellationToken).ConfigureAwait(false);
                var outcome = Map(result.Status, codeRequested);
                LocalLog.Write($"Pairing finished: {result.Status}.");
                return outcome;
            }
            finally
            {
                custom.PairingRequested -= OnPairingRequested;
            }
        }
        catch (OperationCanceledException)
        {
            return PairingOutcome.Canceled;
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Pairing failed ({ex.GetType().Name}).");
            return PairingOutcome.Failed;
        }
    }

    private static PairingOutcome Map(DevicePairingResultStatus status, bool codeRequested) => status switch
    {
        DevicePairingResultStatus.Paired => PairingOutcome.Paired,
        DevicePairingResultStatus.AlreadyPaired => PairingOutcome.AlreadyPaired,
        DevicePairingResultStatus.RejectedByHandler or DevicePairingResultStatus.PairingCanceled when codeRequested => PairingOutcome.NeedsWindowsSettings,
        DevicePairingResultStatus.AuthenticationNotAllowed
            or DevicePairingResultStatus.ProtectionLevelCouldNotBeMet
            or DevicePairingResultStatus.RequiredHandlerNotRegistered
            or DevicePairingResultStatus.NoSupportedProfiles => PairingOutcome.NeedsWindowsSettings,
        DevicePairingResultStatus.ConnectionRejected
            or DevicePairingResultStatus.RemoteDeviceHasAssociation
            or DevicePairingResultStatus.AccessDenied
            or DevicePairingResultStatus.AuthenticationFailure
            or DevicePairingResultStatus.RejectedByHandler => PairingOutcome.Rejected,
        DevicePairingResultStatus.AuthenticationTimeout => PairingOutcome.TimedOut,
        DevicePairingResultStatus.PairingCanceled => PairingOutcome.Canceled,
        // NotReadyToPair, NotPaired, TooManyConnections, HardwareFailure, InvalidCeremonyData,
        // OperationAlreadyInProgress, Failed and anything new.
        _ => PairingOutcome.Failed,
    };

    // ----- Watcher events (thread-pool threads) -----

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
        => Apply(sender, () => _devices[info.Id] = info);

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        => Apply(sender, () =>
        {
            if (_devices.TryGetValue(update.Id, out var info)) info.Update(update);
        });

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        => Apply(sender, () => _devices.Remove(update.Id));

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        // The list is already current; the watcher keeps reporting changes after this.
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        lock (_gate)
        {
            if (ReferenceEquals(sender, _watcher)) _running = false;
        }
    }

    private void Apply(DeviceWatcher sender, Action change)
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            try { change(); }
            catch (Exception ex) { LocalLog.Write($"Pairing search update failed ({ex.GetType().Name})."); return; }
            changed = RebuildCandidates();
        }
        if (changed) CandidatesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ----- Helpers (call with the lock held) -----

    private bool RebuildCandidates()
    {
        var next = new List<PairingCandidate>();
        foreach (var info in _devices.Values)
        {
            if (TryToCandidate(info) is { } candidate) next.Add(candidate);
        }
        next.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        if (next.SequenceEqual(_candidates)) return false;
        _candidates = next;
        return true;
    }

    private static PairingCandidate? TryToCandidate(DeviceInformation info)
    {
        var props = info.Properties;
        int? codMajor = props.TryGetValue(CodMajorKey, out var cod) ? ToInt(cod) : null;
        if (!PairingCandidateFilter.IsCandidate(info.Name, codMajor)) return null;

        if (props.TryGetValue(ProtocolIdKey, out var protocol) && protocol is not null
            && !string.Equals(protocol.ToString(), ClassicProtocolId, StringComparison.OrdinalIgnoreCase)) return null;
        if (props.TryGetValue(IsPresentKey, out var present) && present is false) return null;

        int? signal = props.TryGetValue(SignalStrengthKey, out var strength) ? ToInt(strength) : null;
        return new PairingCandidate(info.Id, info.Name, signal);
    }

    private static int? ToInt(object? value) => value switch
    {
        null => null,
        sbyte v => v,
        byte v => v,
        short v => v,
        ushort v => v,
        int v => v,
        uint v => v <= int.MaxValue ? (int)v : null,
        long v => v is >= int.MinValue and <= int.MaxValue ? (int)v : null,
        _ => null,
    };

    private static void StopWatcher(DeviceWatcher? watcher)
    {
        if (watcher is null) return;
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Stopping the pairing search failed ({ex.GetType().Name}).");
        }
    }

    private void DropWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        watcher.Stopped -= OnStopped;
        StopWatcher(watcher);
    }
}
