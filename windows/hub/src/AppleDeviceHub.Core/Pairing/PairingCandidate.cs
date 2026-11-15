namespace AppleDeviceHub.Core.Pairing;

/// <summary>An unpaired AirPods or Beats device that answered the Bluetooth search.</summary>
/// <param name="Id">Stable id from the platform, used to pair this exact device.</param>
/// <param name="Name">The name the device advertises.</param>
/// <param name="SignalStrength">Signal strength in dBm, or null when unknown.</param>
public sealed record PairingCandidate(string Id, string Name, int? SignalStrength);
