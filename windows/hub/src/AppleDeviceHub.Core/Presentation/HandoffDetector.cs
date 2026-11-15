using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// One reading of the matched nearby broadcast, reduced to what the hand-off decision needs.
/// <paramref name="OutOfCase"/> is true only when the broadcast says the broadcasting bud is out
/// of the case; an unreported case state is never treated as active.
/// </summary>
public readonly record struct HandoffBroadcast(IdentityConfidence Identity, bool OutOfCase, DateTimeOffset HeardAt)
{
    public static HandoffBroadcast FromSource(BroadcastSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new HandoffBroadcast(source.Identity, source.ThisPodInCase == false, source.LastHeardAt);
    }
}

public enum HandoffState
{
    /// <summary>No disconnect episode is being watched.</summary>
    Idle = 0,

    /// <summary>Windows reported a disconnect; waiting for the broadcast to stay active.</summary>
    Watching = 1,

    /// <summary>The notice was already shown for this episode.</summary>
    Shown = 2,

    /// <summary>The episode ended without a notice (user disconnect, case, vanished, ...).</summary>
    Cancelled = 3,
}

/// <summary>
/// Decides when to say "your AirPods probably moved to another device". After Windows reports the
/// selected AirPods went from connected to disconnected, a matched broadcast (identity Probable or
/// better) that keeps showing them out of the case for a sustained period suggests another device
/// took them. The detector only decides; showing the notice and the Take back action are the app's.
/// It is a passive hint, never proof, and it never acts on its own. All time comes from the caller.
/// </summary>
public sealed class HandoffDetector
{
    private bool _hasBaseline;
    private bool _lastConnected;
    private HandoffState _state = HandoffState.Idle;
    private DateTimeOffset _episodeStartedAt;
    private DateTimeOffset? _userDisconnectAt;
    private DateTimeOffset? _activeSince;
    private DateTimeOffset _lastCountedHeardAt;
    private DateTimeOffset _lastActiveHeardAt;
    private int _sightings;

    /// <summary>Master switch (HandoffNoticeEnabled). When false nothing is ever shown.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the broadcast must keep showing the pods active after the disconnect.</summary>
    public TimeSpan SustainedFor { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Distinct broadcasts needed inside that period, so one stray packet cannot trigger.</summary>
    public int MinSightings { get; set; } = 3;

    /// <summary>A sighting older than this is treated as the broadcast having vanished.</summary>
    public TimeSpan FreshAfter { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>A Clasp-initiated disconnect this recent before the transition suppresses the notice.</summary>
    public TimeSpan UserIntentWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Give up on an episode that never produced a qualifying sustained broadcast.</summary>
    public TimeSpan EpisodeTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public HandoffState State => _state;

    /// <summary>Call whenever the user presses Disconnect (or equivalent) in Clasp.</summary>
    public void NoteUserDisconnect(DateTimeOffset now)
    {
        _userDisconnectAt = now;
        // The press can be seen just after the Windows change; it still applies to a fresh episode.
        if (_state == HandoffState.Watching && now - _episodeStartedAt <= UserIntentWindow)
            Cancel();
    }

    /// <summary>
    /// Feed the selected device's Windows connection state on every refresh. The first call is only
    /// a baseline; only a connected-to-disconnected change starts an episode.
    /// </summary>
    public void ObserveConnection(bool isConnected, DateTimeOffset now)
    {
        bool hadBaseline = _hasBaseline;
        bool wasConnected = _lastConnected;
        _hasBaseline = true;
        _lastConnected = isConnected;
        if (!hadBaseline || isConnected == wasConnected) return;

        if (isConnected)
        {
            // Windows has them again (we took them back, or they returned on their own).
            _state = HandoffState.Idle;
            ResetSightings();
            return;
        }

        StartEpisode(now);
    }

    /// <summary>Forget the baseline and any episode (selected device changed or was cleared).</summary>
    public void Reset()
    {
        _hasBaseline = false;
        _state = HandoffState.Idle;
        ResetSightings();
    }

    /// <summary>
    /// Feed the matched broadcast (null when none is matched or heard). Call on every refresh and
    /// whenever the broadcast list changes.
    /// </summary>
    public void ObserveBroadcast(HandoffBroadcast? broadcast, DateTimeOffset now)
    {
        if (_state != HandoffState.Watching) return;
        if (broadcast is not { } b)
        {
            // Not matched at all. Before any sighting this just means we are still waiting.
            if (_sightings > 0) Cancel();
            return;
        }

        if (!b.Identity.HasIndependentClue() || !b.OutOfCase)
        {
            Cancel(); // identity dropped, or the pods are in the case
            return;
        }
        if (now - b.HeardAt > FreshAfter)
        {
            if (_sightings > 0) Cancel();
            return;
        }
        if (b.HeardAt < _episodeStartedAt) return; // an old packet from before the disconnect proves nothing

        _activeSince ??= b.HeardAt;
        _lastActiveHeardAt = b.HeardAt;
        if (b.HeardAt > _lastCountedHeardAt)
        {
            _lastCountedHeardAt = b.HeardAt;
            _sightings++;
        }
    }

    /// <summary>
    /// True exactly once per disconnect episode: when the broadcast has stayed active long enough.
    /// Safe to call as often as needed.
    /// </summary>
    public bool Evaluate(DateTimeOffset now)
    {
        if (_state != HandoffState.Watching) return false;
        if (!Enabled)
        {
            Cancel();
            return false;
        }
        if (now - _episodeStartedAt > EpisodeTimeout)
        {
            Cancel();
            return false;
        }
        if (_activeSince is not { } since) return false;
        if (now - _lastActiveHeardAt > FreshAfter)
        {
            Cancel(); // vanished
            return false;
        }
        if (_sightings < MinSightings || _lastActiveHeardAt - since < SustainedFor) return false;

        _state = HandoffState.Shown;
        return true;
    }

    private void StartEpisode(DateTimeOffset now)
    {
        ResetSightings();
        _episodeStartedAt = now;
        bool userInitiated = _userDisconnectAt is { } at && now - at >= TimeSpan.Zero && now - at <= UserIntentWindow;
        _state = !Enabled || userInitiated ? HandoffState.Cancelled : HandoffState.Watching;
    }

    private void Cancel()
    {
        _state = HandoffState.Cancelled;
        ResetSightings();
    }

    private void ResetSightings()
    {
        _activeSince = null;
        _sightings = 0;
        _lastCountedHeardAt = DateTimeOffset.MinValue;
        _lastActiveHeardAt = DateTimeOffset.MinValue;
    }
}

/// <summary>Plain-language wording for the hand-off notice. Honest: "probably", never a claim of which device.</summary>
public static class HandoffNoticeText
{
    public static string Title => Strings.Handoff_Notice_Title;

    public static string Detail => Strings.Handoff_Notice_Detail;

    public static string TakeBackLabel => Strings.Handoff_Notice_TakeBackButton;
    public static string DismissLabel => Strings.Handoff_Notice_DismissButton;

    public static string Status(bool isConnecting, bool isConnected, bool attempted, string? detail)
    {
        if (isConnecting) return Strings.Handoff_Status_TakingBack;
        if (isConnected) return Strings.Handoff_Status_ConnectedAgain;
        if (!attempted) return "";
        return string.IsNullOrWhiteSpace(detail)
            ? Strings.Handoff_Status_CouldntTakeBack
            : detail;
    }
}
