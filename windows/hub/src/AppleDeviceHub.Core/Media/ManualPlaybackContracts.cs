namespace AppleDeviceHub.Core.Media;

/// <summary>The direct action requested from the current Windows media player.</summary>
public enum PlaybackAction
{
    Play = 0,
    Pause = 1,
    Next = 2,
    Previous = 3,
}

/// <summary>The outcome of a direct playback request.</summary>
public enum PlaybackCommandStatus
{
    Sent = 0,
    NoPlayer = 1,
    Unsupported = 2,
    Busy = 3,
    Refused = 4,
    TimedOut = 5,
    Cancelled = 6,
    Unavailable = 7,
    Failed = 8,
}

/// <summary>A result that describes what Windows did with a requested playback action.</summary>
public readonly record struct PlaybackCommandResult(PlaybackCommandStatus Status, string Message)
{
    /// <summary>True only when Windows accepted the request; this does not claim playback changed.</summary>
    public bool WasSent => Status == PlaybackCommandStatus.Sent;
}

/// <summary>
/// Information about the Windows player currently selected by SMTC. A session id changes when
/// Windows switches to or recreates the current session. Capabilities come from that session's
/// own SMTC controls and are never inferred from playback state. <paramref name="StatusIsRoutine"/> marks a
/// status that only says everything is working (the player name is already on screen), so the flyout can hide it.
/// </summary>
public sealed record PlaybackSnapshot(
    string? SessionId,
    string? PlayerName,
    string? Title,
    MediaPlayback Playback,
    bool CanPlay,
    bool CanPause,
    bool CanNext,
    bool CanPrevious,
    string Status,
    bool StatusIsRoutine = false)
{
    /// <summary>True when Windows currently exposes a target session.</summary>
    public bool HasPlayer => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>Whether the selected session advertises support for an explicit action.</summary>
    public bool Supports(PlaybackAction action) => action switch
    {
        PlaybackAction.Play => CanPlay,
        PlaybackAction.Pause => CanPause,
        PlaybackAction.Next => CanNext,
        PlaybackAction.Previous => CanPrevious,
        _ => false,
    };

    /// <summary>Snapshot shown when SMTC is available but Windows has no current media session.</summary>
    public static PlaybackSnapshot NoPlayer { get; } = new(
        null, null, null, MediaPlayback.Unknown, false, false, false, false,
        "Nothing is playing in Windows right now.");

    /// <summary>Snapshot shown when Windows does not grant access to the SMTC manager.</summary>
    public static PlaybackSnapshot Unavailable { get; } = new(
        null, null, null, MediaPlayback.Unknown, false, false, false, false,
        "Windows media controls aren't available.");
}

/// <summary>
/// The Windows-specific session source used by <see cref="ManualPlaybackCoordinator"/>.
/// Implementations must bind each send to the requested session id and return ordinary Windows
/// failures as a result instead of throwing from event callbacks.
/// </summary>
public interface IPlaybackSessionSource : IAsyncDisposable
{
    /// <summary>The current Windows player session.</summary>
    PlaybackSnapshot Current { get; }

    /// <summary>Raised when the selected player, its metadata, playback state, or capabilities change.</summary>
    event EventHandler<PlaybackSnapshot>? Changed;

    /// <summary>Send one explicit action to exactly the named session.</summary>
    Task<PlaybackCommandResult> ExecuteAsync(
        PlaybackAction action,
        string sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Public manual playback service used by the presentation layer. It deliberately has no
/// Bluetooth or AirPods dependency, so these actions remain available without a device link.
/// </summary>
public interface IPlaybackController : IAsyncDisposable
{
    /// <summary>The latest current Windows player state.</summary>
    PlaybackSnapshot Current { get; }

    /// <summary>Raised when the current Windows player state changes. May arrive on any thread.</summary>
    event EventHandler<PlaybackSnapshot>? Changed;

    /// <summary>Request Play on the current Windows media player.</summary>
    Task<PlaybackCommandResult> PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>Request Pause on the current Windows media player.</summary>
    Task<PlaybackCommandResult> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Request Next on the current Windows media player.</summary>
    Task<PlaybackCommandResult> NextAsync(CancellationToken cancellationToken = default);

    /// <summary>Request Previous on the current Windows media player.</summary>
    Task<PlaybackCommandResult> PreviousAsync(CancellationToken cancellationToken = default);
}
