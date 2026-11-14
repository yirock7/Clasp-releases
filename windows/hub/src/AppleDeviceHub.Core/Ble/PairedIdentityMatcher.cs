namespace AppleDeviceHub.Core.Ble;

/// <summary>
/// How likely it is that a nearby broadcast source belongs to the user's paired AirPods. This is
/// never a proof: only the AirPods' identity key could prove it, and that needs the driver.
/// </summary>
public enum IdentityConfidence
{
    /// <summary>Nothing can be said: wrong or no model, or several candidates at once.</summary>
    Unknown = 0,

    /// <summary>Same model as the paired device and the only such source heard recently.</summary>
    Probable = 1,

    /// <summary>
    /// Probable, plus a Windows battery reading that agrees with every heard bud right now. Forgeable (anyone can
    /// broadcast a matching level), so it is never remembered and never enough for the low-battery alert.
    /// </summary>
    LikelyByBattery = 2,

    /// <summary>Probable, plus a latched clue: connection timing, an already-latched sibling, or address-rotation takeover.</summary>
    LikelyYours = 3,
}

/// <summary>Display wording for <see cref="IdentityConfidence"/>. The word "Confirmed" alone is never used.</summary>
public static class IdentityConfidenceText
{
    public const string Likely = "Your AirPods (likely)";

    public const string Unconfirmed = "identity unconfirmed";

    /// <summary>The likely wording for a device family noun ("AirPods", "Beats" or "headphones"); "AirPods" gives <see cref="Likely"/>.</summary>
    public static string LikelyFor(string noun) => noun == "AirPods" ? Likely : $"Your {noun} (likely)";

    /// <summary>True when the wording may say these are probably the user's AirPods.</summary>
    public static bool IsLikely(this IdentityConfidence confidence) => confidence >= IdentityConfidence.Probable;

    /// <summary>True when an independent connection-timing or (current) battery clue supports the match.</summary>
    public static bool HasIndependentClue(this IdentityConfidence confidence)
        => confidence >= IdentityConfidence.LikelyByBattery;

    /// <summary>True only for a clue that cannot be forged by replaying a battery level (timing, latched sibling, rotation, or the driver).</summary>
    public static bool IsLatched(this IdentityConfidence confidence)
        => confidence == IdentityConfidence.LikelyYours;

    /// <summary>"Your AirPods (likely)" at Probable or better, otherwise "identity unconfirmed".</summary>
    public static string For(IdentityConfidence confidence) => confidence.IsLikely() ? Likely : Unconfirmed;
}

public sealed record PairedIdentityOptions
{
    /// <summary>Matching sources heard within this window count as candidates; two means Unknown.</summary>
    public TimeSpan CandidateWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A broadcast state change and a Windows connect/disconnect this close together line up.</summary>
    public TimeSpan TimingWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Battery readings this close agree (broadcast battery is reported in 10 % steps).</summary>
    public int BatteryTolerancePercent { get; init; } = 10;

    /// <summary>A Windows battery reading older than this is ignored.</summary>
    public TimeSpan WindowsBatteryMaxAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>An old address must have been silent at least this long before a new one may inherit its match.</summary>
    public TimeSpan RotationQuietAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Most sources tracked at once. A new address past this is not stored; the matcher then rates everything
    /// Unknown until the crowd (or flood) has gone quiet, so an attacker cannot evict the real source.
    /// </summary>
    public int MaxTracked { get; init; } = 64;

    /// <summary>How long a quiet source is remembered for address rotation; after this its match is forgotten.</summary>
    public TimeSpan RetainFor { get; init; } = TimeSpan.FromSeconds(60);

    internal void Validate()
    {
        if (CandidateWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CandidateWindow));
        if (TimingWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TimingWindow));
        if (BatteryTolerancePercent < 0) throw new ArgumentOutOfRangeException(nameof(BatteryTolerancePercent));
        if (WindowsBatteryMaxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(WindowsBatteryMaxAge));
        if (RotationQuietAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RotationQuietAfter));
        if (MaxTracked < 2) throw new ArgumentOutOfRangeException(nameof(MaxTracked));
        if (RetainFor < CandidateWindow) throw new ArgumentOutOfRangeException(nameof(RetainFor), "RetainFor must not be shorter than CandidateWindow.");
    }
}

