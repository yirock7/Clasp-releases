using System.Globalization;
using System.Text;
using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Battery;

public sealed record BatteryHistoryOptions
{
    /// <summary>Samples older than this are dropped.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(90);

    /// <summary>Per device cap; the oldest samples go first.</summary>
    public int MaxSamplesPerDevice { get; init; } = 20_000;

    /// <summary>Cap on the number of devices kept (least recently updated go first).</summary>
    public int MaxDevices { get; init; } = 8;
}

/// <summary>
/// Local battery history per paired device. A sample is appended only when a value changed.
/// Broadcast readings are recorded only when the source is rated Probable or better; driver and
/// Windows readings come from the paired device itself. The format is a small text file:
/// "clasp-battery-history 1", then "D" + escaped id lines each followed by sample lines
/// "unixSeconds,left,right,case,flags,source" (empty = not reported). Not thread-safe: callers
/// serialise access (the app uses its UI thread).
/// </summary>
public sealed class BatteryHistory
{
    public const string StoreName = "battery-history";
    private const string Header = "clasp-battery-history 1";

    private readonly IBatteryHistoryStore _store;
    private readonly BatteryHistoryOptions _options;
    private readonly Dictionary<string, List<BatterySample>> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _touched = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private bool _dirty;

    public BatteryHistory(IBatteryHistoryStore store, BatteryHistoryOptions? options = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new BatteryHistoryOptions();
        if (_options.MaxAge <= TimeSpan.Zero || _options.MaxSamplesPerDevice < 1 || _options.MaxDevices < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "History limits must be positive.");
    }

    /// <summary>True when there are changes not yet written by <see cref="Flush"/>.</summary>
    public bool IsDirty => _dirty;

    public IReadOnlyList<BatterySample> Samples(string deviceId) =>
        _devices.TryGetValue(deviceId, out var list) ? list : Array.Empty<BatterySample>();

