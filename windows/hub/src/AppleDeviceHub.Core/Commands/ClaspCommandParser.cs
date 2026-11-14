namespace AppleDeviceHub.Core.Commands;

/// <summary>Outcome of parsing: either a command, or null with a short reason for the log.</summary>
public sealed record ClaspCommandParseResult(ClaspCommand? Command, string? Reason)
{
    public bool Succeeded => Command is not null;

    public static ClaspCommandParseResult Ok(ClaspCommand command) => new(command, null);
    public static ClaspCommandParseResult Fail(string reason) => new(null, reason);
}

/// <summary>
/// Turns outside input into a <see cref="ClaspCommand"/>. Allow-list only: anything that is not
/// exactly one of the known links or names is rejected. Query strings and fragments are ignored
/// (never interpreted), percent-encoding is rejected, and input is length-limited, because the
/// text can come from any program or web page on the PC.
/// </summary>
public static class ClaspCommandParser
{
    /// <summary>Longest input that is even looked at.</summary>
    public const int MaxInputLength = 128;

    private const string Scheme = "clasp:";

    private static readonly Dictionary<string, ClaspCommand> LinkPaths = new(StringComparer.Ordinal)
    {
        ["connect"] = ClaspCommand.Connect,
        ["disconnect"] = ClaspCommand.Disconnect,
        ["output/airpods"] = ClaspCommand.OutputAirPods,
        ["output/previous"] = ClaspCommand.OutputPrevious,
        ["fix"] = ClaspCommand.FixConnection,
        ["show"] = ClaspCommand.ShowFlyout,
        ["settings"] = ClaspCommand.ShowSettings,
        ["playpause"] = ClaspCommand.PlayPause,
    };

    /// <summary>Parses a <c>clasp://...</c> link (case-insensitive, trailing slash tolerated).</summary>
    public static ClaspCommandParseResult ParseUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return ClaspCommandParseResult.Fail("Empty link.");
        var text = uri.Trim();
        if (text.Length > MaxInputLength) return ClaspCommandParseResult.Fail("Link is too long.");
        if (!text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return ClaspCommandParseResult.Fail("Not a clasp: link.");

        var rest = text[Scheme.Length..];
        if (!rest.StartsWith("//", StringComparison.Ordinal)) return ClaspCommandParseResult.Fail("Link must start with clasp://.");
        rest = rest[2..];

        // The query string and fragment are dropped unread; only the command part is checked.
        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0) rest = rest[..cut];
        if (HasUnsafeCharacters(rest)) return ClaspCommandParseResult.Fail("Link contains characters that are not allowed.");
        rest = rest.TrimEnd('/').ToLowerInvariant();

        return LinkPaths.TryGetValue(rest, out var command)
            ? ClaspCommandParseResult.Ok(command)
            : ClaspCommandParseResult.Fail("Unknown command.");
    }

    /// <summary>Parses a command name such as <c>fix</c> or <c>output-airpods</c> (case-insensitive).</summary>
    public static ClaspCommandParseResult ParseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ClaspCommandParseResult.Fail("Empty command name.");
        var text = name.Trim();
        if (text.Length > MaxInputLength) return ClaspCommandParseResult.Fail("Command name is too long.");
        if (HasUnsafeCharacters(text)) return ClaspCommandParseResult.Fail("Command name contains characters that are not allowed.");
        foreach (var command in Enum.GetValues<ClaspCommand>())
            if (string.Equals(ClaspCommandText.CanonicalName(command), text, StringComparison.OrdinalIgnoreCase))
                return ClaspCommandParseResult.Ok(command);
        return ClaspCommandParseResult.Fail("Unknown command.");
    }

    /// <summary>
    /// Looks for one <c>--command=&lt;name&gt;</c> or <c>--uri=&lt;clasp link&gt;</c> argument.
    /// Returns null (no failure) when neither is present; a present but invalid one is a failure.
    /// If several are present only the first is used.
    /// </summary>
    public static ClaspCommandParseResult? ParseArgs(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            if (arg is null) continue;
            if (arg.StartsWith("--command=", StringComparison.OrdinalIgnoreCase))
                return ParseName(arg["--command=".Length..]);
            if (arg.StartsWith("--uri=", StringComparison.OrdinalIgnoreCase))
                return ParseUri(arg["--uri=".Length..]);
        }
        return null;
    }

    /// <summary>
    /// True when the arguments carry a command or link. The app should then ignore its other
    /// startup arguments (for example --transport), because a link can smuggle extra text into
    /// the command line.
    /// </summary>
    public static bool IsCommandLaunch(IEnumerable<string> args) => ParseArgs(args) is not null;

    // Control characters, whitespace inside the text, quotes, backslashes and percent-encoding
    // never appear in a legitimate link.
    private static bool HasUnsafeCharacters(string text)
    {
        foreach (var c in text)
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '"' or '\'' or '\\' or '%' or '`' or '|' or '&' or '^' or '<' or '>')
                return true;
        return false;
    }
}
