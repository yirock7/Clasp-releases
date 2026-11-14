using System.Text.RegularExpressions;

namespace AppleDeviceHub.Core.Engine;

/// <summary>
/// Explicit hardware configuration from the command line. Nothing here is discovered: the
/// engine path is never searched for (no PATH lookup), and no process is started.
/// </summary>
/// <param name="EnginePath">Full path of the clasp-engine executable the pipe server must be.</param>
/// <param name="EngineSha256">Optional expected SHA-256 of that file (64 hex characters).</param>
/// <param name="DeviceId">The selected device (<c>dev-</c> plus 16 lowercase hex characters).</param>
/// <param name="SessionMode">Observe (default) or control.</param>
public sealed partial record ClaspEngineOptions(string EnginePath, string? EngineSha256, string DeviceId, EngineSessionMode SessionMode)
{
    /// <summary>Pipe name override for tests; null means <c>Clasp.Engine.v1.&lt;current user SID&gt;</c>.</summary>
    public string? PipeName { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Reply timeout for list_devices, close_session and set_listening_mode.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reply timeout for open_session. Above the engine's own 20 s open timeout, so the engine's
    /// precise timed_out result arrives first.
    /// </summary>
    public TimeSpan OpenSessionTimeout { get; init; } = TimeSpan.FromSeconds(25);

    [GeneratedRegex("^dev-[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static bool IsValidDeviceId(string? value) => value is not null && DeviceIdPattern().IsMatch(value);

    public static bool IsValidSha256(string? value) => value is not null && Sha256Pattern().IsMatch(value);

    /// <summary>
    /// A local, fully qualified path (drive letter and root). Relative paths, bare file names and
    /// network (UNC) paths are refused.
    /// </summary>
    public static bool IsValidEnginePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)) return false;
        return Path.IsPathFullyQualified(value);
    }

    /// <summary>Null when usable, otherwise the reason it is not.</summary>
    public string? Validate()
    {
        if (!IsValidEnginePath(EnginePath)) return "--engine-path must be the full local path of clasp-engine.exe (for example C:/Clasp/clasp-engine.exe; no relative or network paths).";
        if (EngineSha256 is not null && !IsValidSha256(EngineSha256)) return "--engine-sha256 must be 64 hexadecimal characters.";
        if (!IsValidDeviceId(DeviceId)) return "--device must be a device id such as dev-3f9a1c0e8b7d6a52 (from the engine's device list).";
        return null;
    }
}
