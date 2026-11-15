using System.Globalization;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>One battery gauge (left bud, right bud, or case), ready for display.</summary>
public sealed class BatteryItemViewModel : ObservableObject
{
    /// <summary>Values older than this are shown as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public const int LowThreshold = 20;

    private double _level01;
    private string _percentText = "—";
    private bool _isKnown;
    private bool _isCharging;
    private bool _isStale;
    private bool _isLow;
    private string _statusText = Strings.Battery_Status_Unknown;
    private string _label;
    private string _spokenLabel;
    private string _automationTail = ", " + Strings.Battery_Spoken_Unknown;

    public BatteryItemViewModel(string label, string spokenLabel)
    {
        _label = label;
        _spokenLabel = spokenLabel;
    }

    /// <summary>Short visible label: "L", "R", "Case".</summary>
    public string Label { get => _label; private set => Set(ref _label, value); }

    /// <summary>Screen-reader label: "Left earbud".</summary>
    public string SpokenLabel { get => _spokenLabel; private set => Set(ref _spokenLabel, value); }

    /// <summary>0..1 for gauges. 0 when unknown: use <see cref="IsKnown"/> to draw an empty/dashed gauge.</summary>
    public double Level01 { get => _level01; private set => Set(ref _level01, value); }

    /// <summary>"82%", or "—" when unknown. Never a guessed number.</summary>
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }

    public bool IsKnown { get => _isKnown; private set => Set(ref _isKnown, value); }
    public bool IsCharging { get => _isCharging; private set => Set(ref _isCharging, value); }
    public bool IsStale { get => _isStale; private set => Set(ref _isStale, value); }
    public bool IsLow { get => _isLow; private set => Set(ref _isLow, value); }

    /// <summary>"Charging", "Unknown", "12 min ago" (then "3 h ago", "2 d ago"), or "" when fresh and idle.</summary>
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    // The spoken text is the label plus the state, so a label change only has to rebuild the front.
    public string AutomationName => SpokenLabel + _automationTail;

    /// <summary>Changes the labels, for a gauge whose subject changes (the Windows battery follows the selected device's name).</summary>
    internal void SetLabels(string label, string spokenLabel)
    {
        if (label == _label && spokenLabel == _spokenLabel) return;
        Label = label;
        SpokenLabel = spokenLabel;
        Raise(nameof(AutomationName));
    }

    /// <summary>
    /// Updates the item from a reading. <paramref name="inTenPercentSteps"/> is true for broadcast readings, which come in 10% steps: a reported 0 means 0 to 9%, and is shown as
    /// "&lt;10%" with a small visible arc instead of an empty-looking "0%".
    /// </summary>
    internal void Update(BatteryComponent component, DateTimeOffset now, bool isDemo, bool inTenPercentSteps = false)
    {
        var freshness = component.Level.FreshnessAt(now, StaleAfter);
        // The percentage exists only when it was reported; there is no fallback number.
        int? reported = freshness != Freshness.Unknown ? component.Level.Value : null;
        bool known = reported.HasValue;
        bool charging = known && component.Charging.Value == true;

        bool underTen = inTenPercentSteps && reported == 0;

        IsKnown = known;
        Level01 = underTen ? 0.05 : reported is int p ? p / 100.0 : 0; // 0 only feeds the hidden/dashed gauge
        PercentText = underTen ? Strings.Battery_Percent_UnderTen : reported is int shown ? string.Format(CultureInfo.CurrentCulture, Strings.Battery_Percent_Format, shown.ToString(CultureInfo.InvariantCulture)) : "—";
        IsCharging = charging;
        IsStale = freshness == Freshness.Stale;
        IsLow = reported is int level && level <= LowThreshold;

        string age = IsStale && component.Level.ObservedAt is { } at ? Ago(now - at) : "";
        StatusText = !known ? Strings.Battery_Status_Unknown
            : IsStale ? age
            : charging ? Strings.Battery_Status_Charging
            : "";

        var parts = new List<string>();
        parts.Add(underTen ? Strings.Battery_Spoken_UnderTenPercent
            : reported is int spoken ? string.Format(CultureInfo.CurrentCulture, Strings.Battery_Spoken_Percent, spoken)
            : Strings.Battery_Spoken_Unknown);
        if (charging) parts.Add(Strings.Battery_Spoken_Charging);
        if (IsStale) parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Battery_Spoken_LastReported, age));
        if (isDemo) parts.Add(Strings.Battery_Spoken_DemoData);
        var tail = ", " + string.Join(", ", parts);
        if (tail != _automationTail)
        {
            _automationTail = tail;
            Raise(nameof(AutomationName));
        }
    }

    private static string Ago(TimeSpan span) => span.TotalMinutes < 1 ? Strings.Battery_Age_JustNow
        : span.TotalHours < 1 ? string.Format(CultureInfo.CurrentCulture, Strings.Battery_Age_Minutes, (int)span.TotalMinutes)
        : span.TotalHours < 48 ? string.Format(CultureInfo.CurrentCulture, Strings.Battery_Age_Hours, (int)span.TotalHours)
        : string.Format(CultureInfo.CurrentCulture, Strings.Battery_Age_Days, (int)span.TotalDays);
}
