namespace AppleDeviceHub.Core.Connection;

/// <summary>
/// Maps Windows default roles to the output used for playback status. Communications is
/// deliberately excluded because it can point at a headset call profile while media plays elsewhere.
/// </summary>
public static class ConnectionOutputProjection
{
    /// <summary>Use Multimedia as the playback output, falling back to Console when absent.</summary>
    public static string? SelectPlaybackOutputId(string? multimediaDefaultId, string? consoleDefaultId)
        => !string.IsNullOrWhiteSpace(multimediaDefaultId)
            ? multimediaDefaultId
            : !string.IsNullOrWhiteSpace(consoleDefaultId)
                ? consoleDefaultId
                : null;
}
