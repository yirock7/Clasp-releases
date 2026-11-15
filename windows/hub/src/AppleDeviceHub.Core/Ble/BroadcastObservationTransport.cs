using AppleDeviceHub.Core.Contracts;
using System.Globalization;
using AppleDeviceHub.Core.Interop;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Ble;

/// <summary>Where the observation is in its life cycle.</summary>
public enum BroadcastPhase
{
    /// <summary>The user has not started observing (or stopped). Nothing is being listened to.</summary>
    NotObserving = 0,

    /// <summary>Listening; no attributable broadcast heard yet.</summary>
    Waiting = 1,

    /// <summary>An attributed broadcast was heard within <see cref="BroadcastObservationOptions.StaleAfter"/>.</summary>
    Fresh = 2,

    /// <summary>Last attributed broadcast is older than <see cref="BroadcastObservationOptions.StaleAfter"/>.</summary>
    Stale = 3,

    /// <summary>Nothing attributable heard for <see cref="BroadcastObservationOptions.LostAfter"/>: Nearby is reported false.</summary>
    Lost = 4,

    /// <summary>More than one candidate source is nearby; no values are shown.</summary>
    Ambiguous = 5,
}

public sealed record BroadcastObservationOptions
{
    /// <summary>
    /// Values older than this are stale (ears are dropped, battery shown with its age). AirPods in the ears
    /// broadcast at an unknown, fairly slow rate (a capture heard each pod at least once in 45 s), so this
    /// is two gaps of that, not one: a single missed broadcast must not flip the display.
    /// </summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// After this long without an attributed broadcast, Nearby becomes false. The last reading stays
    /// on screen with its age regardless; this only changes the "nearby" claim.
    /// </summary>
    public TimeSpan LostAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// If observing is on and no Apple advertisement at all (any type, any device) arrives for this long, the
    /// listener is restarted, at most once per interval. Apple devices are almost always nearby, so a longer
    /// silence means the platform watcher died quietly (for example after sleep).
    /// </summary>
    public TimeSpan WatchdogAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>After the listener stopped or a restart failed (radio off, adapter error), try again this often.</summary>
    public TimeSpan RestartRetryInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>An identical payload heard again within this window does not publish a new state.</summary>
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// While the last reading is stale or lost it stays on screen; it is republished this often so the
    /// shown age ("12 min ago") keeps up. Not a claim that anything was heard.
    /// </summary>
    public TimeSpan AgeRefreshInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// While identical packets keep arriving and the shown message carries ear placement, the state is republished this often so the
    /// placement's observation time stays inside the artwork's freshness window (15 s). Without it the buds drew as "in the case"
    /// once the last published placement aged out, although the same worn state was still being heard.
    /// </summary>
    public TimeSpan EarRefreshInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Minimum time between two "why broadcasts are refused" diagnostics, so a noisy area cannot flood the log.</summary>
    public TimeSpan DiagnosticInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often staleness is re-evaluated while observing.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Most Apple advertisements per second, over all addresses, that get identity work; the rest are counted and dropped.</summary>
    public double IngressGlobalRatePerSecond { get; init; } = 50;

    /// <summary>Burst size of the global ingress bucket.</summary>
    public int IngressGlobalBurst { get; init; } = 100;

    /// <summary>Most advertisements per second from one address that get identity work.</summary>
    public double IngressPerAddressRatePerSecond { get; init; } = 5;

    /// <summary>Burst size of each per-address ingress bucket.</summary>
    public int IngressPerAddressBurst { get; init; } = 10;

    public BroadcastAttributionOptions Attribution { get; init; } = new();

    internal void Validate()
    {
        if (IngressGlobalRatePerSecond <= 0 || double.IsNaN(IngressGlobalRatePerSecond)) throw new ArgumentOutOfRangeException(nameof(IngressGlobalRatePerSecond));
        if (IngressGlobalBurst < 1) throw new ArgumentOutOfRangeException(nameof(IngressGlobalBurst));
        if (IngressPerAddressRatePerSecond <= 0 || double.IsNaN(IngressPerAddressRatePerSecond)) throw new ArgumentOutOfRangeException(nameof(IngressPerAddressRatePerSecond));
        if (IngressPerAddressBurst < 1) throw new ArgumentOutOfRangeException(nameof(IngressPerAddressBurst));
        if (StaleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(StaleAfter));
        if (LostAfter <= StaleAfter) throw new ArgumentOutOfRangeException(nameof(LostAfter), "LostAfter must exceed StaleAfter.");
        if (DuplicateWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(DuplicateWindow));
        if (TickInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TickInterval));
        if (WatchdogAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(WatchdogAfter));
        if (RestartRetryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RestartRetryInterval));
        if (AgeRefreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(AgeRefreshInterval));
        if (EarRefreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(EarRefreshInterval));
        if (DiagnosticInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(DiagnosticInterval));
    }
}

/// <summary>
/// Driver-free, read-only transport: decodes Apple proximity broadcasts heard by a passive
/// <see cref="IAdvertisementSource"/>. Nothing is listened to until the user starts observing
/// (<see cref="ConnectCommand"/>); <see cref="DisconnectCommand"/> stops. Nothing is ever written to
/// a device, so every setting command answers <see cref="CommandOutcome.Unsupported"/>.
/// <para>
/// Honesty rules: origin is <see cref="DataOrigin.Broadcast"/>; identity is never confirmed
/// (<see cref="IsIdentityConfirmed"/>); battery is approximate (10 % steps); ear placement is
/// approximate and display-only. Ear bits never drive media controls. The UI may use a stable,
/// unique lid-open snapshot for an explicitly enabled nearby prompt; that does not match the
/// broadcast to a paired identity or establish an AirPods control connection. Optional Windows
/// audio reconnect is a separate user setting and targets only the explicitly selected paired device.
/// The control-session and Windows-audio facts from this broadcast stay unknown.
/// </para>
/// Owns the source and disposes it. Event handlers run under an internal lock and must not block.
/// </summary>
public sealed class BroadcastObservationTransport : IAirPodsTransport, IHardwareTransportStatus
{
    public const string DeviceId = "ble-broadcast";

    public const string TransportName = "Bluetooth LE broadcast";

    private static string ReadOnlyText => Strings.Ble_Broadcast_ReadOnly;

    private readonly IAdvertisementSource _source;
    private readonly TimeProvider _time;
    private readonly BroadcastAttributionPolicy _policy;
    private readonly PairedIdentityMatcher _matcher = new();
    private readonly Lock _gate = new();
    private ulong? _lastAddress;
    private IdentityConfidence _identity = IdentityConfidence.Unknown;
    private string? _pairedDeviceId;