/// <summary>
/// Rates each nearby broadcast source as <see cref="IdentityConfidence.Unknown"/>,
/// <see cref="IdentityConfidence.Probable"/> or <see cref="IdentityConfidence.LikelyYours"/> for the
/// selected paired device, from independent clues:
/// <list type="bullet">
/// <item>Model match (required): the broadcast model id equals the paired device's model id.</item>
/// <item>Only candidate: exactly one matching source heard within the candidate window.</item>
/// <item>Connection timing: a Windows connect/disconnect lines up with a state change of the source
/// (it appeared, or a pod/lid state changed).</item>
/// <item>Battery agreement: an optional, fresh Windows battery percent is within one step of every heard bud's
/// current value. This gives only <see cref="IdentityConfidence.LikelyByBattery"/>, while it holds: it never latches.</item>
/// <item>Address tracking: once latched (<see cref="IdentityConfidence.LikelyYours"/>), the match follows the
/// source to a new random address with the same model and a similar battery, after the old one went
/// quiet, for a bounded time.</item>
/// </list>
/// Two matching sources heard at once are both Unknown: the matcher never picks one. (The two buds of one
/// pair, heard as two addresses, are siblings and count once: see <see cref="BroadcastSiblings"/>.) A latched
/// match is remembered through such an episode but is not reported while it lasts. Not thread-safe:
/// callers serialise access. Time is always passed in.
/// </summary>
public sealed class PairedIdentityMatcher
{
    private const int MaxStateChanges = 8;

    private readonly Dictionary<ulong, Tracked> _tracked = new();
    private readonly List<DateTimeOffset> _transitions = new();
    private ushort? _pairedModel;
    private bool? _windowsConnected;
    private int? _windowsBattery;
    private DateTimeOffset _windowsBatteryAt;
    private DateTimeOffset _overflowAt;
    private bool _overflowed;

    // Heard set and its group count, computed once per (tracked-set generation, now) instead of on every ConfidenceOf call.
    private long _generation;
    private long _heardGeneration = -1;
    private DateTimeOffset _heardAt;
    private List<Tracked> _heard = new();
    private int _heardGroups;

    /// <summary>How many times the heard set was grouped (test hook: must stay O(observations), not O(observations x sources)).</summary>
    internal int GroupingComputations { get; private set; }

    public PairedIdentityMatcher(PairedIdentityOptions? options = null)
    {
        Options = options ?? new PairedIdentityOptions();
        Options.Validate();
    }

    public PairedIdentityOptions Options { get; }

    /// <summary>The paired device's Apple model id, or null when none is selected or it is not known.</summary>
    public ushort? PairedModelId => _pairedModel;

    /// <summary>Sets the selected paired device's model id. A change forgets every tracked source.</summary>
    public void SetPairedModel(ushort? modelId)
    {
        if (_pairedModel == modelId) return;
        _pairedModel = modelId;
        Reset();
        _transitions.Clear();
        _windowsConnected = null;
        _windowsBattery = null;
    }

    /// <summary>Forgets the previous paired identity's sources and correlation clues, even for a same-model selection.</summary>
    internal void ResetForSelection()
    {
        Reset();
        _transitions.Clear();
        _windowsConnected = null;
        _windowsBattery = null;
        _windowsBatteryAt = default;
    }

    /// <summary>
    /// Records Windows' connected state for the paired device. The first call is only a baseline;
    /// each later change is a connect or disconnect transition used as a timing clue.
    /// </summary>
    public void ObserveWindowsConnection(bool isConnected, DateTimeOffset now)
    {
        if (_windowsConnected is bool previous && previous != isConnected)
        {
            _transitions.Add(now);
            if (_transitions.Count > MaxStateChanges) _transitions.RemoveAt(0);
        }
        _windowsConnected = isConnected;
    }

