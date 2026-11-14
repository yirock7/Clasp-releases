// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Artwork;

public readonly record struct ArtPoint(double X, double Y);

public abstract record PathSegment(ArtPoint To);

public sealed record LineSegment(ArtPoint To) : PathSegment(To);

public sealed record CubicSegment(ArtPoint Control1, ArtPoint Control2, ArtPoint To) : PathSegment(To);

public sealed record QuadraticSegment(ArtPoint Control, ArtPoint To) : PathSegment(To);

public sealed record ArcSegment(double Rx, double Ry, double XAxisRotation, bool LargeArc, bool Sweep, ArtPoint To) : PathSegment(To);

/// <summary>One sub-path: a start point, absolute segments and whether it was closed with Z.</summary>
public sealed record PathFigure(ArtPoint Start, bool Closed, IReadOnlyList<PathSegment> Segments);

/// <summary>
/// Parses SVG path data (M L H V C S Q T A Z, absolute and relative, implicit repeats, compact arc flags)
/// into figures with absolute coordinates. S and T are expanded to full cubic and quadratic segments.
/// </summary>
public static class SvgPathParser
{
    private const string Commands = "MmLlHhVvCcSsQqTtAaZz";

    public static IReadOnlyList<PathFigure> Parse(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = new SvgReader(data);
        var figures = new List<PathFigure>();
        List<PathSegment>? segments = null;
        ArtPoint start = default;
        double x = 0, y = 0;
        var cmd = '\0';
        var prev = '\0'; // previous command, upper-cased
        double lastCx = 0, lastCy = 0; // last control point for S/T reflection

        while (true)
        {
            r.SkipSpaces();
            if (r.AtEnd)
            {
                break;
            }

            var c = r.Peek;
            if (c is not (>= '0' and <= '9' or '.' or '-' or '+' or ','))
            {
                if (Commands.IndexOf(c, StringComparison.Ordinal) < 0)
                {
                    throw new ArtworkFormatException($"Unknown path command '{c}' at position {r.Pos} in '{data}'.");
                }

                cmd = c;
                r.Pos++;
                if (figures.Count == 0 && segments is null && cmd is not ('M' or 'm'))
                {
                    throw new ArtworkFormatException($"Path data must start with M (found '{cmd}') in '{data}'.");
                }

                if (cmd is 'Z' or 'z')
                {
                    if (segments is not null)
                    {
                        figures.Add(new PathFigure(start, true, segments));
                        segments = null;
                        x = start.X;
                        y = start.Y;
                    }

                    prev = 'Z';
                    continue;
                }
            }
            else
            {
                if (cmd == '\0' || cmd is 'Z' or 'z')
                {
                    throw new ArtworkFormatException($"Unexpected number at position {r.Pos} in '{data}'.");
                }

                // Extra coordinate pairs after M/m are implicit L/l.
                if (cmd == 'M')
                {
                    cmd = 'L';
                }
                else if (cmd == 'm')
                {
                    cmd = 'l';
                }
            }

            var rel = char.IsAsciiLetterLower(cmd);
            var upper = char.ToUpperInvariant(cmd);
            var ox = rel ? x : 0;
            var oy = rel ? y : 0;

            if (upper == 'M')
            {
                if (segments is not null)
                {
                    figures.Add(new PathFigure(start, false, segments));
                }

                x = ox + r.ReadNumber();
                y = oy + r.ReadNumber();
                start = new ArtPoint(x, y);
                segments = [];
                prev = 'M';
                continue;
            }

            if (segments is null)
            {
                // Drawing after Z starts a new figure at the previous start point.
                start = new ArtPoint(x, y);
                segments = [];
            }

            switch (upper)
            {
                case 'L':
                    x = ox + r.ReadNumber();
                    y = oy + r.ReadNumber();
                    segments.Add(new LineSegment(new ArtPoint(x, y)));
                    break;
                case 'H':
                    x = ox + r.ReadNumber();
                    segments.Add(new LineSegment(new ArtPoint(x, y)));
                    break;
                case 'V':
                    y = oy + r.ReadNumber();
                    segments.Add(new LineSegment(new ArtPoint(x, y)));
                    break;
                case 'C':
                {
                    var c1 = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    var c2 = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    var to = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    segments.Add(new CubicSegment(c1, c2, to));
                    lastCx = c2.X;
                    lastCy = c2.Y;
                    x = to.X;
                    y = to.Y;
                    break;
                }

                case 'S':
                {
                    var c1 = prev is 'C' or 'S' ? new ArtPoint((2 * x) - lastCx, (2 * y) - lastCy) : new ArtPoint(x, y);
                    var c2 = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    var to = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    segments.Add(new CubicSegment(c1, c2, to));
                    lastCx = c2.X;
                    lastCy = c2.Y;
                    x = to.X;
                    y = to.Y;
                    break;
                }

                case 'Q':
                {
                    var q = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    var to = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    segments.Add(new QuadraticSegment(q, to));
                    lastCx = q.X;
                    lastCy = q.Y;
                    x = to.X;
                    y = to.Y;
                    break;
                }

                case 'T':
                {
                    var q = prev is 'Q' or 'T' ? new ArtPoint((2 * x) - lastCx, (2 * y) - lastCy) : new ArtPoint(x, y);
                    var to = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    segments.Add(new QuadraticSegment(q, to));
                    lastCx = q.X;
                    lastCy = q.Y;
                    x = to.X;
                    y = to.Y;
                    break;
                }

                case 'A':
                {
                    var rx = r.ReadNumber();
                    var ry = r.ReadNumber();
                    var rot = r.ReadNumber();
                    var large = r.ReadFlag();
                    var sweep = r.ReadFlag();
                    var to = new ArtPoint(ox + r.ReadNumber(), oy + r.ReadNumber());
                    segments.Add(new ArcSegment(rx, ry, rot, large, sweep, to));
                    x = to.X;
                    y = to.Y;
                    break;
                }

                default:
                    throw new ArtworkFormatException($"Unknown path command '{cmd}' in '{data}'.");
            }

            prev = upper;
        }

        if (segments is not null)
        {
            figures.Add(new PathFigure(start, false, segments));
        }

        return figures;
    }
}