    private bool _started;
    private bool _observing;
    private bool _disposed;
    private long _generation;

    /// <summary>
    /// The observation generation whose Start last asked the source to run (0 = none). Only that
    /// owner may stop the source: a retired start that fails or is cancelled late must not stop
    /// the source a newer start is using (review EF-IR-01).
    /// </summary>
    private long _sourceOwner;
    private long _sequence;
    private ITimer? _tick;
    private DeviceState? _current;
    private string? _unavailableReason;
    private BroadcastPhase _phase = BroadcastPhase.NotObserving;
    private AttributionDecision? _lastDecision;

    private ProximityMessage? _last;
    /// <summary>The newest accepted message per address, so the two buds of a split pair can be merged (see <see cref="MergeSiblingLocked"/>).</summary>
    private readonly Dictionary<ulong, (ProximityMessage Message, DateTimeOffset At)> _recentMessages = new();
    private DateTimeOffset _lastHeardAt;
    private DateTimeOffset _valuesAt;
    private DateTimeOffset _publishedAt;

    /// <summary>Last case level heard this run. Earbuds in the ears report the case as unknown, so the case keeps its last value and age.</summary>
    private (int Percent, bool? Charging, DateTimeOffset At)? _lastCase;

    /// <summary>
    /// A case level remembered from an earlier run (supplied by the host once per selection) or carried over by the user's
    /// Stop from the newest real reading, with its original time. Display only, and only used while no real reading is
    /// held and the shown source is likely the selected pair. It carries no charging flag. Only the transport changes it
    /// after the host's initial hand-over: it is cleared with <see cref="_lastCase"/> whenever the case may belong to another
    /// pair (selection change, ambiguity, a clearly different pair), replaced by a real reading, and kept across Stop/Start.
    /// </summary>
    private (int Percent, DateTimeOffset At)? _rememberedCase;
    private bool _sourceHealthy;
    /// <summary>
    /// The listener restart in progress, if any (set under the lock). Its generation is in
    /// <see cref="_restartGeneration"/>. A Stop, a new Start and Dispose wait for it before they finish, so a
    /// retired restart can never leave the listener scanning or stop a newer observation's listener.
    /// </summary>
    private Task? _restartTask;
    private long _restartGeneration;
    private DateTimeOffset _nextRestartAt;
    private DateTimeOffset _lastSightingAt;
    private DateTimeOffset _lastWatchdogAt;
    private string? _lastReasonKey;
    private readonly IngressLimiter _ingress;
    private long _rateLimitedSightings;
    private long _rateLimitReportedAt;
    private DateTimeOffset _lastRateLimitLoggedAt;
    private DateTimeOffset _lastReasonLoggedAt;
    private int _ambiguousCount;
    private readonly Dictionary<(ulong Address, ushort Model), BroadcastSource> _sources = new();
    private int _nextSourceNumber;
    private bool _sourcesDirty;

    /// <summary>Recent sources for explicit, read-only inspection; never attributed to the owner.</summary>
    public IReadOnlyList<BroadcastSource> Sources
    {
        get
        {
            lock (_gate)
                return CollapseSiblings(_sources.Values.Where(s => _time.GetUtcNow() - s.LastHeardAt <= Options.Attribution.CandidateWindow))
                    .OrderBy(s => s.Number).ToArray();
        }
    }

    /// <summary>
    /// The two buds of a pair are heard as two addresses; the list shows one source per pair. It keeps
    /// the oldest member's id and number (stable while the buds alternate), takes the values of the
    /// newest member, and never lets a member that did not report a field (lid state) blank one that
    /// another did: a pod in the case wins over "not in the case", then the newest known lid state.
    /// </summary>
    private IEnumerable<BroadcastSource> CollapseSiblings(IEnumerable<BroadcastSource> heard)
    {
        var groups = BroadcastSiblings.GroupSiblings(heard.OrderBy(s => s.Number), ReadingOf, Options.Attribution.BatteryTolerancePercent);
        foreach (var group in groups)
        {
            if (group.Count == 1) { yield return group[0]; continue; }
            var newest = group.MaxBy(s => s.LastHeardAt)!;
            var lidReporter = group.Where(s => s.LidClosed is not null).MaxBy(s => s.LastHeardAt);
            bool? inCase = group.Any(s => s.ThisPodInCase == true) ? true
                : group.Any(s => s.ThisPodInCase == false) ? false : null;
            yield return newest with
            {
                Id = group[0].Id,
                Number = group[0].Number,
                CaseBattery = group.Where(s => s.CaseBattery is not null).MaxBy(s => s.LastHeardAt)?.CaseBattery,
                ThisPodInCase = inCase,
                LidClosed = lidReporter?.LidClosed,
                LidCounter = lidReporter?.LidCounter ?? newest.LidCounter,
            };
        }
    }

    private static PodReading ReadingOf(BroadcastSource s) => new(s.ModelId, s.LeftBattery, s.RightBattery, s.CaseBattery, s.LidCounter, s.ThisPodInCase);

    /// <summary>Raised on any thread. Consumers read the latest snapshot on their UI thread.</summary>
    public event EventHandler? SourcesChanged;

    /// <summary>Raised for identical accepted packets so stable ear filtering can count fresh sightings without republishing duplicate UI state.</summary>
    public event EventHandler<DeviceState>? EarSightingObserved;

    public BroadcastObservationTransport(IAdvertisementSource source, TimeProvider? time = null, BroadcastObservationOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? TimeProvider.System;
        Options = options ?? new BroadcastObservationOptions();
        Options.Validate();
        _policy = new BroadcastAttributionPolicy(Options.Attribution);
        _ingress = new IngressLimiter(Options);
        _source.Received += OnReceived;
        _source.StatusChanged += OnSourceStatusChanged;
    }

    public BroadcastObservationOptions Options { get; }

    public TransportInfo Info { get; } = new(TransportName, DataOrigin.Broadcast, HubContract.Version,
        "Listens to nearby Apple proximity broadcasts after the user starts observing. Read-only; values approximate; identity unconfirmed.");

    public DeviceState? Current { get { lock (_gate) return _current; } }

    public bool IsObserving { get { lock (_gate) return _observing; } }

    bool? IHardwareTransportStatus.ObservationActive => IsObserving;

    public BroadcastPhase Phase { get { lock (_gate) return _phase; } }

    /// <summary>The policy's answer for the most recent sighting (null before any).</summary>
    public AttributionDecision? LastDecision { get { lock (_gate) return _lastDecision; } }

    /// <summary>
    /// Always false: cryptographic confirmation needs the IRK, which is not available without the AAP
    /// session. For the honest likelihood use <see cref="IdentityConfidence"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance property read through the transport by the UI.")]
    public bool IsIdentityConfirmed => false;