    /// <summary>
    /// Sets Windows' battery percent for the paired device (the Handsfree battery property).
    /// Null means Windows reports none, which is normal and simply removes this clue.
    /// </summary>
    public void SetWindowsBattery(int? percent, DateTimeOffset now)
    {
        _windowsBattery = percent is int value && value is >= 0 and <= 100 ? value : null;
        _windowsBatteryAt = now;
    }

    /// <summary>Forgets every tracked source (for example when observing stops). Windows facts are kept.</summary>
    public void Reset()
    {
        _tracked.Clear();
        _overflowed = false;
        _generation++;
    }

    /// <summary>Number of sources currently tracked (never more than <see cref="PairedIdentityOptions.MaxTracked"/>).</summary>
    internal int TrackedCount => _tracked.Count;

    private bool OverflowActive(DateTimeOffset now)
        => _overflowed && (now - _overflowAt <= Options.CandidateWindow || _tracked.Count >= Options.MaxTracked);

    /// <summary>Feeds one heard broadcast and returns that source's confidence.</summary>
    public IdentityConfidence Observe(ulong address, ProximityMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        Prune(now);
        _generation++; // any Observe may change a tracked source's reading or the tracked set
        if (_pairedModel is not ushort paired || message.ModelId != paired) return IdentityConfidence.Unknown;

        var signature = new Signature(message.ThisPodInCase, message.OnePodInCase, message.BothPodsInCase, message.LidClosed, message.LidCounter);
        if (_tracked.TryGetValue(address, out var existing))
        {
            if (existing.Signature != signature) existing.AddStateChange(now);
            existing.Update(message, signature, now);
        }
        else
        {
            if (_tracked.Count >= Options.MaxTracked)
            {
                _overflowed = true;
                _overflowAt = now;
                return IdentityConfidence.Unknown;
            }
            var created = new Tracked(address);
            created.Update(message, signature, now);
            created.AddStateChange(now); // appearing counts as a state change
            if (TakeOverFromQuietPredecessor(created, now)) created.Latched = true;
            _tracked[address] = created;
        }
        return ConfidenceOf(address, now);
    }

    /// <summary>The current confidence of a source; Unknown for one not heard within the candidate window.</summary>
    public IdentityConfidence ConfidenceOf(ulong address, DateTimeOffset now)
    {
        Prune(now);
        if (OverflowActive(now)) return IdentityConfidence.Unknown; // cannot tell sources apart in a crowd or flood
        if (!_tracked.TryGetValue(address, out var source) || now - source.LastHeard > Options.CandidateWindow)
            return IdentityConfidence.Unknown;

        // The two buds of one pair broadcast from two addresses; they count as one candidate.
        if (_heardGeneration != _generation || _heardAt != now)
        {
            _heard = _tracked.Values.Where(other => now - other.LastHeard <= Options.CandidateWindow).ToList();
            _heardGroups = BroadcastSiblings.CountGroups(_heard.Select(h => h.Latest), Options.BatteryTolerancePercent);
            _heardGeneration = _generation;
            _heardAt = now;
            GroupingComputations++;
        }
        var heard = _heard;
        if (_heardGroups != 1)
            return IdentityConfidence.Unknown; // never pick between two

        // One rating for the pair: any bud's clue (or latch) counts for both, so the shown rating cannot flicker between buds.
        // Battery agreement never latches: a forged level could be cycled until it matched, then be trusted forever.
        if (!source.Latched && heard.Any(h => h.Latched || TimingAgrees(h)))
            source.Latched = true;
        if (source.Latched) return IdentityConfidence.LikelyYours;
        return GroupBatteryAgrees(heard, now) ? IdentityConfidence.LikelyByBattery : IdentityConfidence.Probable;
    }

