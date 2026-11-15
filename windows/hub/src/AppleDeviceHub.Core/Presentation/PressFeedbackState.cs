namespace AppleDeviceHub.Core.Presentation;

/// <summary>Where a pressable control's scale and light should be heading.</summary>
public enum PressTarget
{
    /// <summary>Nothing under the pointer, or a finger that is not touching.</summary>
    Rest,

    /// <summary>A mouse or pen is over the control and nothing is held.</summary>
    Hover,

    /// <summary>The control is held down and letting go now would count as a click.</summary>
    Pressed,
}

/// <summary>
/// The press, hover and "would a release count?" logic of a pressable control, with no UI
/// dependency so it can be tested. The App feeds it pointer, key and focus events and animates
/// towards <see cref="Target"/>.
/// <para>Rules: touch never hovers. A held pointer that slides outside goes back to
/// <see cref="PressTarget.Rest"/> but is still logically held, and presses again if it returns.
/// Releasing goes to <see cref="PressTarget.Hover"/> when a mouse or pen is inside, otherwise
/// <see cref="PressTarget.Rest"/>, and springs.</para>
/// <para>Every event method returns true when <see cref="Target"/> changed. <see cref="Spring"/>
/// describes the most recent call only: true when that call ended a press (release, cancel, key up
/// or focus loss) and moved the target.</para>
/// </summary>
public sealed class PressFeedbackState
{
    private bool _inside;
    private bool _hoverCapable = true;
    private bool _pointerHeld;
    private bool _keyHeld;

    public PressTarget Target { get; private set; } = PressTarget.Rest;

    /// <summary>The last transition ended a press and should use the release spring.</summary>
    public bool Spring { get; private set; }

    /// <summary>A pointer is held down on the control (even while it is outside the bounds).</summary>
    public bool IsPointerHeld => _pointerHeld;

    /// <summary>The pointer is inside the control's bounds.</summary>
    public bool IsPointerInside => _inside;

    public bool PointerEntered(bool isTouch)
    {
        _inside = true;
        _hoverCapable = !isTouch;
        return Update(releasing: false);
    }

    /// <summary>The pointer left the bounds. If it is held, it stays held; only the target changes.</summary>
    public bool PointerExited()
    {
        _inside = false;
        return Update(releasing: false);
    }

    public bool PointerPressed(bool isTouch = false)
    {
        // A press implies the pointer is over the control (touch may press without a prior enter).
        _inside = true;
        _hoverCapable = !isTouch;
        _pointerHeld = true;
        return Update(releasing: false);
    }

    /// <summary>Position check from the pointer-moved event: true when the pointer is inside the bounds.</summary>
    public bool PointerMoved(bool isInside)
    {
        _inside = isInside;
        return Update(releasing: false);
    }

    public bool PointerReleased()
    {
        if (!_pointerHeld) return Reset();
        _pointerHeld = false;
        return Update(releasing: true);
    }

    /// <summary>Cancel or capture loss. Like a release, but a touch pointer that was cancelled is gone.</summary>
    public bool PointerCanceled()
    {
        if (!_pointerHeld)
        {
            if (!_hoverCapable) _inside = false;
            return Reset();
        }
        _pointerHeld = false;
        if (!_hoverCapable) _inside = false;
        return Update(releasing: true);
    }

    public bool KeyDown()
    {
        _keyHeld = true;
        return Update(releasing: false);
    }

    public bool KeyUp()
    {
        if (!_keyHeld) return Reset();
        _keyHeld = false;
        return Update(releasing: true);
    }

    /// <summary>Focus left the control: anything held is let go.</summary>
    public bool FocusLost()
    {
        bool wasHeld = _pointerHeld || _keyHeld;
        _pointerHeld = false;
        _keyHeld = false;
        return wasHeld ? Update(releasing: true) : Reset();
    }

    private bool Reset()
    {
        Spring = false;
        return false;
    }

    private bool Update(bool releasing)
    {
        PressTarget previous = Target;
        PressTarget next =
            _keyHeld || (_pointerHeld && _inside) ? PressTarget.Pressed
            : _inside && _hoverCapable && !_pointerHeld ? PressTarget.Hover
            : PressTarget.Rest;

        Target = next;
        bool changed = next != previous;
        Spring = changed && releasing && previous == PressTarget.Pressed;
        return changed;
    }
}