    /// <summary>
    /// Appends <paramref name="sample"/> when it differs from the device's latest one.
    /// Broadcast samples need <paramref name="identity"/> of Probable or better; other sources ignore it.
    /// Returns true when a sample was stored.
    /// </summary>
    public bool Record(string deviceId, BatterySample sample, IdentityConfidence identity = IdentityConfidence.Unknown)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (string.IsNullOrEmpty(deviceId) || !sample.HasLevel) return false;
        if (sample.Source == BatterySampleSource.Broadcast && !identity.HasIndependentClue()) return false;
        if (!_devices.TryGetValue(deviceId, out var list))
        {
            list = new List<BatterySample>();
            _devices[deviceId] = list;
        }
        if (list.Count > 0)
        {
            var last = list[^1];
            if (sample.Time < last.Time) return false;            // clock went backwards: ignore
            if (sample.SameValues(last)) return false;
        }
        list.Add(sample);
        _touched[deviceId] = sample.Time;
        Prune(deviceId, sample.Time);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Enforces the age limit on every device, not only the one being recorded: a device that is no longer
    /// used must not keep readings past <see cref="BatteryHistoryOptions.MaxAge"/>. Returns true when anything went.
    /// </summary>
    private bool PruneAll(DateTimeOffset now)
    {
        bool changed = false;
        var cutoff = now - _options.MaxAge;
        foreach (var id in _devices.Keys.ToArray())
        {
            var list = _devices[id];
            int drop = 0;
            while (drop < list.Count && list[drop].Time < cutoff) drop++;
            if (list.Count - drop > _options.MaxSamplesPerDevice) drop = list.Count - _options.MaxSamplesPerDevice;
            if (drop > 0) { list.RemoveRange(0, drop); changed = true; }
            if (list.Count == 0)
            {
                _devices.Remove(id);
                _touched.Remove(id);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Forgets every reading of every device. Call <see cref="Flush"/> to write the empty history.</summary>
    public void Clear()
    {
        if (_devices.Count == 0 && !_dirty) return;
        _devices.Clear();
        _touched.Clear();
        _dirty = true;
    }

    private void Prune(string deviceId, DateTimeOffset now)
    {
        var list = _devices[deviceId];
        var cutoff = now - _options.MaxAge;
        int drop = 0;
        while (drop < list.Count && list[drop].Time < cutoff) drop++;
        if (list.Count - drop > _options.MaxSamplesPerDevice) drop = list.Count - _options.MaxSamplesPerDevice;
        if (drop > 0) list.RemoveRange(0, drop);

        while (_devices.Count > _options.MaxDevices)
        {
            var oldest = _devices.Keys.Where(k => k != deviceId).OrderBy(k => _touched.GetValueOrDefault(k)).FirstOrDefault();
            if (oldest is null) break;
            _devices.Remove(oldest);
            _touched.Remove(oldest);
        }
    }

    /// <summary>Replaces memory with the stored history. A missing or damaged store gives an empty history.</summary>
    public void Load()
    {
        _devices.Clear();
        _touched.Clear();
        _dirty = false;
        var text = _store.Read(StoreName);
        if (string.IsNullOrEmpty(text)) return;
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.Trim() != Header) return;
        List<BatterySample>? current = null;
        string? currentId = null;
        // Called when a device section is complete (its samples are all known). An empty section is
        // dropped without touching anything else; otherwise, only now, the least recently updated device
        // is evicted if there are too many. So file order never decides which history survives, and at
        // most MaxDevices + 1 devices are in memory (the section being read).
        void FinishSection()
        {
            if (currentId is null || current is null) return;
            if (current.Count == 0)
            {
                if (_devices.TryGetValue(currentId, out var held) && ReferenceEquals(held, current)) _devices.Remove(currentId);
                _dirty = true;
            }
            else
            {
                while (_devices.Count > _options.MaxDevices) { EvictLeastRecentlyUpdated(); }
            }
            current = null;
            currentId = null;
        }
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (line[0] == 'D')
            {
                string id;
                FinishSection();
                try { id = Uri.UnescapeDataString(line[1..]); }
                catch (UriFormatException) { current = null; currentId = null; continue; }
                current = new List<BatterySample>();
                currentId = id;
                _devices[id] = current;
                continue;
            }
            if (current is null) continue;
            var sample = ParseSample(line);
            if (sample is not null && (current.Count == 0 || sample.Time >= current[^1].Time))
            {
                current.Add(sample);
                // Drop the oldest in batches (not one at a time) once the list passes twice its cap.
                if (current.Count > _options.MaxSamplesPerDevice * 2L)
                    current.RemoveRange(0, current.Count - _options.MaxSamplesPerDevice);
            }
        }
        FinishSection();
        foreach (var id in _devices.Keys.ToArray())
        {
            if (_devices[id].Count == 0) _devices.Remove(id);
            else _touched[id] = _devices[id][^1].Time;
        }
        // A file left alone for months must not bring back readings past the limit.
        if (PruneAll(_time.GetUtcNow())) _dirty = true;
        // A hand-edited file may hold more devices than the cap: keep the most recently updated.
        while (_devices.Count > _options.MaxDevices)
        {
            var oldest = _devices.Keys.OrderBy(k => _touched.GetValueOrDefault(k)).First();
            _devices.Remove(oldest);
            _touched.Remove(oldest);
            _dirty = true;
        }
    }

    /// <summary>Removes the device whose newest reading is oldest. Used while loading, after a section is complete.</summary>
    private void EvictLeastRecentlyUpdated()
    {
        string? oldest = null;
        var oldestTime = DateTimeOffset.MaxValue;
        foreach (var (id, list) in _devices)
        {
            var time = list.Count == 0 ? DateTimeOffset.MinValue : list[^1].Time;
            if (time < oldestTime) { oldest = id; oldestTime = time; }
        }
        if (oldest is not null) _devices.Remove(oldest);
        _dirty = true;
    }

    /// <summary>Writes the history if anything changed. Returns false only when a needed write failed.</summary>
    public bool Flush()
    {
        if (PruneAll(_time.GetUtcNow())) _dirty = true;
        if (!_dirty) return true;
        var ok = _store.Write(StoreName, Serialize());
        if (ok) _dirty = false;
        return ok;
    }

    /// <summary>The whole history in the compact text format.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var (id, list) in _devices)
        {
            if (list.Count == 0) continue;
            sb.Append('D').Append(Uri.EscapeDataString(id)).Append('\n');
            foreach (var s in list)
            {
                sb.Append(s.Time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(Level(s.Left)).Append(',').Append(Level(s.Right)).Append(',').Append(Level(s.Case)).Append(',')
                  .Append(Flags(s).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(((int)s.Source).ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string Level(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    // Two bits per part: 0 not reported, 1 reported not charging, 2 reported charging.
    private static int Flags(BatterySample s) => Bits(s.LeftCharging) | (Bits(s.RightCharging) << 2) | (Bits(s.CaseCharging) << 4);

    private static int Bits(bool? v) => v is null ? 0 : v.Value ? 2 : 1;

    private static bool? FromBits(int b) => b switch { 1 => false, 2 => true, _ => null };

    private static BatterySample? ParseSample(string line)
    {
        var parts = line.Split(',');
        if (parts.Length != 6) return null;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var secs)) return null;
        if (secs > 253_402_300_799) return null;
        if (!TryLevel(parts[1], out var l) || !TryLevel(parts[2], out var r) || !TryLevel(parts[3], out var c)) return null;
        if (!int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var flags) || flags > 63) return null;
        if (!int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var src) || src > 2) return null;
        var sample = new BatterySample(DateTimeOffset.FromUnixTimeSeconds(secs), l, r, c,
            FromBits(flags & 3), FromBits((flags >> 2) & 3), FromBits((flags >> 4) & 3), (BatterySampleSource)src);
        return sample.HasLevel ? sample : null;
    }

    private static bool TryLevel(string text, out int? level)
    {
        level = null;
        if (text.Length == 0) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v > 100) return false;
        level = v;
        return true;
    }
}
