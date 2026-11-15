using System.Text.Json;

namespace AppleDeviceHub.Core.Updates;

/// <summary>
/// One published Clasp release that could be installed. <see cref="InstallerUrl"/> and
/// <see cref="SignatureUrl"/> are null when the release lacks that asset; a release without a
/// signature is still reported (so the user can be told about it) but is never installed.
/// </summary>
public sealed record ReleaseCandidate(
    ClaspVersion Version,
    string TagName,
    string PageUrl,
    bool IsPrerelease,
    string InstallerName,
    string? InstallerUrl,
    long InstallerSize,
    string? SignatureUrl)
{
    public bool IsInstallable => InstallerUrl is not null && SignatureUrl is not null && InstallerSize > 0;
}

/// <summary>
/// Reads the GitHub "list releases" response (<c>GET /repos/{owner}/{repo}/releases</c>). Uses
/// <see cref="JsonDocument"/> so it needs no reflection (the app is published with Native AOT).
/// Drafts, tags that are not <c>clasp-vX.Y.Z</c>, and malformed entries are skipped, never guessed.
/// </summary>
public static class ReleaseFeed
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/yirock7/Clasp-releases/releases?per_page=20";
    public const string ReleasesPageUrl = "https://github.com/yirock7/Clasp-releases/releases";

    /// <summary>The installer file name Build-Installer.ps1 produces for <paramref name="version"/>.</summary>
    public static string InstallerName(ClaspVersion version) => $"Clasp-{version}-win-x64-setup.exe";

    /// <summary>The detached signature published next to the installer.</summary>
    public static string SignatureName(ClaspVersion version) => InstallerName(version) + ".sig";

    public static IReadOnlyList<ReleaseCandidate> Parse(string json)
    {
        var releases = new List<ReleaseCandidate>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return releases;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;
            if (Bool(release, "draft")) continue;
            var tag = Text(release, "tag_name");
            if (!ClaspVersion.TryParseTag(tag, out var version)) continue;

            var installerName = InstallerName(version);
            var signatureName = SignatureName(version);
            string? installerUrl = null, signatureUrl = null;
            long installerSize = 0;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    var name = Text(asset, "name");
                    var url = Text(asset, "browser_download_url");
                    if (!IsGitHubDownload(url)) continue;
                    if (string.Equals(name, installerName, StringComparison.Ordinal))
                    {
                        installerUrl = url;
                        installerSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0;
                    }
                    else if (string.Equals(name, signatureName, StringComparison.Ordinal))
                    {
                        signatureUrl = url;
                    }
                }
            }

            var page = Text(release, "html_url");
            releases.Add(new ReleaseCandidate(
                version,
                tag!,
                IsGitHubPage(page) ? page! : ReleasesPageUrl,
                Bool(release, "prerelease"),
                installerName,
                installerUrl,
                installerSize,
                signatureUrl));
        }
        return releases;
    }

    /// <summary>
    /// The newest installable release above <paramref name="current"/>, or null when there is none.
    /// A release without a well-formed installer and signature is never considered: release metadata
    /// that Clasp cannot verify must not hide a signed update or send people to download by hand.
    /// </summary>
    public static ReleaseCandidate? NewestAbove(IEnumerable<ReleaseCandidate> releases, ClaspVersion current)
        => releases.Where(release => release.IsInstallable && release.Version > current).MaxBy(release => release.Version);

    /// <summary>Release assets are served from github.com over HTTPS (they redirect to GitHub's download host).</summary>
    public static bool IsGitHubDownload(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/yirock7/Clasp-releases/releases/download/", StringComparison.Ordinal);

    private static bool IsGitHubPage(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
