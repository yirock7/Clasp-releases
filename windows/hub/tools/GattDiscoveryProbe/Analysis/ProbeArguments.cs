namespace GattDiscoveryProbe.Analysis;

public enum ProbeCommandKind
{
    Scope,
    ListPaired,
    Live,
    Refused,
}

public sealed record ProbeOptions(string DeviceId, string? OutDir);

public sealed record ProbeArgumentResult(ProbeCommandKind Command, ProbeOptions? Options = null, string? Error = null);

/// <summary>Pure, case-sensitive command-line parser. It does not access Bluetooth or the file system.</summary>
public static class ProbeArguments
{
    public const string AuthorizeFlag = "--i-authorize-gatt-metadata-discovery";
    public const string ListPairedFlag = "--list-paired";

    public static ProbeArgumentResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
            return new ProbeArgumentResult(ProbeCommandKind.Scope);

        if (args.Count == 1 && string.Equals(args[0], ListPairedFlag, StringComparison.Ordinal))
            return new ProbeArgumentResult(ProbeCommandKind.ListPaired);

        string? deviceId = null;
        string? outDir = null;
        bool authorized = false;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case AuthorizeFlag:
                    if (authorized)
                        return Refused($"{AuthorizeFlag} was given more than once.");
                    authorized = true;
                    break;

                case "--device-id":
                    if (deviceId is not null)
                        return Refused("--device-id was given more than once.");
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return Refused("--device-id needs a paired Bluetooth LE device id.");
                    deviceId = args[++i];
                    break;

                case "--out":
                    if (outDir is not null)
                        return Refused("--out was given more than once.");
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return Refused("--out needs a folder path.");
                    outDir = args[++i];
                    break;

                default:
                    // Do not echo arbitrary argument values: they may contain a Windows device id.
                    return Refused("An unknown or misplaced argument was given.");
            }
        }

        if (!authorized)
            return Refused($"The exact flag {AuthorizeFlag} is required for a live connection.");
        if (deviceId is null)
            return Refused($"--device-id <id> is required with {AuthorizeFlag}.");

        return new ProbeArgumentResult(ProbeCommandKind.Live, new ProbeOptions(deviceId, outDir));
    }

    private static ProbeArgumentResult Refused(string error)
        => new(ProbeCommandKind.Refused, Error: error);
}
