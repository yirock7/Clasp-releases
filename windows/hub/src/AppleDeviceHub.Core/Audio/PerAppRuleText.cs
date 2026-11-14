using System.Text;

namespace AppleDeviceHub.Core.Audio;

/// <summary>
/// Stores rules as plain text, one per line, so a single string setting can hold them:
/// "spotify.exe=airpods", "game.exe=speakers:ENDPOINTID", "chrome.exe=default". Blank lines and
/// lines starting with '#' are ignored; malformed lines are skipped and reported.
/// </summary>
public static class PerAppRuleText
{
    public static IReadOnlyList<PerAppRule> Parse(string? text, out IReadOnlyList<string> rejectedLines)
    {
        var rules = new List<PerAppRule>();
        var rejected = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var rule = ParseLine(line);
            if (rule is null) rejected.Add(line); else rules.Add(rule);
        }
        rejectedLines = rejected;
        return rules;
    }

    public static string Format(IEnumerable<PerAppRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var builder = new StringBuilder();
        foreach (var rule in rules)
        {
            builder.Append(rule.ExecutableName).Append('=');
            builder.Append(rule.Target switch
            {
                PerAppTargetKind.AirPods => "airpods",
                PerAppTargetKind.Speakers => "speakers:" + rule.EndpointId,
                _ => "default",
            });
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static PerAppRule? ParseLine(string line)
    {
        var split = line.IndexOf('=', StringComparison.Ordinal);
        if (split <= 0) return null;
        var name = line[..split].Trim();
        var target = line[(split + 1)..].Trim();
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0) return null;
        if (target.Equals("airpods", StringComparison.OrdinalIgnoreCase)) return new(name, PerAppTargetKind.AirPods);
        if (target.Equals("default", StringComparison.OrdinalIgnoreCase)) return new(name, PerAppTargetKind.Default);
        const string speakers = "speakers:";
        if (target.StartsWith(speakers, StringComparison.OrdinalIgnoreCase))
        {
            var id = target[speakers.Length..].Trim();
            return id.Length == 0 ? null : new(name, PerAppTargetKind.Speakers, id);
        }
        return null;
    }
}
