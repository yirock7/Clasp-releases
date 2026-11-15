// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace AppleDeviceHub.Core.Artwork;

/// <summary>Thrown when a Clasp artwork SVG uses something outside the supported subset or is malformed.</summary>
public sealed class ArtworkFormatException : Exception
{
    public ArtworkFormatException()
    {
    }

    public ArtworkFormatException(string message)
        : base(message)
    {
    }

    public ArtworkFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A straight-alpha 8-bit colour.</summary>
public readonly record struct ArtColor(byte A, byte R, byte G, byte B)
{
    /// <summary>Parses <c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>.</summary>
    public static bool TryParse(string? text, out ArtColor color)
    {
        color = default;
        if (text is null)
        {
            return false;
        }

        var s = text.Trim();
        if (s.Length is not (4 or 7 or 9) || s[0] != '#')
        {
            return false;
        }

        var hex = s.AsSpan(1);
        for (var i = 0; i < hex.Length; i++)
        {
            if (!char.IsAsciiHexDigit(hex[i]))
            {
                return false;
            }
        }

        if (hex.Length == 3)
        {
            var r = Convert.ToByte(new string(hex[0], 2), 16);
            var g = Convert.ToByte(new string(hex[1], 2), 16);
            var b = Convert.ToByte(new string(hex[2], 2), 16);
            color = new ArtColor(255, r, g, b);
            return true;
        }

        var rr = byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var gg = byte.Parse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var bb = byte.Parse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var aa = hex.Length == 8 ? byte.Parse(hex.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)255;
        color = new ArtColor(aa, rr, gg, bb);
        return true;
    }

    public static ArtColor Parse(string text) =>
        TryParse(text, out var c) ? c : throw new ArtworkFormatException($"'{text}' is not a colour (use #rgb, #rrggbb or #rrggbbaa).");
}

/// <summary>A colour that is either a literal or the name of a palette token (<c>var(--name)</c>, stored without the dashes).</summary>
public readonly record struct ArtColorRef(ArtColor Literal, string? Token)
{
    public bool IsToken => Token is not null;

    public static ArtColorRef FromLiteral(ArtColor color) => new(color, null);

    public static ArtColorRef FromToken(string token) => new(default, token);

    public ArtColor Resolve(ArtworkPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return Token is null ? Literal : palette.Resolve(Token);
    }
}

public enum ArtPaintKind
{
    None = 0,
    Solid = 1,
    Gradient = 2,
}

/// <summary>A fill or stroke: nothing, a solid colour (literal or token), or a reference to a gradient.</summary>
public readonly record struct ArtPaint(ArtPaintKind Kind, ArtColorRef Color, string? GradientId)
{
    public static ArtPaint None => new(ArtPaintKind.None, default, null);

    public static ArtPaint Solid(ArtColorRef color) => new(ArtPaintKind.Solid, color, null);

    public static ArtPaint Gradient(string id) => new(ArtPaintKind.Gradient, default, id);
}

/// <summary>Token name to colour. Token names are stored without the leading <c>--</c>.</summary>
public sealed class ArtworkPalette
{
    private readonly IReadOnlyDictionary<string, ArtColor> _colors;

    public ArtworkPalette(IReadOnlyDictionary<string, ArtColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        _colors = colors;
    }

    public IReadOnlyDictionary<string, ArtColor> Colors => _colors;

    public bool TryGet(string token, out ArtColor color) => _colors.TryGetValue(token, out color);

    public ArtColor Resolve(string token) =>
        _colors.TryGetValue(token, out var c) ? c : throw new ArtworkFormatException($"Colour token '--{token}' is not in the palette.");

    public ArtColor Resolve(ArtColorRef reference) => reference.Resolve(this);

    /// <summary>This palette with the given tokens replaced.</summary>
    public ArtworkPalette Overlay(IReadOnlyDictionary<string, ArtColor> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var merged = new Dictionary<string, ArtColor>(_colors, StringComparer.Ordinal);
        foreach (var (key, value) in overrides)
        {
            merged[key] = value;
        }

        return new ArtworkPalette(merged);
    }
}

/// <summary>A named set of token overrides (<c>svg[data-colorway="id"]</c>).</summary>
public sealed record ArtColorway(string Id, IReadOnlyDictionary<string, ArtColor> Overrides)
{
    /// <summary>The id title-cased with hyphens turned into spaces ("electric-orange" becomes "Electric Orange").</summary>
    public string DisplayName
    {
        get
        {
            var parts = Id.Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
            }

            return string.Join(' ', parts);
        }
    }
}

