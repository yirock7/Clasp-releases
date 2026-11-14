using AppleDeviceHub.Core.Engine;

namespace AppleDeviceHub.Core.AirPods;

public enum TransportKind
{
    Demo = 0,

    /// <summary>The legacy upstream NTPods daemon (removed from this repository). Not supported: use <see cref="Hardware"/>.</summary>
    NtpodsDaemon = 1,

    /// <summary>The restricted clasp-engine over its versioned, identity-checked pipe.</summary>
    Hardware = 2,

    /// <summary>
    /// Driver-free, read-only observation of nearby Apple proximity broadcasts
    /// (<c>--transport=ble</c>). Opt-in only by launch flag; listening starts automatically while nearby detection is on (the default).
    /// </summary>
    Broadcast = 3,
}

/// <param name="Kind">What was asked for.</param>
/// <param name="IsAvailable">False when the request cannot be honoured; <paramref name="Explanation"/> says why.</param>
/// <param name="Explanation">One readable sentence.</param>
/// <param name="Hardware">The engine configuration, only for an available <see cref="TransportKind.Hardware"/> choice.</param>
public sealed record TransportChoice(TransportKind Kind, bool IsAvailable, string Explanation, ClaspEngineOptions? Hardware = null)
{
    /// <summary>Only an explicit, valid demo choice (or no choice) may show demo data.</summary>
    public bool IsDemo => Kind == TransportKind.Demo && IsAvailable;
}

/// <summary>
/// Picks the AirPods transport from the command line. Demo is the default. Hardware is only
/// ever an explicit request:
/// <c>--transport=hardware --engine-path=&lt;full path&gt; [--engine-sha256=&lt;hex&gt;] --device=&lt;dev-...&gt; [--session=observe|control]</c>.
/// A hardware request that is incomplete or invalid is refused with the reason; the caller must
/// then show a hardware-unavailable state, never the demo. Nothing here starts a process or
/// searches PATH. <c>--transport=ble</c> selects read-only broadcast observation, which listens
/// automatically while nearby detection is on (the default).
/// </summary>
public static class TransportSelection
{
    private static readonly string[] HardwareOnlyArgumentNames = ["--engine-path", "--engine-sha256", "--device", "--session"];

    private static readonly string[] TransportArgumentNames = ["--transport", .. HardwareOnlyArgumentNames];

    public static TransportChoice FromArgs(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var list = args.ToList();

        // Strict: a transport or hardware argument in any form other than name=value (for
        // example "--transport hardware") is refused, never silently read as "start the demo".
        foreach (var arg in list)
        {
            foreach (var name in TransportArgumentNames)
            {
                if (arg.StartsWith(name, StringComparison.OrdinalIgnoreCase) && !arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return new TransportChoice(TransportKind.Hardware, false,
                        $"Unrecognised argument '{arg}': use the {name}=<value> form (for example --transport=hardware).");
            }
        }

        var transport = Value(list, "--transport");
        bool hardwareArguments = HardwareOnlyArgumentNames.Any(name => Value(list, name) is not null);
        if (hardwareArguments && (transport is null || !transport.Equals("hardware", StringComparison.OrdinalIgnoreCase)))
            return new TransportChoice(TransportKind.Hardware, false,
                "Hardware arguments (--engine-path, --engine-sha256, --device, --session) were given without --transport=hardware.");

        if (transport is null || transport.Equals("demo", StringComparison.OrdinalIgnoreCase))
            return new TransportChoice(TransportKind.Demo, true, "Demo transport: synthetic data, no hardware access.");

        if (transport.Equals("ble", StringComparison.OrdinalIgnoreCase))
            return new TransportChoice(TransportKind.Broadcast, true,
                "Bluetooth LE broadcast observation: read-only, approximate, identity unconfirmed; listening starts automatically while Nearby detection is on, and can be turned off in Settings.");

        if (transport.Equals("ntpods", StringComparison.OrdinalIgnoreCase))
            return new TransportChoice(TransportKind.NtpodsDaemon, false,
                "The upstream NTPods daemon is not supported. Use --transport=hardware with the Clasp engine.");

        if (!transport.Equals("hardware", StringComparison.OrdinalIgnoreCase))
            return new TransportChoice(TransportKind.Hardware, false,
                $"Unknown transport '{transport}'. Use --transport=demo, --transport=hardware or --transport=ble.");

        return FromHardwareArgs(list);
    }

    private static TransportChoice FromHardwareArgs(List<string> args)
    {
        static TransportChoice Refuse(string reason) => new(TransportKind.Hardware, false, reason);

        var path = Value(args, "--engine-path");
        if (string.IsNullOrWhiteSpace(path)) return Refuse("--engine-path is required: the full path of clasp-engine.exe.");
        path = path.Trim().Trim('"');

        var sha = Value(args, "--engine-sha256")?.Trim();
        if (sha is { Length: 0 }) sha = null;

        var device = Value(args, "--device")?.Trim();
        if (string.IsNullOrEmpty(device)) return Refuse("--device is required: the device id (dev-...) from the engine's device list.");

        var sessionText = Value(args, "--session")?.Trim();
        EngineSessionMode session;
        if (sessionText is null || sessionText.Equals("observe", StringComparison.OrdinalIgnoreCase)) session = EngineSessionMode.Observe;
        else if (sessionText.Equals("control", StringComparison.OrdinalIgnoreCase)) session = EngineSessionMode.Control;
        else return Refuse($"Unknown session mode '{sessionText}'. Use --session=observe or --session=control.");

        var options = new ClaspEngineOptions(path, sha?.ToUpperInvariant(), device, session);
        if (options.Validate() is { } invalid) return Refuse(invalid);
        return new TransportChoice(TransportKind.Hardware, true,
            $"Clasp engine at {path}, {EngineWire.SessionModeName(session)} session for {device}.", options);
    }

    /// <summary>The value of the first <c>name=value</c> argument, or null.</summary>
    private static string? Value(List<string> args, string name)
    {
        var prefix = name + "=";
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return arg[prefix.Length..];
        }
        return null;
    }
}
