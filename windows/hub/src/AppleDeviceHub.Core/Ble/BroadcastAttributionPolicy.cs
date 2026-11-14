namespace AppleDeviceHub.Core.Ble;

public enum AttributionVerdict
{
    /// <summary>May be shown as a nearby source; identity is rated separately by <see cref="PairedIdentityMatcher"/>.</summary>
    Accepted = 1,

    /// <summary>Must not be shown (not opted in, not an Apple proximity message, name only, filtered model).</summary>
    Rejected = 2,

    /// <summary>More than one candidate source heard within the window: nothing may be picked.</summary>
    Ambiguous = 3,
}

/// <summary>The policy's answer for one sighting.</summary>
public sealed record AttributionDecision(
    AttributionVerdict Verdict,
    string Reason,
    ushort? ModelId = null,
    ModelRecognition? Recognition = null,
    int CandidateCount = 0)
{
    /// <summary>
    /// How likely this source is the user's paired AirPods, filled in by the caller from
    /// <see cref="PairedIdentityMatcher"/> (the policy alone leaves it Unknown). Never a proof.
    /// </summary>
    public IdentityConfidence IdentityConfidence { get; init; } = IdentityConfidence.Unknown;

    /// <summary>
    /// The reason without values that change from one broadcast to the next, used to log a reason only when it
    /// really changes. Null means <see cref="Reason"/> itself is the key.
    /// </summary>
    public string? ReasonKey { get; init; }

    /// <summary>
    /// Cryptographic confirmation, kept for compatibility. Always false: proving a broadcast belongs
    /// to the user's AirPods needs the device's IRK, which comes from the AAP session (unsigned
    /// driver). Use <see cref="IdentityConfidence"/> for the honest likelihood.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Read through the decision instance by callers and tests.")]
    public bool IdentityConfirmed => false;

    /// <summary>The listed model name, or null (unlisted ids never get a claimed name).</summary>
    public string? ModelName => ModelId is ushort id && Recognition == ModelRecognition.Recognised ? AppleModelCatalog.NameOf(id) : null;
}

public sealed record BroadcastAttributionOptions
{
    /// <summary>
    /// Candidates heard within this window count towards ambiguity. Bounded so that a rotated
    /// random address stops counting as a second device once it has gone quiet.
    /// </summary>
    public TimeSpan CandidateWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Two addresses whose batteries are this close (and lid counters agree) are the two buds of one pair.</summary>
    public int BatteryTolerancePercent { get; init; } = 10;

    /// <summary>When set, only this model id is accepted (user-pinned). Null: any proximity model id.</summary>
    public ushort? ExpectedModelId { get; init; }

    /// <summary>
    /// Most distinct (address, model) candidates remembered at once. Past this the policy reports
    /// <see cref="AttributionVerdict.Ambiguous"/> instead of evicting anything, so a flood of made-up
    /// addresses can never push the real source out and leave a fake one looking unique.
    /// </summary>
    public int MaxCandidates { get; init; } = 64;

    /// <summary>Accept ids not in the allow-list as "unrecognised Apple audio device" (needed for Pro 3). Default true.</summary>
    public bool AcceptUnlistedModelIds { get; init; } = true;
}

/// <summary>
/// Decides whether a heard proximity broadcast may be shown as the user's selected device.
/// Rules: the user must have explicitly chosen to observe; a display name alone never identifies
/// a device; only Apple proximity-pairing messages (type 0x07) count; an unlisted model id is an
/// unrecognised Apple audio device with no claimed name; when more than one distinct source
/// (pair; the two buds of one pair broadcast from two addresses and count once, see
/// <see cref="BroadcastSiblings"/>) is heard within <see cref="BroadcastAttributionOptions.CandidateWindow"/>
/// the answer is <see cref="AttributionVerdict.Ambiguous"/>, never a pick. Whether an accepted
/// source is the user's own is a separate, graded question (<see cref="PairedIdentityMatcher"/>). Not thread-safe: callers serialise access.
/// </summary>
public sealed class BroadcastAttributionPolicy
{
    private readonly Dictionary<(ulong Address, ushort Model), (DateTimeOffset Seen, PodReading Reading)> _candidates = new();
    private DateTimeOffset _overflowAt;
    private bool _overflowed;

    public BroadcastAttributionPolicy(BroadcastAttributionOptions? options = null)
    {
        Options = options ?? new BroadcastAttributionOptions();
        ExpectedModelId = Options.ExpectedModelId;
        if (Options.CandidateWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "CandidateWindow must be positive.");
        if (Options.MaxCandidates < 2) throw new ArgumentOutOfRangeException(nameof(options), "MaxCandidates must be at least 2.");
    }

    public BroadcastAttributionOptions Options { get; }

    /// <summary>
    /// The model id currently required (initially <see cref="BroadcastAttributionOptions.ExpectedModelId"/>).
    /// Set from the selected paired device; null accepts any proximity model id.
    /// </summary>
    public ushort? ExpectedModelId { get; set; }

