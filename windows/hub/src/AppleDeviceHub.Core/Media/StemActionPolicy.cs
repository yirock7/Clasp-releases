namespace AppleDeviceHub.Core.Media;

/// <summary>A media key that an AirPods stem press arrives as (Windows cannot tell it from a keyboard key).</summary>
public enum StemGesture
{
    PlayPause = 0,
    Next = 1,
    Previous = 2,
}

/// <summary>What a stem gesture should do.</summary>
public enum StemAction
{
    /// <summary>Leave the key alone; Windows handles it as usual (the default).</summary>
    PassThrough = 0,

    /// <summary>Toggle mute on the default microphone.</summary>
    MuteMicrophone = 1,

    /// <summary>Send a play/pause key instead.</summary>
    PlayPause = 2,

    /// <summary>Send a next-track key instead.</summary>
    Next = 3,

    /// <summary>Send a previous-track key instead.</summary>
    Previous = 4,

    /// <summary>Swallow the key and do nothing.</summary>
    None = 5,
}

/// <summary>User configuration for the remaps. The default is off with every gesture passed through.</summary>
public sealed record StemActionSettings(
    bool Enabled = false,
    StemAction PlayPause = StemAction.PassThrough,
    StemAction Next = StemAction.PassThrough,
    StemAction Previous = StemAction.PassThrough)
{
    public static StemActionSettings Default { get; } = new();

    public StemAction For(StemGesture gesture) => gesture switch
    {
        StemGesture.PlayPause => PlayPause,
        StemGesture.Next => Next,
        StemGesture.Previous => Previous,
        _ => StemAction.PassThrough,
    };
}

/// <summary>The facts the policy needs about the current connection and output.</summary>
public readonly record struct StemContext(bool AirPodsConnected, bool OutputIsAirPods)
{
    public static StemContext Inactive => default;
    public bool IsActive => AirPodsConnected && OutputIsAirPods;
}

/// <summary>
/// The verdict for one key event. When <see cref="Swallow"/> is false the key must reach Windows
/// untouched. When true, the original key is blocked and <see cref="Action"/> is carried out
/// when <see cref="ShouldRun"/> says so (not for <see cref="StemAction.None"/> or a duplicate).
/// </summary>
public readonly record struct StemDecision(bool Swallow, StemAction Action, bool IsDuplicate, string Reason)
{
    public static StemDecision Pass(string reason) => new(false, StemAction.PassThrough, false, reason);

    /// <summary>True when the caller should carry out <see cref="Action"/>.</summary>
    public bool ShouldRun => Swallow && !IsDuplicate && Action != StemAction.None;
}

/// <summary>
/// Pure decision logic for remapping AirPods stem presses (arriving as media keys). Off by
/// default. Keys are touched only while the feature is on AND the AirPods are connected AND are
/// the current audio output; in every other case, including a gesture mapped to
/// <see cref="StemAction.PassThrough"/> or to itself, the key passes through untouched.
/// <para>
/// A key-down that fires twice for the same key within <see cref="DebounceWindow"/> is treated as
/// one press: if the first was swallowed the repeat is swallowed without running the action
/// again; if the first passed through, so does the repeat. The matching key-up of a swallowed
/// key-down is swallowed too, so no key is ever left half-delivered.
/// </para>
/// Thread-safe: settings arrive from the UI thread, key events from the hook thread.
/// </summary>
public sealed class StemActionPolicy
{
    /// <summary>Same key within this window counts as one press.</summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(150);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<StemGesture, (DateTimeOffset At, bool Swallowed)> _last = [];
    private readonly HashSet<StemGesture> _swallowedDown = [];
    private StemActionSettings _settings = StemActionSettings.Default;

    public StemActionPolicy(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Replaces the configuration. Turning the feature off forgets debounce history.</summary>
    public void Configure(StemActionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = settings;
            if (!settings.Enabled) _last.Clear();
        }
    }

    /// <summary>Decides one key-down.</summary>
    public StemDecision OnKeyDown(StemGesture gesture, StemContext context)
    {
        lock (_gate)
        {
            var now = _clock();
            var action = _settings.For(gesture);
            StemDecision decision;
            if (!_settings.Enabled) decision = StemDecision.Pass("Stem actions are off.");
            else if (!context.AirPodsConnected) decision = StemDecision.Pass("AirPods are not connected.");
            else if (!context.OutputIsAirPods) decision = StemDecision.Pass("AirPods are not the audio output.");
            else if (action == StemAction.PassThrough || IsSameKey(gesture, action))
                decision = StemDecision.Pass("This gesture is not remapped.");
            else if (_last.TryGetValue(gesture, out var previous)
                && now - previous.At >= TimeSpan.Zero
                && now - previous.At < DebounceWindow)
            {
                // Repeat of the previous press: follow what happened to it. The original timestamp is
                // kept so a long burst cannot extend the window indefinitely.
                if (!previous.Swallowed) return StemDecision.Pass("Duplicate of a press that passed through.");
                _swallowedDown.Add(gesture);
                return new StemDecision(true, action, true, "Duplicate of a press just handled.");
            }
            else decision = new StemDecision(true, action, false, "Remapped.");

            _last[gesture] = (now, decision.Swallow);
            if (decision.Swallow) _swallowedDown.Add(gesture); else _swallowedDown.Remove(gesture);
            return decision;
        }
    }

    /// <summary>True when the key-up should be swallowed because its key-down was.</summary>
    public bool OnKeyUp(StemGesture gesture)
    {
        lock (_gate) return _swallowedDown.Remove(gesture);
    }

    private static bool IsSameKey(StemGesture gesture, StemAction action) =>
        (gesture == StemGesture.PlayPause && action == StemAction.PlayPause)
        || (gesture == StemGesture.Next && action == StemAction.Next)
        || (gesture == StemGesture.Previous && action == StemAction.Previous);
}
