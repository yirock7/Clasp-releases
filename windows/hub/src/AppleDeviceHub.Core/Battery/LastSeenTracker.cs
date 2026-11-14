using System.Globalization;
using System.Text;
using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Battery;

/// <summary>How strong the last broadcast was, in words (it is a rough hint, not a distance).</summary>
public enum SignalBucket
{
    Unknown = 0,
    Far = 1,
    InRange = 2,
    Near = 3,
}

public sealed record LastSeen(DateTimeOffset Time, SignalBucket Signal);

/// <summary>
/// Remembers when the user's AirPods (identity supported by an independent pairing clue) were last heard and roughly
/// how strongly. Nothing is shown for a device that was never heard. Persisted through the same
/// store abstraction as the battery history. Not thread-safe.
/// </summary>
public sealed class LastSeenTracker
{
    public const string StoreName = "last-seen";
    private const string Header = "clasp-last-seen 1";
    private static readonly long MinUnixTimeSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixTimeSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>Writes are only requested when the time moved on by at least this much (or the bucket changed).</summary>
    public static readonly TimeSpan MinUpdateInterval = TimeSpan.FromMinutes(1);

    /// <summary>Cap on the number of devices kept (the least recently heard go first), as in the battery history.</summary>
    public const int MaxDevices = 8;

    private readonly IBatteryHistoryStore _store;
    private readonly Dictionary<string, LastSeen> _seen = new(StringComparer.Ordinal);
    private bool _dirty;

    public LastSeenTracker(IBatteryHistoryStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public bool IsDirty => _dirty;

    public static SignalBucket BucketFor(short? rssi) => rssi switch
    {
        null => SignalBucket.Unknown,
        > -60 => SignalBucket.Near,
        > -80 => SignalBucket.InRange,
        _ => SignalBucket.Far,
    };

    /// <summary>Notes a sighting. Returns true when the stored record changed.</summary>
    public bool Observe(string deviceId, IdentityConfidence identity, short? rssi, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(deviceId) || !identity.HasIndependentClue()) return false;
        var bucket = BucketFor(rssi);
        if (_seen.TryGetValue(deviceId, out var previous))
        {
            if (now < previous.Time) return false;
            if (bucket == previous.Signal && now - previous.Time < MinUpdateInterval) return false;
        }
        _seen[deviceId] = new LastSeen(now, bucket);
        TrimToCap(keep: deviceId);
        _dirty = true;
        return true;
    }

    private void TrimToCap(string? keep)
    {
        while (_seen.Count > MaxDevices)
        {
            var oldest = _seen.Where(pair => pair.Key != keep).OrderBy(pair => pair.Value.Time).Select(pair => pair.Key).FirstOrDefault();
            if (oldest is null) break;
            _seen.Remove(oldest);
        }
    }

    /// <summary>Forgets every device. Call <see cref="Flush"/> to write the empty file.</summary>
    public void Clear()
    {
        if (_seen.Count == 0 && !_dirty) return;
        _seen.Clear();
        _dirty = true;
    }

    public LastSeen? Get(string deviceId) => _seen.TryGetValue(deviceId, out var seen) ? seen : null;

    /// <summary>"Last seen 10 min ago · near", or null when never heard. Short enough for one line.</summary>
    public string? Text(string deviceId, DateTimeOffset now) => Get(deviceId) is { } seen ? Describe(seen, now) : null;

    public static string Describe(LastSeen seen, DateTimeOffset now)
    {
        var text = "Last seen " + Ago(now - seen.Time);
        return seen.Signal switch
        {
            SignalBucket.Near => text + " · near",
            SignalBucket.InRange => text + " · in range",
            SignalBucket.Far => text + " · far away",
            _ => text,
        };
    }

    /// <summary>"just now", "12 min ago", "3 hr ago", "2 days ago".</summary>
    public static string Ago(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return Unit((int)age.TotalMinutes, "min", plural: "min");
        if (age < TimeSpan.FromDays(1)) return Unit((int)age.TotalHours, "hr", plural: "hr");
        return Unit((int)age.TotalDays, "day", plural: "days");
    }

    private static string Unit(int n, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? singular : plural)} ago");

    public void Load()
    {
        _seen.Clear();
        _dirty = false;
        var text = _store.Read(StoreName);
        if (string.IsNullOrEmpty(text)) return;
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.Trim() != Header) return;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split(',');
            if (parts.Length != 3) continue;
            if (!long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var secs)
                || secs < MinUnixTimeSeconds || secs > MaxUnixTimeSeconds) continue;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var bucket) || bucket > (int)SignalBucket.Near) continue;
            try { _seen[Uri.UnescapeDataString(parts[0])] = new LastSeen(DateTimeOffset.FromUnixTimeSeconds(secs), (SignalBucket)bucket); }
            catch (UriFormatException) { }
        }
        // A hand-edited file may hold more devices than the cap: keep the most recently heard.
        TrimToCap(keep: null);
    }

    public bool Flush()
    {
        if (!_dirty) return true;
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var (id, seen) in _seen)
            sb.Append(Uri.EscapeDataString(id)).Append(',')
              .Append(seen.Time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(((int)seen.Signal).ToString(CultureInfo.InvariantCulture)).Append('\n');
        var ok = _store.Write(StoreName, sb.ToString());
        if (ok) _dirty = false;
        return ok;
    }
}
