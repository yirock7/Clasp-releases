namespace AppleDeviceHub.Core.Battery;

/// <summary>A narrow marker standing in for a long stretch with no readings, in plot x.</summary>
public sealed record ChartBreak(double X1, double X2);

/// <summary>One stretch of continuous readings (no silence longer than the gap limit), in plot x.</summary>
public sealed record ChartSession(DateTimeOffset From, DateTimeOffset To, double X1, double X2);

/// <summary>
/// Maps time to plot x for the history chart. When enough of the window is silence (gaps longer than the
/// line-break limit, where the chart draws nothing anyway), those gaps shrink to fixed-width break markers and the
/// sessions that hold data share the width; each session keeps a linear time scale inside itself and has a minimum
/// width so a few minutes of data stay readable. When the data is dense the map is a plain linear one.
/// </summary>
public sealed class ChartTimeMap
{
    /// <summary>Width of one break marker.</summary>
    public const double BreakWidth = 14;

    /// <summary>Narrowest a session is drawn, however short it was.</summary>
    public const double MinSessionWidth = 28;

    /// <summary>Gaps are compressed only when they make up at least this share of the window.</summary>
    public const double CompressShare = 0.6;

    private enum Kind { Active, Idle, Break }

    private readonly record struct Piece(Kind Kind, DateTimeOffset T0, DateTimeOffset T1, double X0, double X1);

    private readonly List<Piece> _pieces;
    private readonly DateTimeOffset _start;
    private readonly DateTimeOffset _end;

    private ChartTimeMap(List<Piece> pieces, DateTimeOffset start, DateTimeOffset end, bool compressed,
        IReadOnlyList<ChartBreak> breaks, IReadOnlyList<ChartSession> sessions)
    {
        _pieces = pieces;
        _start = start;
        _end = end;
        IsCompressed = compressed;
        Breaks = breaks;
        Sessions = sessions;
    }

    /// <summary>True when long gaps were shrunk to break markers.</summary>
    public bool IsCompressed { get; }

    public IReadOnlyList<ChartBreak> Breaks { get; }

    /// <summary>The stretches with readings; empty for a linear map.</summary>
    public IReadOnlyList<ChartSession> Sessions { get; }

    /// <summary>The plot x of <paramref name="t"/>; times outside the window are clamped to its edges.</summary>
    public double X(DateTimeOffset t)
    {
        if (t < _start) t = _start;
        if (t > _end) t = _end;
        // Readings sit on sessions, so look at those first; a boundary shared with a break belongs to the session.
        foreach (bool breaks in new[] { false, true })
            foreach (var p in _pieces)
            {
                if ((p.Kind == Kind.Break) != breaks || t < p.T0 || t > p.T1) continue;
                if (p.T1 == p.T0) return (p.X0 + p.X1) / 2;
                return p.X0 + (t - p.T0).TotalSeconds / (p.T1 - p.T0).TotalSeconds * (p.X1 - p.X0);
            }
        return _pieces[^1].X1;
    }

