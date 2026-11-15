using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AppleDeviceHub.Core.Resources;
using AppleDeviceHub.Core.Storage;
using AppleDeviceHub.Core.Updates;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Reads Clasp's GitHub releases, downloads an installer with its signature into
/// %LOCALAPPDATA%\AppleDeviceHub\updates, and starts it only after the signature verifies against
/// the release key built into the app. Nothing is sent but the requests themselves (no identifiers).
/// </summary>
internal sealed class GitHubUpdateService : IUpdateService
{
    private const long MaxInstallerBytes = 200L * 1024 * 1024;
    private const int MaxSignatureBytes = 4096;
    private const int MaxFeedBytes = 1024 * 1024;

    // The verified installer, held open (readers allowed, writers and deleters refused) from verification until the
    // installer process has been started, so the file cannot be swapped in between.
    private FileStream? _pinnedInstaller;
    private string? _pinnedPath;

    private static readonly HttpClient Http = CreateClient();

    public ClaspVersion? CurrentVersion { get; } = ClaspVersion.FromInformationalVersion(
        typeof(GitHubUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    private static string UpdatesFolder => Path.Combine(AppDataFolder.Current, "updates");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = Timeout.InfiniteTimeSpan, // callers pass their own cancellation
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Clasp-Updater");
        return client;
    }

    public async Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseFeed.ReleasesApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = Encoding.UTF8.GetString(await ReadLimitedAsync(response, MaxFeedBytes, cancellationToken));
        var releases = ReleaseFeed.Parse(json);
        LocalLog.Write($"Update check: {releases.Count} releases listed; current {CurrentVersion?.ToString() ?? "development build"}.");
        return releases;
    }

    public async Task<string> DownloadVerifiedAsync(ReleaseCandidate release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!release.IsInstallable || release.InstallerSize > MaxInstallerBytes)
            throw new UpdateVerificationException(Strings.UpdateReason_NoValidInstaller);

        ReleasePin(); // an earlier verified download that was never started
        var versionFolder = Path.Combine(UpdatesFolder, release.Version.ToString());
        EnsurePlainDirectory(UpdatesFolder);
        EnsurePlainDirectory(versionFolder);
        // A fresh folder per download: nothing from an earlier attempt (or planted there) is ever reused.
        var folder = Path.Combine(versionFolder, Guid.NewGuid().ToString("N"));
        EnsurePlainDirectory(folder);
        var installerPath = Path.Combine(folder, release.InstallerName);

        try
        {
            var signature = await DownloadSignatureAsync(release.SignatureUrl!, cancellationToken);

            await using (var file = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > release.InstallerSize || total > MaxInstallerBytes)
                        throw new UpdateVerificationException(Strings.UpdateReason_DownloadTooLarge);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report((double)total / release.InstallerSize);
                }
                if (total != release.InstallerSize)
                    throw new UpdateVerificationException(Strings.UpdateReason_DownloadIncomplete);
            }

            // Verify what is really on disk, through a handle that stays open: others may read the file but nobody
            // can write to it, rename it or delete it until the installer has been started.
            var pinned = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (pinned.Length != release.InstallerSize)
                    throw new UpdateVerificationException(Strings.UpdateReason_WrongSize);
                var hash = await SHA256.HashDataAsync(pinned, cancellationToken);
                if (!UpdateSignature.Verify(UpdateSignature.PublicKey, release.Version, release.InstallerName, hash, signature))
                {
                    LocalLog.Write($"Update {release.Version} rejected: signature does not verify.");
                    throw new UpdateVerificationException(Strings.UpdateReason_SignatureMismatch);
                }
                LocalLog.Write($"Update {release.Version} downloaded and verified ({Convert.ToHexStringLower(hash)}).");
            }
            catch
            {
                pinned.Dispose();
                throw;
            }
            _pinnedInstaller = pinned;
            _pinnedPath = installerPath;
            return installerPath;
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    /// <summary>Creates the directory if needed and refuses a junction or symbolic link.</summary>
    private static void EnsurePlainDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            throw new UpdateVerificationException(Strings.UpdateReason_FolderNotPlain);
    }

    private void ReleasePin()
    {
        var pinned = _pinnedInstaller;
        _pinnedInstaller = null;
        _pinnedPath = null;
        pinned?.Dispose();
    }

    /// <summary>Reads the body, refusing anything over <paramref name="limit"/> bytes (stops at limit + 1). Bytes, not characters.</summary>
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new IOException("The response was larger than allowed.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit) throw new IOException("The response was larger than allowed.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task<string> DownloadSignatureAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        try
        {
            return Encoding.UTF8.GetString(await ReadLimitedAsync(response, MaxSignatureBytes, cancellationToken));
        }
        catch (IOException)
        {
            throw new UpdateVerificationException(Strings.UpdateReason_SignatureTooLarge);
        }
    }

    public void StartInstaller(string installerPath)
    {
        // Quiet upgrade: no wizard pages, /WAITPID makes Setup wait for this process to exit before it replaces files,
        // /CLOSEAPPLICATIONS closes anything that is still holding them, /LOG leaves a log in the updates folder, and
        // /RELAUNCH makes the installer reopen Clasp when it finishes (see Clasp.iss).
        // The installer must be the file that was just verified and is still held open.
        if (_pinnedInstaller is null || !string.Equals(_pinnedPath, installerPath, StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException(Strings.UpdateReason_InstallerNoLongerVerified);
        ClaspVersion? version = ClaspVersion.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(installerPath))), out var parsed) ? parsed : null;
        var logPath = Path.Combine(UpdatesFolder, version is { } v ? InstallerLaunch.LogFileName(v) : "setup.log");
        var start = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            Arguments = InstallerLaunch.CommandLine(logPath, Environment.ProcessId),
        };
        LocalLog.Write($"Starting update installer {Path.GetFileName(installerPath)} (log {Path.GetFileName(logPath)}).");
        try { Process.Start(start)?.Dispose(); }
        finally { ReleasePin(); } // only now may the file change again
    }

    /// <summary>Removes downloaded installers older than the running version (best effort, at startup).</summary>
    public void CleanUpOldDownloads()
    {
        try
        {
            if (!Directory.Exists(UpdatesFolder) || CurrentVersion is not { } current) return;
            foreach (var folder in Directory.GetDirectories(UpdatesFolder))
                if (!ClaspVersion.TryParse(Path.GetFileName(folder), out var version) || version <= current) TryDelete(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