    /// <summary>
    /// How likely the source whose values are shown is the selected paired AirPods. Unknown unless
    /// a paired device model was set and the shown source is its only match; never a proof.
    /// </summary>
    public IdentityConfidence IdentityConfidence
    {
        get { lock (_gate) return ShownIdentityLocked(); }
    }

    private IdentityConfidence ShownIdentityLocked()
        => _phase is BroadcastPhase.Fresh or BroadcastPhase.Stale ? _identity : IdentityConfidence.Unknown;

    /// <summary>
    /// Sets the selected paired device's Apple model id and stable internal selection key (null when
    /// none or unknown). Also restricts accepted broadcasts to that model. A selection change clears
    /// all prior source values and correlation clues, including when the replacement has the same model.
    /// </summary>
    public void SetPairedDevice(ushort? modelId, string? pairedDeviceId = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool changed = _policy.ExpectedModelId != modelId
                || !string.Equals(_pairedDeviceId, pairedDeviceId, StringComparison.Ordinal);
            _pairedDeviceId = pairedDeviceId;
            _policy.ExpectedModelId = modelId;
            _matcher.SetPairedModel(modelId);
            if (changed)
            {
                _policy.Reset();
                _matcher.ResetForSelection();
                _last = null;
                _recentMessages.Clear();
                _lastAddress = null;
                _lastCase = null;
                _rememberedCase = null;
                _identity = IdentityConfidence.Unknown;
                _lastDecision = null;
                _lastHeardAt = default;
                _valuesAt = default;
                _ambiguousCount = 0;
                _sources.Clear();
                _sourcesDirty = true;
                _phase = _observing ? BroadcastPhase.Waiting : BroadcastPhase.NotObserving;
                // Layout and noun follow the selection even while not observing; before StartAsync nothing is published.
                if (_started) PublishLocked();
                PublishSourcesLocked();
                return;
            }
            RefreshIdentityLocked(_time.GetUtcNow(), publishIfChanged: true);
        }
    }

    /// <summary>
    /// Gives the transport the selected device's case level from an earlier run (null clears it). Shown as the Case only
    /// while no real reading was heard, the earbuds are being heard and the source is likely the selected pair, with
    /// charging unknown and the original time. Call it after <see cref="SetPairedDevice"/>, which clears it on a change.
    /// Out-of-range levels and times in the future are ignored.
    /// </summary>
    public void SetRememberedCase(int? percent, DateTimeOffset? at)
    {
        lock (_gate)
        {
            if (_disposed) return;
            (int Percent, DateTimeOffset At)? value = percent is >= 0 and <= 100 && at is { } time ? (percent.Value, time) : null;
            if (_rememberedCase == value) return;
            _rememberedCase = value;
            // Show (or drop) it right away if the earbuds are already on screen and no real reading covers it.
            if (_observing && _lastCase is null && _last is not null && LayoutHasCaseLocked()) PublishLocked();
        }
    }

    /// <summary>Feeds Windows' connected state for the selected paired device (a timing clue). Call on each metadata refresh.</summary>
    public void ObserveWindowsConnection(bool isConnected)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = _time.GetUtcNow();
            _matcher.ObserveWindowsConnection(isConnected, now);
            RefreshIdentityLocked(now, publishIfChanged: true);
        }
    }

    /// <summary>Feeds Windows' optional Handsfree battery percent for the selected paired device; null when Windows reports none.</summary>
    public void SetWindowsBattery(int? percent)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = _time.GetUtcNow();
            _matcher.SetWindowsBattery(percent, now);
            RefreshIdentityLocked(now, publishIfChanged: true);
        }
    }

    /// <summary>
    /// Under the lock: re-rate every remembered source. Returns true when any rating changed. When
    /// asked to, publishes the state and the source list for such a change.
    /// </summary>
    private bool RefreshIdentityLocked(DateTimeOffset now, bool publishIfChanged)
    {
        var before = ShownIdentityLocked();
        bool changed = false;
        foreach (var key in _sources.Keys.ToArray())
        {
            var source = _sources[key];
            var rating = _matcher.ConfidenceOf(key.Address, now);
            if (rating == source.Identity) continue;
            _sources[key] = source with { Identity = rating };
            _sourcesDirty = true;
            changed = true;
        }
        if (_lastAddress is ulong last)
        {
            var rating = _matcher.ConfidenceOf(last, now);
            // Quiet is not a rival: with sparse in-ear broadcasts the source may be silent longer than the candidate
            // window, and it keeps its rating until another source is heard (ambiguity still clears it).
            bool quiet = _phase != BroadcastPhase.Ambiguous && now - _lastHeardAt > Options.Attribution.CandidateWindow;
            if (!(quiet && rating == IdentityConfidence.Unknown)) _identity = rating;
        }
        else
        {
            _identity = IdentityConfidence.Unknown;
        }
        bool displayChanged = ShownIdentityLocked() != before;
        if (publishIfChanged)
        {
            if (displayChanged && _observing) PublishLocked();
            PublishSourcesLocked();
        }
        return changed || displayChanged;
    }

    /// <summary>Always true: broadcast ear bits are approximate and display-only.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance property read through the transport by the UI.")]
    public bool EarsAreApproximate => true;

    public string? UnavailableReason { get { lock (_gate) return _unavailableReason; } }

    public string SourceText => IdentityConfidence.IsLikely()
        ? Strings.Ble_Broadcast_SourceLikely
        : Strings.Ble_Broadcast_SourceUnconfirmed;

    public string? ReadOnlyReason => ReadOnlyText;

    public event EventHandler<DeviceState>? StateChanged;

    public event EventHandler? StatusChanged;

    public event EventHandler<string>? Diagnostic;

    /// <summary>Publishes the initial not-observing state. Does NOT start listening.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || _started) return Task.CompletedTask;
            _started = true;
            PublishLocked();
        }
        return Task.CompletedTask;
    }

    public async Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (_disposed) return Result(command, CommandOutcome.TransportUnavailable, Strings.Ble_Broadcast_TransportClosed);
        }
        return command switch
        {
            ConnectCommand => await StartObservingAsync(command, cancellationToken).ConfigureAwait(false),
            DisconnectCommand => await StopObservingAsync(command).ConfigureAwait(false),
            _ => Result(command, CommandOutcome.Unsupported, ReadOnlyText),
        };
    }

    private async Task<CommandResult> StartObservingAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        lock (_gate)
        {
            if (_disposed) return Result(command, CommandOutcome.TransportUnavailable, Strings.Ble_Broadcast_TransportClosed);
            if (_observing) return Result(command, CommandOutcome.Acknowledged, "Already observing.");
            _observing = true;
            generation = ++_generation;
            _policy.Reset();
            _matcher.Reset();
            ClearValuesLocked();
            _phase = BroadcastPhase.Waiting;
            _started = true;
            _tick = _time.CreateTimer(_ => OnTick(generation), null, Options.TickInterval, Options.TickInterval);
            _sourceOwner = generation;
            _sourceHealthy = true;
            _lastSightingAt = _lastWatchdogAt = _time.GetUtcNow();
            PublishLocked();
        }
        RaiseDiagnostic("Observing started by the user.");

        AdvertisementSourceStatus status;
        try
        {
            // A restart left over from an earlier observation must finish before this start touches the source.
            await WaitForRestartAsync(cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation)) return Result(command, CommandOutcome.Acknowledged, "Stopped before observing began.");
            status = await _source.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RollBackStartAsync(generation, null).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            status = new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, string.Format(CultureInfo.CurrentCulture, Strings.Ble_Broadcast_Unavailable_CouldNotStart, ex.GetType().Name));
        }

        if (status.State == AdvertisementSourceState.Running)
        {
            bool changed, abandoned;
            long ownerNow;
            lock (_gate)
            {
                ownerNow = _sourceOwner;
                // A Stop (or Dispose) arrived while the source was starting: the source's own Stop
                // may have run before it had anything to stop, so stop it again now. If a newer
                // Start is already observing, the running source serves that one instead.
                abandoned = !_observing || _disposed;
                changed = !abandoned && _unavailableReason is not null;
                if (!abandoned && _generation == generation) _unavailableReason = null;
            }
            if (abandoned)
            {
                // Nobody is observing now, so whichever generation owns the source has already
                // been retired too: stop it unless a newer start takes ownership meanwhile. This
                // does not rely on the source cancelling in-flight starts (EF-IR review F4).
                await StopSourceIfOwnedAsync(ownerNow).ConfigureAwait(false);
                return Result(command, CommandOutcome.Acknowledged, "Stopped before observing began.");
            }
            if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
            return Result(command, CommandOutcome.Acknowledged, "Observing nearby broadcasts.");
        }

        var reason = status.Reason ?? Strings.Ble_Broadcast_Unavailable_Generic;
        // The start failed for a platform reason (radio off or not ready, often at login), not because the user
        // stopped: observing stays on, shows the reason and retries. Only the user's Stop ends it.
        bool kept, reasonChanged = false;
        lock (_gate)
        {
            kept = _generation == generation && _observing && !_disposed;
            if (kept)
            {
                _sourceHealthy = false;
                _nextRestartAt = _time.GetUtcNow() + Options.RestartRetryInterval;
                reasonChanged = !string.Equals(_unavailableReason, reason, StringComparison.Ordinal);
                _unavailableReason = reason;
            }
        }
        if (kept)
        {
            RaiseDiagnostic($"Listener could not start: {reason} Will retry.");
            if (reasonChanged) StatusChanged?.Invoke(this, EventArgs.Empty);
            return Result(command, CommandOutcome.Acknowledged, $"Waiting for Bluetooth ({reason.TrimEnd('.')}); will keep retrying.");
        }
        // A newer Stop or Start retired this one: nothing to retry.
        await StopSourceIfOwnedAsync(generation).ConfigureAwait(false);
        return Result(command, CommandOutcome.TransportUnavailable, reason);
    }

    private async Task RollBackStartAsync(long generation, string? reason)
    {
        bool rolledBack = false;
        lock (_gate)
        {
            if (_generation == generation && _observing && !_disposed)
            {
                StopLocked();
                if (reason is not null) _unavailableReason = reason;
                PublishLocked();
                rolledBack = true;
            }
        }
        if (rolledBack && reason is not null) StatusChanged?.Invoke(this, EventArgs.Empty);
        await StopSourceIfOwnedAsync(generation).ConfigureAwait(false);
    }

    private async Task<CommandResult> StopObservingAsync(DeviceCommand command)
    {
        long owner;
        bool reasonCleared;
        lock (_gate)
        {
            if (!_observing) return Result(command, CommandOutcome.Acknowledged, "Not observing.");
            owner = _sourceOwner;
            // Only the user's Stop keeps the newest real case level for the next Start: judged now, before StopLocked clears
            // the identity, and stored without a charging flag. (A cancelled start's rollback and every path that means "the
            // values may belong to someone else" never reach here.)
            if (_lastCase is { } newest && ShownIdentityLocked().IsLikely() && LayoutHasCaseLocked())
                _rememberedCase = (newest.Percent, newest.At);
            StopLocked();
            // A "Bluetooth unavailable, retrying" reason ends with the retrying.
            reasonCleared = _unavailableReason is not null;
            _unavailableReason = null;
            PublishLocked(); // the final state of this observation; nothing follows until the next start
        }
        if (reasonCleared) StatusChanged?.Invoke(this, EventArgs.Empty);
        RaiseDiagnostic("Observing stopped by the user.");
        // First stop the source (this cancels a start in progress), so Stop never waits on a radio check.
        await StopSourceIfOwnedAsync(owner).ConfigureAwait(false);
        // Then let a restart that was already running finish; it may have started the source meanwhile.
        bool waited = await WaitForRestartAsync().ConfigureAwait(false);
        bool nobodyObserving;
        lock (_gate) nobodyObserving = !_observing;
        if (waited && nobodyObserving) await SafeStopSourceAsync().ConfigureAwait(false);
        return Result(command, CommandOutcome.Acknowledged, "Stopped observing.");
    }

    /// <summary>Under the lock: stop accepting sightings and timer ticks.</summary>
    private void StopLocked()
    {
        _observing = false;
        _generation++;
        _tick?.Dispose();
        _tick = null;
        _policy.Reset();
        _matcher.Reset();
        ClearValuesLocked();
        _phase = BroadcastPhase.NotObserving;
    }

    private void ClearValuesLocked()
    {
        _last = null;
        _recentMessages.Clear();
        _lastCase = null;
        _lastReasonKey = null;
        _lastAddress = null;
        _identity = IdentityConfidence.Unknown;
        _ambiguousCount = 0;
        _sources.Clear();
        _sourcesDirty = false;
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PruneSourcesLocked(DateTimeOffset now)
    {
        foreach (var key in _sources.Where(pair => now - pair.Value.LastHeardAt > Options.Attribution.CandidateWindow)
                     .Select(pair => pair.Key).ToArray())
        {
            _sources.Remove(key);
            _sourcesDirty = true;
        }
    }

    private void ObserveSourceLocked(AdvertisementSighting sighting, ProximityMessage parsed, DateTimeOffset now)
    {
        PruneSourcesLocked(now);
        var key = (Address: sighting.Address, Model: parsed.ModelId!.Value);
        bool added = !_sources.TryGetValue(key, out var previous);
        // Bound memory even in a crowded area. When full, a new address is simply not listed: evicting
        // an older one would let a flood of made-up addresses push the real source out of the list.
        if (added && _sources.Count >= 32) return;
        _sources[key] = new BroadcastSource(previous?.Id ?? Guid.NewGuid(), previous?.Number ?? ++_nextSourceNumber,
            key.Model, AppleModelCatalog.DisplayNameOf(key.Model), sighting.Rssi,
            parsed.LeftBatteryPercent, parsed.RightBatteryPercent, parsed.CaseBatteryPercent, now,
            parsed.ThisPodInCase, parsed.LidClosed, parsed.LidCounter, _matcher.ConfidenceOf(sighting.Address, now),
            parsed.Layout);
        _sourcesDirty = true;
        if (added) PublishSourcesLocked();
    }

    private void PublishSourcesLocked()
    {
        if (!_sourcesDirty) return;
        _sourcesDirty = false;
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stop the source only if <paramref name="generation"/> still owns it.</summary>
    private async Task StopSourceIfOwnedAsync(long generation)
    {
        lock (_gate)
        {
            // Only a newer Start transfers ownership. A stopped generation keeps it, so its own
            // late Running result can still stop the source it started.
            if (_sourceOwner != generation) return; // a newer start owns the source now
        }
        await SafeStopSourceAsync().ConfigureAwait(false);
    }

    private async Task SafeStopSourceAsync()
    {
        try { await _source.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { RaiseDiagnostic($"Stopping the listener failed ({ex.GetType().Name})."); }
    }

    private void OnReceived(object? sender, AdvertisementSighting sighting)
    {
        try
        {
            if (sighting is null) return;
            var parsed = sighting.CompanyId == ProximityMessage.AppleCompanyId
                ? ProximityMessageParser.Parse(sighting.ManufacturerData is byte[] array ? array : sighting.ManufacturerData.ToArray())
                : null;
            lock (_gate)
            {
                if (!_observing || _disposed) return;
                var now = _time.GetUtcNow();
                _lastSightingAt = now;
                // Cheap gate before any attribution or identity work: a flood cannot drive the quadratic grouping.
                if (parsed is not null && !_ingress.TryAccept(sighting.Address, now))
                {
                    NoteRateLimitedLocked(now);
                    return;
                }
                var decision = _policy.Evaluate(sighting, parsed, userChoseToObserve: true, now);
                bool identityChanged = false;
                if (decision.Verdict != AttributionVerdict.Rejected)
                {
                    _matcher.Observe(sighting.Address, parsed!, now);
                    ObserveSourceLocked(sighting, parsed!, now);
                    if (decision.Verdict == AttributionVerdict.Accepted) _lastAddress = sighting.Address;
                    var before = ShownIdentityLocked();
                    identityChanged = RefreshIdentityLocked(now, publishIfChanged: false);
                    if (decision.Verdict == AttributionVerdict.Ambiguous) _identity = IdentityConfidence.Unknown;
                    decision = decision with { IdentityConfidence = _matcher.ConfidenceOf(sighting.Address, now) };
                    identityChanged |= before != ShownIdentityLocked();
                }
                _lastDecision = decision;
                if (parsed is not null) NoteDecisionLocked(decision, now);
                if (identityChanged) PublishSourcesLocked();
                switch (decision.Verdict)
                {
                    case AttributionVerdict.Rejected:
                        return;
                    case AttributionVerdict.Ambiguous:
                        if (_phase == BroadcastPhase.Ambiguous && _ambiguousCount == decision.CandidateCount) return;
                        _phase = BroadcastPhase.Ambiguous;
                        _ambiguousCount = decision.CandidateCount;
                        // Forget earlier values: once ambiguous, they may have belonged to either source.
                        _last = null;
                        _recentMessages.Clear();
                        _lastCase = null;
                        _rememberedCase = null; // it could attach to the wrong pair once one source is accepted again
                        PublishLocked();
                        return;
                }

                // Accepted. Each bud broadcasts from its own address: fold in what its sibling last said.
                var incoming = parsed!;
                parsed = MergeSiblingLocked(sighting.Address, incoming, now);
                bool duplicate = _phase == BroadcastPhase.Fresh && _last is not null
                    && _last.Raw.AsSpan().SequenceEqual(parsed.Raw)
                    && SameMergedValues(_last, parsed)
                    && now - _valuesAt < Options.DuplicateWindow;
                _lastHeardAt = now;
                // The retained case level belongs to one pair: drop it when this message is clearly another.
                if (_last is not null && (_lastCase is not null || _rememberedCase is not null)
                    && !BroadcastSiblings.AreSiblings(PodReading.From(_last), PodReading.From(parsed), Options.Attribution.BatteryTolerancePercent))
                {
                    _lastCase = null;
                    _rememberedCase = null;
                }
                RememberCaseLocked(parsed, now);
                if (duplicate)
                {
                    _valuesAt = now;
                    // A steady stream of identical packets is still live: republish now and then so the shown age does not run on.
                    // Ear placement is only trusted for a few seconds, so a heard-again placement is republished well before that lapses.
                    bool earsDue = _last!.HasEarBits && now - _publishedAt >= Options.EarRefreshInterval;
                    if (identityChanged || earsDue || now - _publishedAt >= Options.AgeRefreshInterval) PublishLocked();
                    PublishSourcesLocked();
                    EarSightingObserved?.Invoke(this, BuildState(now));
                    return;
                }

                _last = parsed;
                _valuesAt = now;
                _ambiguousCount = 0;
                _phase = BroadcastPhase.Fresh;
                PublishLocked();
            }
        }
        catch (Exception ex)
        {
            // Never let a bad advertisement or a subscriber fault escape into the platform callback.
            RaiseDiagnostic($"Broadcast ignored ({ex.GetType().Name}).");
        }
    }

    /// <summary>Apple broadcasts dropped by the ingress token buckets since this transport was created.</summary>
    internal long RateLimitedSightings
    {
        get { lock (_gate) return _rateLimitedSightings; }
    }

    private void NoteRateLimitedLocked(DateTimeOffset now)
    {
        _rateLimitedSightings++;
        if (now - _lastRateLimitLoggedAt < Options.DiagnosticInterval && _lastRateLimitLoggedAt != default) return;
        _lastRateLimitLoggedAt = now;
        long dropped = _rateLimitedSightings - _rateLimitReportedAt;
        _rateLimitReportedAt = _rateLimitedSightings;
        RaiseDiagnostic($"Broadcast rate limit: {dropped} broadcast(s) ignored (too many per second).");
    }

    /// <summary>Global plus per-address token buckets. Dropping only counts; it never changes any observed state. Under the transport lock.</summary>
    private sealed class IngressLimiter(BroadcastObservationOptions options)
    {
        private const int PruneAbove = 512;
        private readonly Dictionary<ulong, (double Tokens, DateTimeOffset At)> _perAddress = new();
        private double _globalTokens = options.IngressGlobalBurst;
        private DateTimeOffset _globalAt;
        private bool _started;

        public bool TryAccept(ulong address, DateTimeOffset now)
        {
            if (!_started) { _started = true; _globalAt = now; }
            double global = Refill(_globalTokens, _globalAt, now, options.IngressGlobalRatePerSecond, options.IngressGlobalBurst);
            double local = options.IngressPerAddressBurst;
            if (_perAddress.TryGetValue(address, out var entry))
                local = Refill(entry.Tokens, entry.At, now, options.IngressPerAddressRatePerSecond, options.IngressPerAddressBurst);
            else if (_perAddress.Count >= PruneAbove) Prune(now);

            // Consume from both only when both allow it, so a noisy address cannot drain the global budget by being refused.
            bool accept = local >= 1 && global >= 1;
            if (accept) { local -= 1; global -= 1; }
            _globalTokens = global;
            _globalAt = now > _globalAt ? now : _globalAt;
            _perAddress[address] = (local, now > (entry.At) ? now : entry.At);
            return accept;
        }

        private static double Refill(double tokens, DateTimeOffset at, DateTimeOffset now, double rate, int burst)
        {
            var elapsed = (now - at).TotalSeconds;
            if (elapsed <= 0) return tokens;
            return Math.Min(burst, tokens + elapsed * rate);
        }

        /// <summary>Forgets addresses idle long enough to have a full bucket again, so random addresses cannot grow the table without bound.</summary>
        private void Prune(DateTimeOffset now)
        {
            double fullAfter = options.IngressPerAddressBurst / options.IngressPerAddressRatePerSecond;
            foreach (var key in _perAddress.Where(pair => (now - pair.Value.At).TotalSeconds >= fullAfter).Select(pair => pair.Key).ToArray())
                _perAddress.Remove(key);
        }
    }

    /// <summary>
    /// One compact diagnostic line when the verdict or reason for a proximity broadcast changes (never per
    /// broadcast, at most one per <see cref="BroadcastObservationOptions.DiagnosticInterval"/>). Model id and
    /// reason only: no address and no payload.
    /// </summary>
    private void NoteDecisionLocked(AttributionDecision decision, DateTimeOffset now)
    {
        var key = $"{decision.Verdict}|{decision.ModelId}|{decision.ReasonKey ?? decision.Reason}";
        if (key == _lastReasonKey || now - _lastReasonLoggedAt < Options.DiagnosticInterval) return;
        _lastReasonKey = key;
        _lastReasonLoggedAt = now;
        var model = decision.ModelId is ushort id ? $" (model 0x{id:X4})" : "";
        RaiseDiagnostic($"Broadcast {decision.Verdict.ToString().ToLowerInvariant()}{model}: {decision.Reason}");
    }

    /// <summary>
    /// The two buds of one pair are heard as two addresses and, split (one in an ear, the other in the case),
    /// each message reports only part of the picture: the bud in the ear often has the other bud's level
    /// unknown. Fills a left/right battery a message did not report from the newest recent message of a
    /// sibling address (see <see cref="BroadcastSiblings"/>). The case level is NOT merged: it keeps its own
    /// time through <c>_lastCase</c>, so it is never shown as fresher than it is. The ear and in-case bits are never mixed: they
    /// come from the bud that is out of the case when one is (its message names its own side), else from
    /// the newest message, so the shown ear state is one coherent reading.
    /// </summary>
    private ProximityMessage MergeSiblingLocked(ulong address, ProximityMessage message, DateTimeOffset now)
    {
        _recentMessages[address] = (message, now);
        foreach (var key in _recentMessages.Keys.ToArray())
        {
            if (now - _recentMessages[key].At > Options.Attribution.CandidateWindow) _recentMessages.Remove(key);
        }
        var reading = PodReading.From(message);
        ProximityMessage? sibling = null;
        DateTimeOffset siblingAt = default;
        foreach (var (otherAddress, other) in _recentMessages)
        {
            if (otherAddress == address || other.Message.ModelId != message.ModelId) continue;
            if (!BroadcastSiblings.AreSiblings(reading, PodReading.From(other.Message), Options.Attribution.BatteryTolerancePercent)) continue;
            if (sibling is null || other.At > siblingAt) (sibling, siblingAt) = (other.Message, other.At);
        }
        if (sibling is null || message.Layout != BatteryLayout.EarbudsWithCase) return message;

        // Which message supplies the status-derived fields: a bud known to be out of the case speaks for itself.
        var basis = message.ThisPodInCase != false && sibling.ThisPodInCase == false ? sibling : message;
        var donor = ReferenceEquals(basis, message) ? sibling : message;
        return basis with
        {
            LeftBatteryPercent = basis.LeftBatteryPercent ?? donor.LeftBatteryPercent,
            RightBatteryPercent = basis.RightBatteryPercent ?? donor.RightBatteryPercent,
            LeftCharging = basis.LeftBatteryPercent is null ? donor.LeftCharging : basis.LeftCharging,
            RightCharging = basis.RightBatteryPercent is null ? donor.RightCharging : basis.RightCharging,
        };
    }

    private static bool SameMergedValues(ProximityMessage a, ProximityMessage b)
        => a.LeftBatteryPercent == b.LeftBatteryPercent && a.RightBatteryPercent == b.RightBatteryPercent
           && a.CaseBatteryPercent == b.CaseBatteryPercent && a.LeftCharging == b.LeftCharging
           && a.RightCharging == b.RightCharging;

    private void RememberCaseLocked(ProximityMessage message, DateTimeOffset now)
    {
        if (message.CaseBatteryPercent is int percent)
        {
            _lastCase = (percent, message.CaseCharging, now);
            _rememberedCase = null; // a real reading replaces it (the host stores the new level)
        }
    }

    private void OnTick(long generation)
    {
        try
        {
            lock (_gate)
            {
                if (!_observing || _disposed || _generation != generation) return;
                var now = _time.GetUtcNow();
                PruneSourcesLocked(now);
                RefreshIdentityLocked(now, publishIfChanged: true); // also publishes pending source changes
                PublishSourcesLocked();
                MaybeRestartSourceLocked(now, generation);
                switch (_phase)
                {
                    case BroadcastPhase.Ambiguous when _policy.CandidateCount(now) == 0:
                        // Every candidate went quiet (for example a rotated address): stop claiming ambiguity.
                        _ambiguousCount = 0;
                        _phase = _last is null ? BroadcastPhase.Waiting : PhaseByAge(now);
                        PublishLocked();
                        break;
                    case BroadcastPhase.Fresh or BroadcastPhase.Stale or BroadcastPhase.Lost:
                        var next = PhaseByAge(now);
                        if (next != _phase)
                        {
                            _phase = next;
                            PublishLocked();
                        }
                        else if (_phase != BroadcastPhase.Fresh && _last is not null && now - _publishedAt >= Options.AgeRefreshInterval)
                        {
                            // The last reading stays on screen (with its age) until observing stops or the device changes.
                            PublishLocked();
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            RaiseDiagnostic($"Freshness check failed ({ex.GetType().Name}).");
        }
    }

    private BroadcastPhase PhaseByAge(DateTimeOffset now)
    {
        var age = now - _lastHeardAt;
        if (age > Options.LostAfter) return BroadcastPhase.Lost;
        if (age > Options.StaleAfter) return BroadcastPhase.Stale;
        return BroadcastPhase.Fresh;
    }

    /// <summary>
    /// The listener stopped or became unavailable (radio switched off, sleep/resume, adapter error). The user
    /// has not stopped observing, so it stays on: the last reading is kept (aging as usual) and the listener
    /// is restarted by the tick, every <see cref="BroadcastObservationOptions.RestartRetryInterval"/> until it works.
    /// Only the user's Stop ends observing.
    /// </summary>
    private void OnSourceStatusChanged(object? sender, AdvertisementSourceStatus status)
    {
        if (status is null || status.State == AdvertisementSourceState.Running) return;
        bool changed;
        string reason;
        lock (_gate)
        {
            // Stops during our own restart are expected; the restart reports its own result.
            if (!_observing || _disposed || _restartGeneration == _generation) return;
            _sourceHealthy = false;
            _nextRestartAt = _time.GetUtcNow();
            reason = status.State == AdvertisementSourceState.Unavailable
                ? status.Reason ?? Strings.Ble_Broadcast_Unavailable_Became
                : Strings.Ble_Broadcast_Unavailable_StoppedUnexpectedly;
            changed = !string.Equals(_unavailableReason, reason, StringComparison.Ordinal);
            _unavailableReason = reason;
        }
        RaiseDiagnostic($"Listener lost: {reason} Will retry.");
        if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Under the lock: start one restart of the listener when it is down, or silent for too long.</summary>
    private void MaybeRestartSourceLocked(DateTimeOffset now, long generation)
    {
        if (_restartTask is { IsCompleted: false }) return;
        string? why = null;
        if (!_sourceHealthy)
        {
            if (now >= _nextRestartAt) why = "after it stopped";
        }
        else if (now - _lastSightingAt >= Options.WatchdogAfter && now - _lastWatchdogAt >= Options.WatchdogAfter)
        {
            why = $"no Apple broadcast heard for {(int)(now - _lastSightingAt).TotalMinutes} min";
        }
        if (why is null) return;
        _restartGeneration = generation;
        _lastWatchdogAt = now;
        _restartTask = Task.Run(() => RestartSourceAsync(generation, why));
    }

    /// <summary>True while <paramref name="generation"/> is still the current observation (not stopped, replaced or disposed).</summary>
    private bool IsCurrent(long generation)
    {
        lock (_gate) return _observing && !_disposed && _generation == generation;
    }

    /// <summary>
    /// Restarts the listener for <paramref name="generation"/>. Before each call into the source it checks that
    /// the generation is still current, so a restart retired by Stop, a new Start or Dispose never touches the
    /// source. Never faults (it is awaited by Stop, Start and Dispose, and otherwise unobserved).
    /// </summary>
    private async Task RestartSourceAsync(long generation, string why)
    {
        try
        {
            if (!IsCurrent(generation)) { EndRestart(generation); return; }
            RaiseDiagnostic($"Restarting the Bluetooth listener ({why}).");
            AdvertisementSourceStatus status;
            try
            {
                await _source.StopAsync().ConfigureAwait(false);
                if (!IsCurrent(generation)) { EndRestart(generation); return; }
                status = await _source.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                status = new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, string.Format(CultureInfo.CurrentCulture, Strings.Ble_Broadcast_Unavailable_CouldNotRestart, ex.GetType().Name));
            }

            bool current, changed = false, running = status.State == AdvertisementSourceState.Running;
            lock (_gate)
            {
                if (_restartGeneration == generation) _restartGeneration = 0;
                current = _observing && !_disposed && _generation == generation;
                if (current)
                {
                    var now = _time.GetUtcNow();
                    _lastWatchdogAt = now;
                    if (running)
                    {
                        _sourceHealthy = true;
                        changed = _unavailableReason is not null;
                        _unavailableReason = null;
                    }
                    else
                    {
                        var reason = status.Reason ?? Strings.Ble_Broadcast_Unavailable_Generic;
                        _sourceHealthy = false;
                        _nextRestartAt = now + Options.RestartRetryInterval;
                        changed = !string.Equals(_unavailableReason, reason, StringComparison.Ordinal);
                        _unavailableReason = reason;
                    }
                }
            }

            // Retired while starting: whoever retired it (Stop, Dispose) waits for this task and stops the source
            // again if nobody is observing; a newer Start waits for it and then starts the source itself.
            if (!current) return;
            RaiseDiagnostic(running ? "Bluetooth listener restarted." : $"Bluetooth listener restart failed: {status.Reason ?? "unavailable"}");
            if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            EndRestart(generation);
            RaiseDiagnostic($"Listener restart failed ({ex.GetType().Name}).");
        }
    }

    private void EndRestart(long generation)
    {
        lock (_gate)
        {
            if (_restartGeneration == generation) _restartGeneration = 0;
        }
    }

    /// <summary>Waits for a listener restart still running, if any, without holding the lock. True when it had to wait.</summary>
    private async Task<bool> WaitForRestartAsync(CancellationToken cancellationToken = default)
    {
        Task? restart;
        lock (_gate) restart = _restartTask;
        if (restart is not { IsCompleted: false }) return false;
        try { await restart.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* the restart itself never cancels */ }
        catch (Exception) { /* RestartSourceAsync reports its own faults */ }
        return true;
    }

    /// <summary>Build and raise the state for the current phase. Caller holds the lock.</summary>
    private void PublishLocked()
    {
        var now = _time.GetUtcNow();
        var state = BuildState(now);
        _publishedAt = now;
        _current = state;
        StateChanged?.Invoke(this, state);
    }

    /// <summary>True when the shown message (else the selected model) has a case battery.</summary>
    private bool LayoutHasCaseLocked()
        => (_last?.Layout
            ?? (_policy.ExpectedModelId is ushort id && AppleModelCatalog.Find(id) is { } info ? info.Layout : BatteryLayout.EarbudsWithCase))
            == BatteryLayout.EarbudsWithCase;

    private DeviceState BuildState(DateTimeOffset now)
    {
        var caps = DeviceCapabilities.Battery | DeviceCapabilities.BroadcastObservation | DeviceCapabilities.ConnectControl;
        var link = LinkState.Unknown;
        var battery = BatteryState.Unknown;
        var ears = EarState.Unknown;
        string display, model = Strings.Ble_Broadcast_UnknownModel;

        bool showValues = _last is not null && _phase is BroadcastPhase.Fresh or BroadcastPhase.Stale or BroadcastPhase.Lost;
        // Layout and noun come from the shown message, else the selected model, so a waiting Studio Pro never looks like earbuds.
        // With no model known the noun stays "AirPods" (the original text); an unlisted id gives "headphones".
        ushort? knownModel = showValues && _last is { ModelId: ushort shownId } ? shownId : _policy.ExpectedModelId;
        string noun = knownModel is null ? "AirPods" : AppleModelCatalog.FamilyNounOf(knownModel);
        var layout = showValues && _last is { } shown
            ? shown.Layout
            : _policy.ExpectedModelId is ushort selected && AppleModelCatalog.Find(selected) is { } info
                ? info.Layout
                : BatteryLayout.EarbudsWithCase;
        switch (_phase)
        {
            case BroadcastPhase.NotObserving:
                display = string.Format(CultureInfo.CurrentCulture, Strings.Ble_Broadcast_Display_NotObserving, noun);
                break;
            case BroadcastPhase.Waiting:
                display = string.Format(CultureInfo.CurrentCulture, Strings.Ble_Broadcast_Display_Waiting, noun);
                break;
            case BroadcastPhase.Ambiguous:
                display = string.Format(CultureInfo.CurrentCulture, Strings.Ble_Broadcast_Display_Ambiguous, _ambiguousCount);
                break;
            default:
                display = ShownIdentityLocked().IsLikely() ? IdentityConfidenceText.LikelyFor(noun) : string.Format(CultureInfo.CurrentCulture, Strings.Ble_Broadcast_Display_Unconfirmed, noun);
                break;
        }

        if (showValues && _last is { } m)
        {
            var at = _valuesAt;
            model = m.ModelId is ushort id ? AppleModelCatalog.DisplayNameOf(id) : model;
            battery = new BatteryState(
                BatteryComponent.From(m.LeftBatteryPercent, m.LeftCharging, at),
                BatteryComponent.From(m.RightBatteryPercent, m.RightCharging, at),
                CaseComponentLocked(now, layout));
            var nearby = _phase == BroadcastPhase.Lost
                ? Observation<bool>.At(false, now)
                : Observation<bool>.At(true, _lastHeardAt);
            link = new LinkState(nearby, Observation<bool>.Unknown, Observation<bool>.Unknown, Observation<bool>.Unknown);

            // Approximate, display-only ear placement; only while fresh.
            if (_phase == BroadcastPhase.Fresh && m.HasEarBits)
            {
                ears = new EarState(Placement(m, m.PrimaryInEar, at), Placement(m, m.SecondaryInEar, at))
                {
                    // The broadcast also says which bud is which side (the same flip that orders the L and R batteries).
                    Left = Placement(m, m.LeftInEar, at),
                    Right = Placement(m, m.RightInEar, at),
                };
                caps |= DeviceCapabilities.EarDetection;
            }
        }

        return new DeviceState(
            DeviceId, display, model, DataOrigin.Broadcast, ++_sequence, now, caps, link, battery,
            ListeningState.Unknown, Observation<bool>.Unknown, Observation<int>.Unknown)
        {
            Ears = ears,
            Layout = layout,
            DeviceNoun = noun,
            ModelId = knownModel,
        };
    }

    /// <summary>
    /// A real case reading always wins. Otherwise a level remembered from an earlier run, only while the shown source is
    /// likely the selected pair, with charging unknown and its original time so the shown age is truthful.
    /// </summary>
    private BatteryComponent CaseComponentLocked(DateTimeOffset now, BatteryLayout layout)
    {
        // Headsets and case-less models have no case battery; this also hides a phantom level saved by an earlier version.
        if (layout != BatteryLayout.EarbudsWithCase) return BatteryComponent.Unknown;
        if (_lastCase is { } kept) return BatteryComponent.From(kept.Percent, kept.Charging, kept.At);
        if (_rememberedCase is { } remembered && remembered.At <= now && ShownIdentityLocked().IsLikely())
            return BatteryComponent.From(remembered.Percent, null, remembered.At);
        return BatteryComponent.Unknown;
    }

    private static Observation<PodPlacement> Placement(ProximityMessage m, bool? inEar, DateTimeOffset at)
    {
        if (m.BothPodsInCase == true) return Observation<PodPlacement>.At(PodPlacement.InCase, at);
        if (inEar == true) return Observation<PodPlacement>.At(PodPlacement.InEar, at);
        // One bud in the case and this one not in an ear: which bud is in the case is not decodable.
        if (m.OnePodInCase == true || inEar is null) return Observation<PodPlacement>.Unknown;
        return Observation<PodPlacement>.At(PodPlacement.OutOfEar, at);
    }

    private static CommandResult Result(DeviceCommand command, CommandOutcome outcome, string detail)
        => new(command.CommandId, outcome, DataOrigin.Broadcast, detail);

    private void RaiseDiagnostic(string message)
    {
        try { Diagnostic?.Invoke(this, message); }
        catch (Exception) { /* diagnostics must never break observation */ }
    }

    /// <summary>Stops listening, publishes nothing further, and disposes the source. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _observing = false;
            _generation++;
            // _sourceOwner is kept: a start still in flight may report Running after this, and
            // must then stop the (disposed) source rather than trust it to refuse.
            _tick?.Dispose();
            _tick = null;
        }
        _source.Received -= OnReceived;
        _source.StatusChanged -= OnSourceStatusChanged;
        // Same order as Stop: stop the source first (this ends a start in progress, so a restart held in a slow
        // radio check cannot stall quitting), then let a running restart finish and stop the source again.
        await SafeStopSourceAsync().ConfigureAwait(false);
        if (await WaitForRestartAsync().ConfigureAwait(false)) await SafeStopSourceAsync().ConfigureAwait(false);
        try { _source.Dispose(); }
        catch (Exception ex) { RaiseDiagnostic($"Disposing the listener failed ({ex.GetType().Name})."); }
    }
}
