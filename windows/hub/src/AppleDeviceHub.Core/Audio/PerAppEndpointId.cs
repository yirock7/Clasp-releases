namespace AppleDeviceHub.Core.Audio;

/// <summary>
/// Converts between an MMDevice render endpoint id (for example "{0.0.0.00000000}.{guid}") and
/// the software-device form the per-app policy store uses
/// ("\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{e6327cad-dcec-4949-ae8a-991e976a79d2}").
/// </summary>
public static class PerAppEndpointId
{
    private const string Prefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureSuffix = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    /// <summary>Returns the persisted form, or null for an empty id.</summary>
    public static string? ToPersisted(string? mmDeviceId)
    {
        if (string.IsNullOrWhiteSpace(mmDeviceId)) return null;
        var id = FromPersisted(mmDeviceId.Trim());
        return id.Length == 0 ? null : Prefix + id + RenderSuffix;
    }

    /// <summary>Strips the persisted wrapper; ids that are not wrapped come back unchanged.</summary>
    public static string FromPersisted(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var id = value.Trim();
        if (id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) id = id[Prefix.Length..];
        if (id.EndsWith(RenderSuffix, StringComparison.OrdinalIgnoreCase)) id = id[..^RenderSuffix.Length];
        else if (id.EndsWith(CaptureSuffix, StringComparison.OrdinalIgnoreCase)) id = id[..^CaptureSuffix.Length];
        return id;
    }
}