    private bool TimingAgrees(Tracked source)
    {
        foreach (var change in source.StateChanges)
        {
            foreach (var transition in _transitions)
            {
                var gap = change - transition;
                if (gap < TimeSpan.Zero) gap = -gap;
                if (gap <= Options.TimingWindow) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True while the fresh Windows battery agrees with the CURRENT reading of every heard source that reports a level
    /// (and at least one does), so a second address cannot carry the clue for a bud that disagrees.
    /// </summary>
    private bool GroupBatteryAgrees(List<Tracked> heard, DateTimeOffset now)
    {
        if (_windowsBattery is not int windows || now - _windowsBatteryAt > Options.WindowsBatteryMaxAge) return false;
        bool anyReported = false;
        foreach (var source in heard)
        {
            var left = source.Latest.Left;
            var right = source.Latest.Right;
            if (left is null && right is null) continue;
            anyReported = true;
            bool agrees = (left is int l && Math.Abs(l - windows) <= Options.BatteryTolerancePercent)
                || (right is int r && Math.Abs(r - windows) <= Options.BatteryTolerancePercent);
            if (!agrees) return false;
        }
        return anyReported;
    }

    /// <summary>
    /// Address rotation: if exactly one latched source has gone quiet (but is still remembered) and
    /// this new source's batteries agree with it, the new source inherits the match and the old
    /// record is retired so it does not count as a second candidate.
    /// </summary>
    private bool TakeOverFromQuietPredecessor(Tracked fresh, DateTimeOffset now)
    {
        Tracked? only = null;
        foreach (var old in _tracked.Values)
        {
            if (!old.Latched || now - old.LastHeard < Options.RotationQuietAfter || now - old.LastHeard > Options.RetainFor) continue;
            if (!BatteriesCompatible(old, fresh)) continue;
            if (only is not null) return false; // more than one possible predecessor: do not guess
            only = old;
        }
        if (only is null) return false;
        _tracked.Remove(only.Address);
        _generation++;
        return true;
    }

    private bool BatteriesCompatible(Tracked a, Tracked b)
    {
        int compared = 0;
        foreach (var (x, y) in new[] { (a.Left, b.Left), (a.Right, b.Right), (a.Case, b.Case) })
        {
            if (x is not int px || y is not int py) continue;
            if (Math.Abs(px - py) > Options.BatteryTolerancePercent) return false;
            compared++;
        }
        return compared > 0;
    }

    private void Prune(DateTimeOffset now)
    {
        List<ulong>? expired = null;
        foreach (var (address, source) in _tracked)
        {
            if (now - source.LastHeard > Options.RetainFor) (expired ??= new()).Add(address);
        }
        if (expired is not null)
        {
            foreach (var address in expired) _tracked.Remove(address);
            _generation++;
        }

        _transitions.RemoveAll(t => now - t > Options.RetainFor);
        foreach (var source in _tracked.Values) source.PruneStateChanges(now, Options.RetainFor);
    }

    private readonly record struct Signature(bool? ThisPodInCase, bool? OnePodInCase, bool? BothPodsInCase, bool? LidClosed, byte? LidCounter);

    private sealed class Tracked(ulong address)
    {
        public ulong Address { get; } = address;
        public DateTimeOffset LastHeard { get; private set; }
        public int? Left { get; private set; }
        public int? Right { get; private set; }
        public int? Case { get; private set; }
        public Signature Signature { get; private set; }
        public PodReading Latest { get; private set; }
        public bool Latched { get; set; }
        public List<DateTimeOffset> StateChanges { get; } = new();

        public void Update(ProximityMessage message, Signature signature, DateTimeOffset now)
        {
            LastHeard = now;
            Signature = signature;
            Latest = PodReading.From(message);
            Left = message.LeftBatteryPercent ?? Left;
            Right = message.RightBatteryPercent ?? Right;
            Case = message.CaseBatteryPercent ?? Case;
        }

        public void AddStateChange(DateTimeOffset now)
        {
            StateChanges.Add(now);
            if (StateChanges.Count > MaxStateChanges) StateChanges.RemoveAt(0);
        }

        public void PruneStateChanges(DateTimeOffset now, TimeSpan retain) => StateChanges.RemoveAll(t => now - t > retain);
    }
}