    /// <summary>Forget every candidate (for example when observing stops).</summary>
    public void Reset()
    {
        _candidates.Clear();
        _overflowed = false;
    }

    /// <param name="sighting">What was heard.</param>
    /// <param name="parsed">The parsed proximity message, or null when the payload did not parse.</param>
    /// <param name="userChoseToObserve">True only after the user's explicit Start-observing action.</param>
    /// <param name="now">Current time (injected clock).</param>
    public AttributionDecision Evaluate(AdvertisementSighting sighting, ProximityMessage? parsed, bool userChoseToObserve, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sighting);
        Prune(now);

        if (!userChoseToObserve)
            return new AttributionDecision(AttributionVerdict.Rejected, "The user has not chosen to observe broadcasts.");

        if (sighting.CompanyId != ProximityMessage.AppleCompanyId || parsed?.ModelId is not ushort model)
        {
            return string.IsNullOrWhiteSpace(sighting.LocalName)
                ? new AttributionDecision(AttributionVerdict.Rejected, "Not an Apple proximity-pairing message.")
                : new AttributionDecision(AttributionVerdict.Rejected, "Only a name was heard; a name never identifies a device.");
        }

        var recognition = AppleModelCatalog.Recognise(model);
        if (ExpectedModelId is ushort expected && expected != model)
            return new AttributionDecision(AttributionVerdict.Rejected, $"Model id 0x{model:X4} is not the chosen model.", model, recognition);
        if (recognition == ModelRecognition.Unrecognised && !Options.AcceptUnlistedModelIds)
            return new AttributionDecision(AttributionVerdict.Rejected, $"Model id 0x{model:X4} is not in the allow-list.", model, recognition);

        var key = (sighting.Address, model);
        if (!_candidates.ContainsKey(key) && _candidates.Count >= Options.MaxCandidates)
        {
            // Table full of live candidates: a crowd or a flood. Refuse to insert or evict; nothing may be picked.
            _overflowed = true;
            _overflowAt = now;
        }
        if (OverflowActive(now))
        {
            const string overflowReason = "Too many possible sources heard nearby; not choosing one.";
            return new AttributionDecision(AttributionVerdict.Ambiguous, overflowReason, model, recognition, Options.MaxCandidates + 1)
            { ReasonKey = overflowReason };
        }

        _candidates[key] = (now, PodReading.From(parsed));
        int count = GroupCount();
        if (count > 1)
        {
            const string suffix = "not choosing one.";
            var baseReason = $"{count} possible sources heard nearby; {suffix}";
            var why = ExplainSplit();
            return new AttributionDecision(AttributionVerdict.Ambiguous,
                why is null ? baseReason : $"{baseReason} Not the same pair: {why}.", model, recognition, count)
            { ReasonKey = baseReason };
        }

        return new AttributionDecision(AttributionVerdict.Accepted,
            recognition == ModelRecognition.Recognised
                ? "Apple proximity broadcast with a listed model id; identity unconfirmed."
                : "Apple proximity broadcast with an unlisted model id (unrecognised Apple audio device); identity unconfirmed.",
            model, recognition, count);
    }

    /// <summary>Number of distinct candidate pairs heard within the window ending at <paramref name="now"/>.</summary>
    public int CandidateCount(DateTimeOffset now)
    {
        Prune(now);
        return OverflowActive(now) ? Options.MaxCandidates + 1 : GroupCount();
    }

    /// <summary>True while the candidate table overflowed within the window (or is still full): sources cannot be told apart.</summary>
    private bool OverflowActive(DateTimeOffset now)
        => _overflowed && (now - _overflowAt <= Options.CandidateWindow || _candidates.Count >= Options.MaxCandidates);

    /// <summary>The first disagreement (field and values) between two heard sources that are not the same pair, or null.</summary>
    private string? ExplainSplit()
    {
        var groups = BroadcastSiblings.GroupSiblings(_candidates.Values.Select(c => c.Reading), r => r, Options.BatteryTolerancePercent);
        if (groups.Count < 2) return null;
        var other = groups[1][0];
        foreach (var member in groups[0])
        {
            if (BroadcastSiblings.Disagreement(member, other, Options.BatteryTolerancePercent) is { } why) return why;
        }
        return null;
    }

    private int GroupCount() => BroadcastSiblings.CountGroups(_candidates.Values.Select(c => c.Reading), Options.BatteryTolerancePercent);

    private void Prune(DateTimeOffset now)
    {
        List<(ulong, ushort)>? expired = null;
        foreach (var (key, entry) in _candidates)
        {
            if (now - entry.Seen > Options.CandidateWindow) (expired ??= new()).Add(key);
        }
        if (expired is null) return;
        foreach (var key in expired) _candidates.Remove(key);
    }
}
