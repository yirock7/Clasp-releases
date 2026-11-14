using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppleDeviceHub.Core.Ble;

namespace BleObservationProbe.Analysis;

/// <summary>Run-level facts that are not derived from sightings.</summary>
public sealed record RunInfo
{
    public required string ProbeVersion { get; init; }
    public string? GitCommit { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required int Seconds { get; init; }
    public required string OutputFolder { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? StoppedUtc { get; init; }

    /// <summary>timer, stopped-by-user, radio-unavailable, error, or in-progress.</summary>
    public required string Ended { get; init; }

    public string? EndDetail { get; init; }
    public int? ExitCode { get; init; }
    public IReadOnlyList<string> StatusLog { get; init; } = [];
    public long WriteErrors { get; init; }
}

/// <summary>
/// Aggregates sightings into the run summary (per address tag, per model id, distinct tags over
/// time, status/battery histograms, non-proximity message types). Not thread-safe: callers serialise.
/// </summary>
public sealed class RunSummaryBuilder
{
    public const string IdentityNotice =
        "Every sighting is UNCONFIRMED. Any nearby Apple device (phones, watches, other people's AirPods) " +
        "can appear here, and no broadcast can be tied to the owner's AirPods without the device keys " +
        "that only the AAP session provides. BLE observation is not proof of identity and not proof " +
        "that any control (listening mode, Conversation Awareness, strength) works.";

    private const int MaxTimelineEntriesPerTag = 60;

    private readonly double _bucketSeconds;
    private readonly Dictionary<string, TagStats> _tags = new(StringComparer.Ordinal);
    private readonly SortedDictionary<ushort, ModelStats> _models = new();
    private readonly SortedDictionary<int, BucketStats> _buckets = new();
    private readonly SortedDictionary<string, long> _parseFailures = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _nonProximityTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<string, long>> _histograms = new(StringComparer.Ordinal);
    private long _total, _proximity, _namePresent;

    public RunSummaryBuilder(double bucketSeconds = 5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketSeconds);
        _bucketSeconds = bucketSeconds;
        foreach (var h in HistogramNames) _histograms[h] = new SortedDictionary<string, long>(StringComparer.Ordinal);
    }

    public static IReadOnlyList<string> HistogramNames { get; } =
    [
        "statusByte", "statusBitSet", "podsHighNibble", "podsLowNibble", "caseNibble",
        "chargingNibble", "lidByte", "connectionStateCode", "colorCode", "primaryIsLeft",
    ];

    public long Total => _total;
    public long ProximityCount => _proximity;
    public int DistinctTags => _tags.Count;

    public void Add(SightingObservation s)
    {
        ArgumentNullException.ThrowIfNull(s);
        _total++;
        if (s.NamePresent) _namePresent++;

        if (!_tags.TryGetValue(s.AddressTag, out var tag))
        {
            tag = new TagStats { FirstT = s.SecondsSinceStart };
            _tags[s.AddressTag] = tag;
        }
        tag.Count++;
        tag.LastT = s.SecondsSinceStart;
        if (s.NamePresent) tag.NamePresent++;
        if (s.Rssi is short r)
        {
            tag.RssiMin = Math.Min(tag.RssiMin ?? r, r);
            tag.RssiMax = Math.Max(tag.RssiMax ?? r, r);
            tag.RssiSum += r;
            tag.RssiCount++;
        }
        foreach (var e in s.Entries)
        {
            string key = AppleTlv.Hex(e.Type);
            Increment(tag.MessageTypes, key);
            if (e.Type != ProximityMessage.ProximityPairingType) Increment(_nonProximityTypes, key);
        }

        int bucketIndex = (int)Math.Floor(Math.Max(0, s.SecondsSinceStart) / _bucketSeconds);
        if (!_buckets.TryGetValue(bucketIndex, out var bucket)) _buckets[bucketIndex] = bucket = new BucketStats();
        bucket.Count++;
        bucket.Tags.Add(s.AddressTag);

        if (s.ParseFailure is string reason) Increment(_parseFailures, reason);

        if (s.Proximity is not ProximityMessage m) return;
        _proximity++;
        tag.Proximity++;
        bucket.ProximityTags.Add(s.AddressTag);

        if (m.ModelId is ushort id)
        {
            tag.ModelIds.Add(id);
            if (!_models.TryGetValue(id, out var model)) _models[id] = model = new ModelStats { FirstT = s.SecondsSinceStart };
            model.Count++;
            model.LastT = s.SecondsSinceStart;
            model.Tags.Add(s.AddressTag);
            string modelKey = $"0x{id:X4}";
            if (!bucket.TagsByModel.TryGetValue(modelKey, out var set)) bucket.TagsByModel[modelKey] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(s.AddressTag);
        }

        if (m.Status is byte st)
        {
            Increment(_histograms["statusByte"], AppleTlv.Hex(st));
            for (int bit = 0; bit < 8; bit++)
            {
                if ((st & (1 << bit)) != 0) Increment(_histograms["statusBitSet"], AppleTlv.Hex((byte)(1 << bit)));
            }
        }
        if (m.PodBatteryByte is byte pods)
        {
            Increment(_histograms["podsHighNibble"], (pods >> 4).ToString(CultureInfo.InvariantCulture));
            Increment(_histograms["podsLowNibble"], (pods & 0x0F).ToString(CultureInfo.InvariantCulture));
        }
        if (SightingObservation.RawAt(m, 5) is byte caseByte)
        {
            Increment(_histograms["caseNibble"], (caseByte & 0x0F).ToString(CultureInfo.InvariantCulture));
            Increment(_histograms["chargingNibble"], (caseByte >> 4).ToString(CultureInfo.InvariantCulture));
        }
        byte? lidByte = SightingObservation.RawAt(m, 6);
        if (lidByte is byte lb) Increment(_histograms["lidByte"], AppleTlv.Hex(lb));
        if (m.ConnectionStateCode is byte cs) Increment(_histograms["connectionStateCode"], AppleTlv.Hex(cs));
        if (m.ColorCode is byte cc) Increment(_histograms["colorCode"], AppleTlv.Hex(cc));
        if (m.PrimaryIsLeft is bool pl) Increment(_histograms["primaryIsLeft"], pl ? "true" : "false");

        // Compact per-tag timeline: only when the decoded state bytes change.
        string state = string.Join(" ",
            $"model={(m.ModelId is ushort mid ? $"0x{mid:X4}" : "?")}",
            $"status={Hx(m.Status)}", $"pods={Hx(m.PodBatteryByte)}", $"case={Hx(SightingObservation.RawAt(m, 5))}",
            $"lid={Hx(lidByte)}", $"conn={Hx(m.ConnectionStateCode)}");
        if (state != tag.LastState)
        {
            tag.LastState = state;
            if (tag.Timeline.Count < MaxTimelineEntriesPerTag) tag.Timeline.Add((s.SecondsSinceStart, state));
            else tag.TimelineDropped++;
        }
    }

    public JsonObject BuildJson(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var tags = OrderedTags();
        var json = new JsonObject
        {
            ["identityNotice"] = IdentityNotice,
            ["probe"] = new JsonObject { ["name"] = "BleObservationProbe", ["version"] = run.ProbeVersion, ["gitCommit"] = run.GitCommit },
            ["parameters"] = new JsonObject
            {
                ["seconds"] = run.Seconds,
                ["outputFolder"] = run.OutputFolder,
                ["arguments"] = new JsonArray(run.Arguments.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                ["timeBucketSeconds"] = _bucketSeconds,
            },
            ["startedUtc"] = Iso(run.StartedUtc),
            ["stoppedUtc"] = run.StoppedUtc is DateTimeOffset st ? Iso(st) : null,
            ["durationSeconds"] = run.StoppedUtc is DateTimeOffset st2 ? Math.Round((st2 - run.StartedUtc).TotalSeconds, 3) : null,
            ["ended"] = run.Ended,
            ["endDetail"] = run.EndDetail,
            ["exitCode"] = run.ExitCode,
            ["statusLog"] = new JsonArray(run.StatusLog.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["fileWriteErrors"] = run.WriteErrors,
            ["totals"] = new JsonObject
            {
                ["appleSightings"] = _total,
                ["proximityDecoded"] = _proximity,
                ["notDecoded"] = _total - _proximity,
                ["distinctAddressTags"] = _tags.Count,
                ["distinctAddressTagsWithProximity"] = _tags.Values.Count(t => t.Proximity > 0),
                ["sightingsWithNamePresent"] = _namePresent,
            },
        };

        var tagArray = new JsonArray();
        foreach (var (name, t) in tags)
        {
            tagArray.Add(new JsonObject
            {
                ["addressTag"] = name,
                ["sightings"] = t.Count,
                ["proximityDecoded"] = t.Proximity,
                ["firstT"] = Math.Round(t.FirstT, 3),
                ["lastT"] = Math.Round(t.LastT, 3),
                ["rssiMin"] = t.RssiMin,
                ["rssiMax"] = t.RssiMax,
                ["rssiMean"] = t.RssiCount > 0 ? Math.Round((double)t.RssiSum / t.RssiCount, 1) : null,
                ["modelIds"] = new JsonArray(t.ModelIds.Select(i => (JsonNode?)JsonValue.Create($"0x{i:X4}")).ToArray()),
                ["namePresentCount"] = t.NamePresent,
                ["messageTypes"] = ToJson(t.MessageTypes),
                ["timeline"] = new JsonArray(t.Timeline.Select(x => (JsonNode?)new JsonObject { ["t"] = Math.Round(x.T, 3), ["state"] = x.State }).ToArray()),
                ["timelineEntriesDropped"] = t.TimelineDropped,
            });
        }
        json["addressTags"] = tagArray;

        var modelArray = new JsonArray();
        foreach (var (id, m) in _models)
        {
            modelArray.Add(new JsonObject
            {
                ["modelId"] = $"0x{id:X4}",
                ["modelName"] = AppleModelCatalog.DisplayNameOf(id),
                ["sightings"] = m.Count,
                ["distinctAddressTags"] = m.Tags.Count,
                ["addressTags"] = new JsonArray(m.Tags.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["firstT"] = Math.Round(m.FirstT, 3),
                ["lastT"] = Math.Round(m.LastT, 3),
            });
        }
        json["models"] = modelArray;

        var bucketArray = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, b) in _buckets)
        {
            int newTags = b.Tags.Count(seen.Add);
            var byModel = new JsonObject();
            foreach (var (model, set) in b.TagsByModel) byModel[model] = set.Count;
            bucketArray.Add(new JsonObject
            {
                ["fromT"] = index * _bucketSeconds,
                ["toT"] = (index + 1) * _bucketSeconds,
                ["sightings"] = b.Count,
                ["distinctTags"] = b.Tags.Count,
                ["distinctProximityTags"] = b.ProximityTags.Count,
                ["newTags"] = newTags,
                ["distinctTagsByModel"] = byModel,
            });
        }
        json["distinctTagsOverTime"] = bucketArray;

        var histograms = new JsonObject();
        foreach (var name in HistogramNames) histograms[name] = ToJson(_histograms[name]);
        json["histograms"] = histograms;
        json["parseFailures"] = ToJson(_parseFailures);
        json["nonProximityMessageTypes"] = ToJson(_nonProximityTypes);
        return json;
    }

    public string BuildText(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');

        L("BleObservationProbe summary");
        L("===========================");
        L();
        L("IDENTITY: " + IdentityNotice);
        L();
        L($"Probe version : {run.ProbeVersion}");
        L($"Git commit    : {run.GitCommit ?? "(not available)"}");
        L($"Arguments     : {string.Join(' ', run.Arguments)}");
        L($"Requested     : {run.Seconds} s");
        L($"Results folder: {run.OutputFolder}");
        L($"Started (UTC) : {Iso(run.StartedUtc)}");
        L($"Stopped (UTC) : {(run.StoppedUtc is DateTimeOffset st ? Iso(st) : "(still running or did not finish cleanly)")}");
        if (run.StoppedUtc is DateTimeOffset st2) L($"Duration      : {(st2 - run.StartedUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        L($"Ended         : {run.Ended}{(run.EndDetail is null ? "" : " - " + run.EndDetail)}");
        L($"Exit code     : {(run.ExitCode is int ec ? ec.ToString(CultureInfo.InvariantCulture) : "(n/a)")}");
        if (run.WriteErrors > 0) L($"File write errors: {run.WriteErrors}");
        foreach (var s in run.StatusLog) L($"Listener status: {s}");
        L();
        L($"Apple sightings: {_total}; proximity decoded: {_proximity}; not decoded: {_total - _proximity}; " +
          $"distinct address tags: {_tags.Count} ({_tags.Values.Count(t => t.Proximity > 0)} with proximity); " +
          $"sightings with a name present: {_namePresent}");
        L();

        L("By model id (proximity messages only; names are upstream assumptions)");
        if (_models.Count == 0) L("  (none)");
        foreach (var (id, m) in _models)
        {
            L($"  0x{id:X4} {AppleModelCatalog.DisplayNameOf(id)}: {m.Count} sightings, {m.Tags.Count} distinct address tags, " +
              $"t={F(m.FirstT)}..{F(m.LastT)} s");
            L($"    tags: {string.Join(", ", m.Tags)}");
        }
        L();

        L($"Distinct address tags over time ({F(_bucketSeconds)} s buckets)");
        L("  from-to s   sightings  tags  proximity-tags  new-tags  tags-by-model");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, b) in _buckets)
        {
            int newTags = b.Tags.Count(seen.Add);
            string byModel = string.Join(" ", b.TagsByModel.Select(kv => $"{kv.Key}:{kv.Value.Count}"));
            L($"  {F(index * _bucketSeconds),5}-{F((index + 1) * _bucketSeconds),-5} {b.Count,9}  {b.Tags.Count,4}  {b.ProximityTags.Count,14}  {newTags,8}  {byModel}");
        }
        L();

        L("Per address tag (tags are a per-run keyed hash; never the raw address)");
        foreach (var (name, t) in OrderedTags())
        {
            string rssi = t.RssiCount > 0
                ? $"rssi {t.RssiMin}..{t.RssiMax} (mean {((double)t.RssiSum / t.RssiCount).ToString("0.0", CultureInfo.InvariantCulture)})"
                : "rssi ?";
            L($"  {name}: {t.Count} sightings ({t.Proximity} proximity), t={F(t.FirstT)}..{F(t.LastT)} s, {rssi}, " +
              $"models [{string.Join(",", t.ModelIds.Select(i => $"0x{i:X4}"))}], types [{string.Join(",", t.MessageTypes.Select(kv => $"{kv.Key}x{kv.Value}"))}]" +
              (t.NamePresent > 0 ? $", name present {t.NamePresent}x" : ""));
            foreach (var (time, state) in t.Timeline) L($"      {F(time),6} s  {state}");
            if (t.TimelineDropped > 0) L($"      ... {t.TimelineDropped} further state changes not listed");
        }
        L();

        L("Histograms (proximity messages only; raw values, upstream-assumed meaning)");
        foreach (var name in HistogramNames)
        {
            var h = _histograms[name];
            L($"  {name}: {(h.Count == 0 ? "(none)" : string.Join(", ", h.Select(kv => $"{kv.Key}={kv.Value}")))}");
        }
        L();

        L("Non-proximity Apple message types (count of entries per type)");
        L(_nonProximityTypes.Count == 0 ? "  (none)" : "  " + string.Join(", ", _nonProximityTypes.Select(kv => $"{kv.Key}={kv.Value}")));
        L();
        L("Parse failures / not decoded (reason = count)");
        if (_parseFailures.Count == 0) L("  (none)");
        foreach (var (reason, n) in _parseFailures) L($"  {n,6}  {reason}");
        L();
        L("Reminder: " + IdentityNotice);
        return sb.ToString();
    }

    public static string ToJsonText(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private List<(string Name, TagStats Stats)> OrderedTags()
        => _tags.OrderBy(kv => kv.Value.FirstT).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();

    private static void Increment(SortedDictionary<string, long> d, string key)
        => d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;

    private static JsonObject ToJson(SortedDictionary<string, long> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v;
        return o;
    }

    private static string Hx(byte? b) => b is byte x ? AppleTlv.Hex(x) : "?";

    private static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private sealed class TagStats
    {
        public long Count, Proximity, NamePresent, RssiSum, RssiCount;
        public int TimelineDropped;
        public double FirstT, LastT;
        public short? RssiMin, RssiMax;
        public SortedSet<ushort> ModelIds { get; } = new();
        public SortedDictionary<string, long> MessageTypes { get; } = new(StringComparer.Ordinal);
        public List<(double T, string State)> Timeline { get; } = new();
        public string? LastState;
    }

    private sealed class ModelStats
    {
        public long Count;
        public double FirstT, LastT;
        public SortedSet<string> Tags { get; } = new(StringComparer.Ordinal);
    }

    private sealed class BucketStats
    {
        public long Count;
        public SortedSet<string> Tags { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> ProximityTags { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, SortedSet<string>> TagsByModel { get; } = new(StringComparer.Ordinal);
    }
}
