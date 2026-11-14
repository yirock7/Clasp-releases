using System.Globalization;
using System.Text;

namespace AppleDeviceHub.Core.Battery;

/// <summary>One remembered case level and when it was heard. No charging flag: it would be hours old.</summary>
public sealed record RememberedCase(int Percent, DateTimeOffset Time);

/// <summary>
/// Remembers the last case level heard for each paired device, so a restart or update does not blank the Case ring.
/// AirPods report the case only while the lid is open, which is rare. The charging flag is deliberately not kept: a
/// remembered reading must never claim to be charging hours later. Display only: it never feeds alerts, automation or
/// battery history. Persisted through the same store abstraction as the battery history. Not thread-safe.
/// </summary>
public sealed class LastCaseMemory
{
    public const string StoreName = "last-case";
    private const string Header = "clasp-last-case 1";
    private static readonly long MinUnixTimeSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixTimeSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>Cap on the number of devices kept (the least recently heard go first), as in the battery history.</summary>
    public const int MaxDevices = 8;

    private readonly IBatteryHistoryStore _store;
    private readonly Dictionary<string, RememberedCase> _cases = new(StringComparer.Ordinal);
    private bool _dirty;

    public LastCaseMemory(IBatteryHistoryStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public bool IsDirty => _dirty;

    /// <summary>Notes a case level. Returns true when the stored record changed (older readings and bad percents are ignored).</summary>
    public bool Observe(string deviceId, int percent, DateTimeOffset time)
    {
        if (string.IsNullOrEmpty(deviceId) || percent is < 0 or > 100) return false;
        if (_cases.TryGetValue(deviceId, out var previous))
        {
            if (time < previous.Time) return false;
            if (time == previous.Time && percent == previous.Percent) return false;
        }
        _cases[deviceId] = new RememberedCase(percent, time);
        while (_cases.Count > MaxDevices)
        {
            var oldest = _cases.Where(pair => pair.Key != deviceId).OrderBy(pair => pair.Value.Time).Select(pair => pair.Key).FirstOrDefault();
            if (oldest is null) break;
            _cases.Remove(oldest);
        }
        _dirty = true;
        return true;
    }

    /// <summary>Forgets every device. Call <see cref="Flush"/> to write the empty file.</summary>
    public void Clear()
    {
        if (_cases.Count == 0 && !_dirty) return;
        _cases.Clear();
        _dirty = true;
    }

    public RememberedCase? Get(string? deviceId)
        => !string.IsNullOrEmpty(deviceId) && _cases.TryGetValue(deviceId, out var remembered) ? remembered : null;

    /// <summary>Replaces memory with the stored data. A missing or damaged store gives an empty memory.</summary>
    public void Load()
    {
        _cases.Clear();
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
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent > 100) continue;
            try
            {
                var id = Uri.UnescapeDataString(parts[0]);
                if (id.Length == 0) continue;
                _cases[id] = new RememberedCase(percent, DateTimeOffset.FromUnixTimeSeconds(secs));
            }
            catch (UriFormatException) { }
        }
        // A hand-edited file may hold more devices than the cap: keep the most recently heard.
        if (_cases.Count > MaxDevices)
            foreach (var id in _cases.OrderByDescending(pair => pair.Value.Time).Skip(MaxDevices).Select(pair => pair.Key).ToArray())
                _cases.Remove(id);
    }

    public bool Flush()
    {
        if (!_dirty) return true;
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var (id, remembered) in _cases)
            sb.Append(Uri.EscapeDataString(id)).Append(',')
              .Append(remembered.Time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(remembered.Percent.ToString(CultureInfo.InvariantCulture)).Append('\n');
        var ok = _store.Write(StoreName, sb.ToString());
        if (ok) _dirty = false;
        return ok;
    }
}
