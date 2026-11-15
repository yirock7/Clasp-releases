using System.Globalization;

namespace BleObservationProbe.Analysis;

/// <summary>Validated command line for an authorised run.</summary>
public sealed record ProbeOptions(int Seconds, string? OutDir);

/// <summary>
/// Pure argument parsing. Without <see cref="AuthorizeFlag"/> the caller prints the scope and
/// exits; with it, every other argument must be valid or the run is refused.
/// </summary>
public static class ProbeArguments
{
    public const string AuthorizeFlag = "--i-authorize-a-passive-scan";
    public const int MinSeconds = 1;
    public const int MaxSeconds = 120;

    public static bool IsAuthorized(IReadOnlyList<string> args)
        => args.Contains(AuthorizeFlag, StringComparer.Ordinal);

    /// <summary>
    /// True for a plain request to see the scope (no arguments, or only a help switch). Any other
    /// arguments without the exact, case-sensitive flag are a refused run (exit 2), so a mistyped
    /// flag such as "--i-authorise-..." never looks like success (EF-IR review F7).
    /// </summary>
    public static bool IsScopeRequest(IReadOnlyList<string> args)
        => args.Count == 0 || (args.Count == 1 && args[0] is "--help" or "-h" or "/?");

    /// <summary>Parse an authorised command line. Returns options, or an error and null options.</summary>
    public static (ProbeOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        int? seconds = null;
        string? outDir = null;
        bool authorized = false;

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            switch (a)
            {
                case AuthorizeFlag:
                    if (authorized) return (null, $"{AuthorizeFlag} was given more than once.");
                    authorized = true;
                    break;
                case "--seconds":
                    if (seconds is not null) return (null, "--seconds was given more than once.");
                    if (i + 1 >= args.Count) return (null, "--seconds needs a value.");
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                        return (null, $"--seconds value '{args[i]}' is not a whole number.");
                    seconds = n;
                    break;
                case "--out":
                    if (outDir is not null) return (null, "--out was given more than once.");
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return (null, "--out needs a folder path.");
                    outDir = args[++i];
                    break;
                default:
                    return (null, $"Unknown argument '{a}'.");
            }
        }

        if (!authorized) return (null, $"{AuthorizeFlag} is required for a live run.");
        if (seconds is null) return (null, $"--seconds N is required with {AuthorizeFlag} ({MinSeconds} <= N <= {MaxSeconds}).");
        if (seconds < MinSeconds || seconds > MaxSeconds)
            return (null, $"--seconds must be between {MinSeconds} and {MaxSeconds} (got {seconds}).");
        return (new ProbeOptions(seconds.Value, outDir), null);
    }
}
