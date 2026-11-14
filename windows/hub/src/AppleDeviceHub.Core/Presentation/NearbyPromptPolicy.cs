using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// Turns passive broadcast snapshots into a small number of optional nearby-case prompts.
/// It never attributes a broadcast to the selected paired device. A prompt needs one fresh source
/// with a stable identity and the upstream-assumed lid bit to say open while the broadcasting bud
/// is in the case. Ambiguity, staleness, a closed lid, or a source change resets the settle window.
/// Address rotation cannot create another prompt until an open case ends or all nearby sources
/// have been absent for the cooldown.
/// </summary>
public sealed class NearbyPromptPolicy
{
    private bool _nearbyPresent;
    private bool _openEpisodeActive;
    private bool _nearbyEpisodeNotified;
    private Guid? _openCandidateId;
    private Guid? _activeOpenSourceId;
    private DateTimeOffset? _openCandidateSince;
    private DateTimeOffset? _absenceSince;
    private DateTimeOffset? _lastPromptAt;

    /// <summary>Minimum time between prompts and the global absence needed to rearm.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How recent a snapshot must be before its lid bit may prompt.</summary>
    public TimeSpan FreshAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one unique open source must remain stable before it can prompt.</summary>
    public TimeSpan StableOpenAfter { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Evaluates the latest snapshots. Calling this again with the same open source is harmless.
    /// </summary>
    public NearbyPromptDecision Evaluate(
        IReadOnlyList<BroadcastSource> sources,
        BroadcastPhase phase,
        bool observing,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (Cooldown <= TimeSpan.Zero) throw new InvalidOperationException("Cooldown must be positive.");
        if (FreshAfter <= TimeSpan.Zero) throw new InvalidOperationException("FreshAfter must be positive.");
        if (StableOpenAfter <= TimeSpan.Zero) throw new InvalidOperationException("StableOpenAfter must be positive.");

        bool nearby = observing && sources.Count > 0;
        if (!nearby)
        {
            bool episodeEnded = _nearbyPresent;
            _nearbyPresent = false;
            _absenceSince ??= now;
            ResetOpenCandidate();
            if (now - _absenceSince >= Cooldown)
            {
                _openEpisodeActive = false;
                _nearbyEpisodeNotified = false;
                _activeOpenSourceId = null;
            }
            return new NearbyPromptDecision(
                IsNearby: false,
                HasUniqueFreshOpenSource: false,
                ShouldPrompt: false,
                ShouldNotifyNearbyEpisodePresent: false,
                ShouldNotifyNearbyEpisodeAbsent: episodeEnded);
        }

        if (!_nearbyPresent && _absenceSince is { } absentAt)
        {
            // Require one continuous global quiet period, so a random address rotation cannot
            // look like a new case-open episode.
            if (now - absentAt >= Cooldown)
            {
                _openEpisodeActive = false;
                _nearbyEpisodeNotified = false;
                _activeOpenSourceId = null;
            }
            _absenceSince = null;
        }
        _nearbyPresent = true;

        bool uniqueFresh = sources.Count == 1
            && phase == BroadcastPhase.Fresh
            && IsFresh(sources[0], now);
        if (!uniqueFresh)
        {
            ResetOpenCandidate();
            return new NearbyPromptDecision(
                IsNearby: true,
                HasUniqueFreshOpenSource: false,
                ShouldPrompt: false,
                ShouldNotifyNearbyEpisodePresent: false,
                ShouldNotifyNearbyEpisodeAbsent: false);
        }

        var source = sources[0];
        if (source.ThisPodInCase == true && source.LidClosed == true)
        {
            _openEpisodeActive = false;
            _activeOpenSourceId = null;
            ResetOpenCandidate();
            return new NearbyPromptDecision(
                IsNearby: true,
                HasUniqueFreshOpenSource: false,
                ShouldPrompt: false,
                ShouldNotifyNearbyEpisodePresent: false,
                ShouldNotifyNearbyEpisodeAbsent: false);
        }

        bool isOpen = source.ThisPodInCase == true && source.LidClosed == false;
        if (!isOpen)
        {
            ResetOpenCandidate();
            return new NearbyPromptDecision(
                IsNearby: true,
                HasUniqueFreshOpenSource: false,
                ShouldPrompt: false,
                ShouldNotifyNearbyEpisodePresent: false,
                ShouldNotifyNearbyEpisodeAbsent: false);
        }

        if (_openEpisodeActive)
        {
            ResetOpenCandidate();
            return new NearbyPromptDecision(
                IsNearby: true,
                HasUniqueFreshOpenSource: source.Id == _activeOpenSourceId,
                ShouldPrompt: false,
                ShouldNotifyNearbyEpisodePresent: false,
                ShouldNotifyNearbyEpisodeAbsent: false);
        }

        if (_openCandidateId != source.Id)
        {
            _openCandidateId = source.Id;
            _openCandidateSince = now;
            return PendingOpen();
        }

        if (_openCandidateSince is not { } candidateSince || now - candidateSince < StableOpenAfter)
            return PendingOpen();

        _openEpisodeActive = true;
        _activeOpenSourceId = source.Id;
        bool shouldPrompt = _lastPromptAt is null || now - _lastPromptAt >= Cooldown;
        if (shouldPrompt) _lastPromptAt = now;
        bool notifyPresent = !_nearbyEpisodeNotified;
        _nearbyEpisodeNotified = true;
        return new NearbyPromptDecision(
            IsNearby: true,
            HasUniqueFreshOpenSource: true,
            ShouldPrompt: shouldPrompt,
            ShouldNotifyNearbyEpisodePresent: notifyPresent,
            ShouldNotifyNearbyEpisodeAbsent: false);
    }

    private static NearbyPromptDecision PendingOpen() => new(
        IsNearby: true,
        HasUniqueFreshOpenSource: false,
        ShouldPrompt: false,
        ShouldNotifyNearbyEpisodePresent: false,
        ShouldNotifyNearbyEpisodeAbsent: false);

    private void ResetOpenCandidate()
    {
        _openCandidateId = null;
        _openCandidateSince = null;
    }

    private bool IsFresh(BroadcastSource source, DateTimeOffset now)
        => source.LastHeardAt <= now && now - source.LastHeardAt <= FreshAfter;
}

/// <summary>The presentation and presence changes emitted by one policy evaluation.</summary>
public readonly record struct NearbyPromptDecision(
    bool IsNearby,
    bool HasUniqueFreshOpenSource,
    bool ShouldPrompt,
    bool ShouldNotifyNearbyEpisodePresent,
    bool ShouldNotifyNearbyEpisodeAbsent);
