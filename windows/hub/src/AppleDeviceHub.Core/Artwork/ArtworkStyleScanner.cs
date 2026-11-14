// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Artwork;

/// <summary>The palette blocks of an artwork's <c>&lt;style&gt;</c>.</summary>
internal sealed record ArtworkStyles(Dictionary<string, ArtColor> Defaults, List<ArtColorway> Colorways);

/// <summary>
/// A small hand-written scanner (not a CSS engine) for the only CSS the artwork format allows:
/// <c>svg { --token: #rrggbb; }</c> and <c>svg[data-colorway="id"] { --token: #rrggbb; }</c>.
/// </summary>
internal static class ArtworkStyleScanner
{
    public static ArtworkStyles Parse(string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        var defaults = new Dictionary<string, ArtColor>(StringComparer.Ordinal);
        var colorways = new List<ArtColorway>();
        var text = StripComments(css);
        var pos = 0;
        while (true)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }

            if (pos >= text.Length)
            {
                break;
            }

            var open = text.IndexOf('{', pos);
            if (open < 0)
            {
                throw new ArtworkFormatException($"Style block has text without a '{{' rule: '{text[pos..].Trim()}'.");
            }

            var close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                throw new ArtworkFormatException("Style block has a rule with no closing '}'.");
            }

            var selector = text[pos..open].Trim();
            var values = ParseDeclarations(selector, text[(open + 1)..close]);
            pos = close + 1;

            if (selector == "svg")
            {
                foreach (var (key, value) in values)
                {
                    defaults[key] = value;
                }

                continue;
            }

            var id = ParseColorwaySelector(selector);
            foreach (var existing in colorways)
            {
                if (existing.Id == id)
                {
                    throw new ArtworkFormatException($"Colourway '{id}' is declared twice.");
                }
            }

            colorways.Add(new ArtColorway(id, values));
        }

        return new ArtworkStyles(defaults, colorways);
    }

    private static string StripComments(string css)
    {
        var sb = new System.Text.StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new ArtworkFormatException("Style block has an unterminated comment.");
                }

                i = end + 2;
                continue;
            }

            sb.Append(css[i++]);
        }

        return sb.ToString();
    }

    private static string ParseColorwaySelector(string selector)
    {
        // svg[data-colorway="id"] (single or double quotes, optional spaces inside the brackets)
        const string head = "svg[";
        if (!selector.StartsWith(head, StringComparison.Ordinal) || !selector.EndsWith(']'))
        {
            throw new ArtworkFormatException($"Unsupported style selector '{selector}' (only 'svg' and 'svg[data-colorway=\"id\"]').");
        }

        var inner = selector[head.Length..^1].Trim();
        const string attr = "data-colorway";
        if (!inner.StartsWith(attr, StringComparison.Ordinal))
        {
            throw new ArtworkFormatException($"Unsupported style selector '{selector}'.");
        }

        var rest = inner[attr.Length..].TrimStart();
        if (!rest.StartsWith('='))
        {
            throw new ArtworkFormatException($"Unsupported style selector '{selector}'.");
        }

        rest = rest[1..].Trim();
        if (rest.Length < 3 || (rest[0] != '"' && rest[0] != '\'') || rest[^1] != rest[0])
        {
            throw new ArtworkFormatException($"Colourway selector '{selector}' needs a quoted id.");
        }

        var id = rest[1..^1];
        foreach (var ch in id)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            {
                throw new ArtworkFormatException($"Colourway id '{id}' may only use letters, digits, '-' and '_'.");
            }
        }

        return id;
    }

    private static Dictionary<string, ArtColor> ParseDeclarations(string selector, string body)
    {
        var result = new Dictionary<string, ArtColor>(StringComparer.Ordinal);
        foreach (var raw in body.Split(';'))
        {
            var decl = raw.Trim();
            if (decl.Length == 0)
            {
                continue;
            }

            var colon = decl.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                throw new ArtworkFormatException($"Style declaration '{decl}' in '{selector}' has no ':'.");
            }

            var name = decl[..colon].Trim();
            if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length < 3)
            {
                throw new ArtworkFormatException($"Style property '{name}' in '{selector}' is not a custom property (--name).");
            }

            var token = name[2..];
            foreach (var ch in token)
            {
                if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
                {
                    throw new ArtworkFormatException($"Token name '{name}' may only use letters, digits, '-' and '_'.");
                }
            }

            result[token] = ArtColor.Parse(decl[(colon + 1)..].Trim());
        }

        return result;
    }
}
