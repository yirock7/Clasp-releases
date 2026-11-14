// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Ble;

/// <summary>
/// Decides when the pairing help may move its model picker to the model Clasp knows. Routine refreshes repeat the
/// same model and never move it; once the user picks a model by hand, nothing moves it again.
/// </summary>
public sealed class PairingDefaultTracker
{
    private string? _lastKnown;
    private bool _userPicked;

    /// <summary>The user chose a model in the picker.</summary>
    public void UserPicked() => _userPicked = true;

    /// <summary>
    /// True when the picker should be moved to <paramref name="knownModelName"/>: it is a real change from the last
    /// model seen, and the user has not chosen one by hand. The name is remembered either way.
    /// </summary>
    public bool ShouldApply(string? knownModelName)
    {
        if (string.IsNullOrWhiteSpace(knownModelName)) return false;
        bool changed = !string.Equals(_lastKnown, knownModelName, StringComparison.OrdinalIgnoreCase);
        _lastKnown = knownModelName;
        return changed && !_userPicked;
    }
}