    /// <summary>
    /// Builds the map. <paramref name="ordered"/> must be sorted and not after <paramref name="end"/>; a silence
    /// longer than <paramref name="maxGap"/> is a gap (the same limit at which the chart breaks its lines).
    /// </summary>
    public static ChartTimeMap Create(IReadOnlyList<BatterySample> ordered, DateTimeOffset start, DateTimeOffset end,
        double width, TimeSpan maxGap, bool allowCompression = true)
    {
        var spans = new List<(DateTimeOffset From, DateTimeOffset To)>();
        (DateTimeOffset From, DateTimeOffset To)? cur = null;
        foreach (var s in ordered)
        {
            if (cur is null) cur = (s.Time, s.Time);
            else if (s.Time - cur.Value.To > maxGap) { spans.Add(cur.Value); cur = (s.Time, s.Time); }
            else cur = (cur.Value.From, s.Time);
        }
        if (cur is { } c) spans.Add((c.From, end - c.To <= maxGap ? end : c.To));
        spans = spans.Where(s => s.To >= start).Select(s => (s.From < start ? start : s.From, s.To)).ToList();

        var raw = new List<(Kind Kind, DateTimeOffset T0, DateTimeOffset T1)>();
        var cursor = start;
        foreach (var (from, to) in spans)
        {
            if (from > cursor) raw.Add((from - cursor > maxGap ? Kind.Break : Kind.Idle, cursor, from));
            raw.Add((Kind.Active, from, to));
            cursor = to;
        }
        if (end > cursor) raw.Add((end - cursor > maxGap ? Kind.Break : Kind.Idle, cursor, end));

        double total = (end - start).TotalSeconds;
        double breakSeconds = raw.Where(r => r.Kind == Kind.Break).Sum(r => (r.T1 - r.T0).TotalSeconds);
        if (!allowCompression || spans.Count == 0 || total <= 0 || breakSeconds < CompressShare * total)
            return Linear(start, end, width);

        int breakCount = raw.Count(r => r.Kind == Kind.Break);
        int activeCount = raw.Count(r => r.Kind == Kind.Active);
        double breakWidth = Math.Min(BreakWidth, 0.3 * width / breakCount);
        double available = width - breakCount * breakWidth;
        double min = Math.Min(MinSessionWidth, available / activeCount);

        var weight = raw.Select(r => r.Kind == Kind.Break ? 0 : (r.T1 - r.T0).TotalSeconds).ToArray();
        var fixedWidth = new double?[raw.Count];
        for (bool changed = true; changed;)
        {
            changed = false;
            double room = available - fixedWidth.Sum(f => f ?? 0);
            double weights = Enumerable.Range(0, raw.Count).Where(i => raw[i].Kind != Kind.Break && fixedWidth[i] is null).Sum(i => weight[i]);
            for (int i = 0; i < raw.Count; i++)
            {
                if (raw[i].Kind != Kind.Active || fixedWidth[i] is not null) continue;
                double share = weights > 0 ? weight[i] / weights * room : 0;
                if (share < min) { fixedWidth[i] = min; changed = true; break; }
            }
        }
        double fixedSum = fixedWidth.Sum(f => f ?? 0);
        double openWeight = Enumerable.Range(0, raw.Count).Where(i => raw[i].Kind != Kind.Break && fixedWidth[i] is null).Sum(i => weight[i]);
        var widths = new double[raw.Count];
        for (int i = 0; i < raw.Count; i++)
            widths[i] = raw[i].Kind == Kind.Break ? breakWidth
                : fixedWidth[i] ?? (openWeight > 0 ? weight[i] / openWeight * (available - fixedSum) : 0);
        // An unused remainder: stretch the non-break pieces so the plot is filled exactly.
        double content = Enumerable.Range(0, raw.Count).Where(i => raw[i].Kind != Kind.Break).Sum(i => widths[i]);
        if (content > 0)
            for (int i = 0; i < raw.Count; i++)
                if (raw[i].Kind != Kind.Break) widths[i] *= available / content;

        var pieces = new List<Piece>();
        var breaks = new List<ChartBreak>();
        var sessions = new List<ChartSession>();
        double x = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            var piece = new Piece(raw[i].Kind, raw[i].T0, raw[i].T1, x, x + widths[i]);
            pieces.Add(piece);
            if (piece.Kind == Kind.Break) breaks.Add(new ChartBreak(piece.X0, piece.X1));
            else if (piece.Kind == Kind.Active) sessions.Add(new ChartSession(piece.T0, piece.T1, piece.X0, piece.X1));
            x += widths[i];
        }
        return new ChartTimeMap(pieces, start, end, true, breaks, sessions);
    }

    private static ChartTimeMap Linear(DateTimeOffset start, DateTimeOffset end, double width)
        => new(new List<Piece> { new(Kind.Idle, start, end, 0, width) }, start, end, false, Array.Empty<ChartBreak>(), Array.Empty<ChartSession>());
}
