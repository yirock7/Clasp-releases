namespace AppleDeviceHub.Core.Pairing;

/// <summary>Decides which discovered devices are offered for pairing, and in what order.</summary>
public static class PairingCandidateFilter
{
    private const int AudioVideoMajorClass = 4;

    /// <summary>True when the name contains AirPods, Beats or Powerbeats (a renamed device still matches).</summary>
    public static bool IsAppleAudioName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && (name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Beats", StringComparison.OrdinalIgnoreCase));

    /// <summary>True for an Apple audio name whose Bluetooth major class is Audio/Video (or unknown).</summary>
    public static bool IsCandidate(string? name, int? codMajorClass)
        => IsAppleAudioName(name) && (codMajorClass is null || codMajorClass == AudioVideoMajorClass);

    /// <summary>Strongest signal first (unknown last), then by name, with each id listed once.</summary>
    public static IReadOnlyList<PairingCandidate> Order(IEnumerable<PairingCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<PairingCandidate>();
        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate.Id)) unique.Add(candidate);
        }
        return unique
            .OrderBy(c => c.SignalStrength is null ? 1 : 0)
            .ThenByDescending(c => c.SignalStrength ?? int.MinValue)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
