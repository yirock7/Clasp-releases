using AppleDeviceHub.Core.Contracts;
using Upstream = NTPods.WinUI.Ipc;

namespace AppleDeviceHub.Core.Interop;

/// <summary>
/// Translates NTPods' daemon <c>Snapshot</c> (upstream wire format, copied unchanged from
/// the removed legacy client into Interop/Upstream/NtpodsMessages.cs) into this project's contract. This checks that
/// the contract can represent the real engine's data. It is NOT wired to a running daemon in
/// phase one, and a successful mapping is not evidence that any hardware feature works.
/// </summary>
public static class NtpodsSnapshotMapper
{
    private static readonly IReadOnlyList<ListeningMode> NtpodsModes = new[]
    {
        ListeningMode.Off, ListeningMode.Transparency, ListeningMode.Adaptive, ListeningMode.NoiseCancellation,
    };

    /// <param name="snapshot">Parsed upstream snapshot.</param>
    /// <param name="deviceId">The app-level id of the device the user selected.</param>
    /// <param name="sequence">Strictly increasing per adapter instance.</param>
    /// <param name="receivedAt">When this process received the snapshot.</param>
    public static DeviceState ToDeviceState(Upstream.Snapshot snapshot, string deviceId, long sequence, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = receivedAt;
        var b = snapshot.Battery ?? new Upstream.Battery();

        // Upstream "connected" means the daemon holds an AAP session. "link_stale" means that
        // session went silent, so it is reported as not-up rather than trusted.
        var session = Observation<bool>.At(snapshot.Connected && !snapshot.LinkStale, now);

        // The snapshot carries no BLE-nearby or Windows-audio facts: leave them unknown.
        var link = new LinkState(Observation<bool>.Unknown, Observation<bool>.Unknown, session, Observation<bool>.Unknown);

        var battery = new BatteryState(
            BatteryComponent.From(b.Left, b.LeftCharging, now),
            BatteryComponent.From(b.Right, b.RightCharging, now),
            BatteryComponent.From(b.Case, b.CaseCharging, now));

        // anc: 0 = unknown, 1..4 = Off / NC / Transparency / Adaptive. Anything else is unknown.
        var actual = snapshot.Anc is >= 1 and <= 4
            ? Observation<ListeningMode>.At((ListeningMode)snapshot.Anc, now)
            : Observation<ListeningMode>.Unknown;

        // Feature flags are only meaningful while a session is up; otherwise they are stale defaults.
        bool live = session.Value == true;

        return new DeviceState(
            DeviceId: deviceId,
            DisplayName: string.IsNullOrWhiteSpace(snapshot.DevName) ? "AirPods" : snapshot.DevName,
            ModelName: snapshot.Model ?? "",
            Origin: DataOrigin.Hardware,
            Sequence: sequence,
            ObservedAt: now,
            // Capabilities are claimed by the engine's code paths, not validated on hardware.
            Capabilities: DeviceCapabilities.Battery | DeviceCapabilities.ListeningModes | DeviceCapabilities.ConversationAwareness,
            Link: link,
            Battery: battery,
            Listening: new ListeningState(actual, NtpodsModes),
            ConversationAwareness: live ? Observation<bool>.At(snapshot.ConversationalAwareness, now) : Observation<bool>.Unknown,
            AdaptiveStrength: Observation<int>.Unknown);
    }
}