/// <summary>2D affine matrix: x' = a*x + c*y + e, y' = b*x + d*y + f.</summary>
public readonly record struct ArtMatrix(double A, double B, double C, double D, double E, double F)
{
    public static ArtMatrix Identity => new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity => this == Identity;

    public static ArtMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static ArtMatrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public static ArtMatrix Rotate(double degrees)
    {
        var r = degrees * Math.PI / 180;
        var cos = Math.Cos(r);
        var sin = Math.Sin(r);
        return new ArtMatrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>This matrix times <paramref name="other"/>: <paramref name="other"/> is applied to a point first.</summary>
    public ArtMatrix Multiply(ArtMatrix other) => new(
        (A * other.A) + (C * other.B),
        (B * other.A) + (D * other.B),
        (A * other.C) + (C * other.D),
        (B * other.C) + (D * other.D),
        (A * other.E) + (C * other.F) + E,
        (B * other.E) + (D * other.F) + F);

    public static ArtMatrix operator *(ArtMatrix left, ArtMatrix right) => left.Multiply(right);

    public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>Parses an SVG transform list: translate, scale, rotate and matrix, in sequence. Empty text is the identity.</summary>
    public static ArtMatrix Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var r = new SvgReader(text);
        var result = Identity;
        while (true)
        {
            r.SkipSeparators();
            if (r.AtEnd)
            {
                return result;
            }

            var nameStart = r.Pos;
            while (!r.AtEnd && char.IsAsciiLetter(r.Peek))
            {
                r.Pos++;
            }

            var name = text[nameStart..r.Pos];
            r.SkipSpaces();
            if (name.Length == 0 || r.AtEnd || r.Peek != '(')
            {
                throw new ArtworkFormatException($"Invalid transform '{text}' at position {nameStart}.");
            }

            r.Pos++;
            var args = new List<double>();
            while (true)
            {
                r.SkipSpaces();
                if (r.AtEnd)
                {
                    throw new ArtworkFormatException($"Invalid transform '{text}': missing ')'.");
                }

                if (r.Peek == ')')
                {
                    r.Pos++;
                    break;
                }

                args.Add(r.ReadNumber());
            }

            ArtMatrix step;
            switch (name, args.Count)
            {
                case ("translate", 1):
                    step = Translate(args[0], 0);
                    break;
                case ("translate", 2):
                    step = Translate(args[0], args[1]);
                    break;
                case ("scale", 1):
                    step = Scale(args[0], args[0]);
                    break;
                case ("scale", 2):
                    step = Scale(args[0], args[1]);
                    break;
                case ("rotate", 1):
                    step = Rotate(args[0]);
                    break;
                case ("rotate", 3):
                    step = Translate(args[1], args[2]) * Rotate(args[0]) * Translate(-args[1], -args[2]);
                    break;
                case ("matrix", 6):
                    step = new ArtMatrix(args[0], args[1], args[2], args[3], args[4], args[5]);
                    break;
                default:
                    throw new ArtworkFormatException($"Unsupported or malformed transform function '{name}' with {args.Count} argument(s) in '{text}'.");
            }

            result *= step;
        }
    }
}

/// <summary>Shared scanner for the number lists of path data and transforms.</summary>
internal sealed class SvgReader(string text)
{
    public int Pos { get; set; }

    public bool AtEnd => Pos >= text.Length;

    public char Peek => text[Pos];

    public void SkipSpaces()
    {
        while (Pos < text.Length && char.IsWhiteSpace(text[Pos]))
        {
            Pos++;
        }
    }

    /// <summary>Skips whitespace and at most one comma.</summary>
    public void SkipSeparators()
    {
        SkipSpaces();
        if (Pos < text.Length && text[Pos] == ',')
        {
            Pos++;
            SkipSpaces();
        }
    }

    public double ReadNumber()
    {
        SkipSeparators();
        var start = Pos;
        var p = Pos;
        if (p < text.Length && (text[p] is '+' or '-'))
        {
            p++;
        }

        var digits = 0;
        while (p < text.Length && char.IsAsciiDigit(text[p]))
        {
            p++;
            digits++;
        }

        if (p < text.Length && text[p] == '.')
        {
            p++;
            while (p < text.Length && char.IsAsciiDigit(text[p]))
            {
                p++;
                digits++;
            }
        }

        if (digits == 0)
        {
            throw new ArtworkFormatException($"Expected a number at position {start} in '{text}'.");
        }

        if (p < text.Length && (text[p] is 'e' or 'E'))
        {
            var q = p + 1;
            if (q < text.Length && (text[q] is '+' or '-'))
            {
                q++;
            }

            if (q < text.Length && char.IsAsciiDigit(text[q]))
            {
                while (q < text.Length && char.IsAsciiDigit(text[q]))
                {
                    q++;
                }

                p = q;
            }
        }

        Pos = p;
        return double.Parse(text.AsSpan(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads one arc flag, which may be written with no separator after it.</summary>
    public bool ReadFlag()
    {
        SkipSeparators();
        if (AtEnd || text[Pos] is not ('0' or '1'))
        {
            throw new ArtworkFormatException($"Expected an arc flag (0 or 1) at position {Pos} in '{text}'.");
        }

        return text[Pos++] == '1';
    }
}
