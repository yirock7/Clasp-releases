using System.Globalization;

namespace AppleDeviceHub.Core.Updates;

/// <summary>
/// A release version: exactly three numbers, as in the <c>clasp-v0.1.3</c> release tags and the
/// installer's <c>-Version</c>. Anything else (suffixes, four parts) is rejected rather than guessed.
/// </summary>
public readonly record struct ClaspVersion(int Major, int Minor, int Patch) : IComparable<ClaspVersion>
{
    public const string TagPrefix = "clasp-v";

    public static bool TryParse(string? text, out ClaspVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length is 0 or > 6 || !parts[i].All(char.IsAsciiDigit)) return false;
            numbers[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new ClaspVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>Parses a release tag such as <c>clasp-v0.1.3</c>.</summary>
    public static bool TryParseTag(string? tag, out ClaspVersion version)
    {
        version = default;
        return tag is not null
            && tag.StartsWith(TagPrefix, StringComparison.Ordinal)
            && TryParse(tag[TagPrefix.Length..], out version);
    }

    /// <summary>
    /// The running app's version from its informational version (for example <c>0.1.4</c> or
    /// <c>0.1.4+abc123</c> with build metadata). Null for builds without a release version.
    /// </summary>
    public static ClaspVersion? FromInformationalVersion(string? informational)
    {
        if (informational is null) return null;
        var core = informational.Split('+', 2)[0];
        return TryParse(core, out var version) && version != default ? version : null;
    }

    public int CompareTo(ClaspVersion other)
        => Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);

    public static bool operator >(ClaspVersion left, ClaspVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(ClaspVersion left, ClaspVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(ClaspVersion left, ClaspVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(ClaspVersion left, ClaspVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
