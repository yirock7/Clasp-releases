using System.Globalization;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Updates;

/// <summary>Where the app talks to the release server, downloads, and starts the installer.</summary>
public interface IUpdateService
{
    /// <summary>The running app's release version, or null for a development build (which never updates).</summary>
    ClaspVersion? CurrentVersion { get; }

    Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the installer and its signature and returns the installer's local path only after
    /// the signature verifies. Throws <see cref="UpdateVerificationException"/> when it does not.
    /// </summary>
    Task<string> DownloadVerifiedAsync(ReleaseCandidate release, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Starts the verified installer quietly; it closes Clasp, upgrades, and reopens it.</summary>
    void StartInstaller(string installerPath);
}

/// <summary>A downloaded update failed its signature or size check; nothing was installed.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Failed,
}

/// <summary>
/// Update checks and the one-click install. Automatic checks run only after the user agreed
/// (<see cref="HubSettings.UpdateCheckFrequency"/>); "Check now" always works. Nothing is installed
/// unless it is newer than this build and signed with the release key.
/// Methods are called on the UI thread and resume there, so property changes reach bindings directly.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _service;
    private readonly HubSettings _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<ClaspVersion> _announced = new();
    private UpdatePhase _phase;
    private ReleaseCandidate? _available;
    private string? _errorText;
    private int _downloadPercent;
    private bool _consentDismissed;

    public UpdateViewModel(IUpdateService service, HubSettings settings, Func<DateTimeOffset>? clock = null)
    {
        _service = service;
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HubSettings.UpdateCheckFrequency))
            {
                Raise(nameof(ShowConsentPrompt));
            }
            else if (e.PropertyName is nameof(HubSettings.LastUpdateCheckAt))
            {
                Raise(nameof(LastCheckedText));
            }
        };
    }

    /// <summary>An automatic check found a version not announced before in this session.</summary>
    public event EventHandler<ReleaseCandidate>? UpdateFound;

    /// <summary>The installer is running; the host should quit so it can replace the app.</summary>
    public event EventHandler? InstallerStarted;

    public UpdatePhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(CanCheck));
            Raise(nameof(CanInstall));
            Raise(nameof(IsBusy));
            Raise(nameof(ShowUpdateBanner));
            Raise(nameof(InstallButtonText));
        }
    }

    public ReleaseCandidate? AvailableRelease
    {
        get => _available;
        private set
        {
            if (!Set(ref _available, value)) return;
            Raise(nameof(AvailableVersionText));
            Raise(nameof(InstallButtonText));
            Raise(nameof(CanInstall));
        }
    }

    public int DownloadPercent
    {
        get => _downloadPercent;
        private set { if (Set(ref _downloadPercent, value)) Raise(nameof(StatusText)); }
    }

    public bool IsDevelopmentBuild => _service.CurrentVersion is null;

    public string CurrentVersionText => _service.CurrentVersion is { } version
        ? $"Version {version}"
        : "Development build. Updates are off.";

    public string AvailableVersionText => AvailableRelease is { } release ? $"Clasp {release.Version}" : "";

    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    public bool CanCheck => !IsBusy && !IsDevelopmentBuild;

    public bool CanInstall => Phase == UpdatePhase.Available && AvailableRelease is { IsInstallable: true };

    public string InstallButtonText => AvailableRelease is { } release ? $"Update to {release.Version}" : "Update";

    /// <summary>The flyout banner: a newer release was found and nothing is in progress.</summary>
    public bool ShowUpdateBanner => Phase == UpdatePhase.Available;

    /// <summary>The one-time question, until the user answers it (or hides it for this session).</summary>
    public bool ShowConsentPrompt
        => !IsDevelopmentBuild && _settings.UpdateCheckFrequency == UpdateCheckFrequency.NotAsked && !_consentDismissed;

    public string StatusText => Phase switch
    {
        UpdatePhase.Checking => "Checking for updates…",
        UpdatePhase.UpToDate => "Clasp is up to date.",
        UpdatePhase.Available when AvailableRelease is { } release => $"Clasp {release.Version} is available.",
        UpdatePhase.Downloading => $"Downloading the update… {DownloadPercent}%",
        UpdatePhase.Installing => "Installing the update. Clasp will restart.",
        UpdatePhase.Failed => _errorText ?? "The update didn't finish.",
        _ => IsDevelopmentBuild ? "Development builds don't check for updates." : "",
    };

    public string LastCheckedText => _settings.LastUpdateCheckAt is { } at
        ? $"Last checked {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
        : "Not checked yet";

    /// <summary>"Yes" to the one-time question: check automatically, daily.</summary>
    public void AcceptAutomaticChecks()
    {
        _settings.UpdateCheckFrequency = UpdateCheckFrequency.Daily;
        _ = CheckIfDueAsync();
    }

    /// <summary>"No thanks": only check when the user asks.</summary>
    public void DeclineAutomaticChecks() => _settings.UpdateCheckFrequency = UpdateCheckFrequency.Manual;

    /// <summary>"Not now": hide the question until the next launch.</summary>
    public void DismissConsentPrompt()
    {
        _consentDismissed = true;
        Raise(nameof(ShowConsentPrompt));
    }

    public Task CheckNowAsync() => CheckAsync(automatic: false);

    /// <summary>Runs a check only when the chosen frequency says one is due.</summary>
    public Task CheckIfDueAsync()
        => UpdateSchedule.IsDue(_settings.UpdateCheckFrequency, _settings.LastUpdateCheckAt, _clock())
            ? CheckAsync(automatic: true)
            : Task.CompletedTask;

    private async Task CheckAsync(bool automatic)
    {
        if (!CanCheck || _service.CurrentVersion is not { } current) return;
        // An automatic check never hides an update the user has not acted on yet.
        if (automatic && Phase == UpdatePhase.Available) return;
        Phase = UpdatePhase.Checking;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var releases = await _service.GetReleasesAsync(timeout.Token);
            _settings.LastUpdateCheckAt = _clock();
            var newest = ReleaseFeed.NewestAbove(releases, current);
            AvailableRelease = newest;
            Phase = newest is null ? UpdatePhase.UpToDate : UpdatePhase.Available;
            if (newest is not null && automatic && _announced.Add(newest.Version)) UpdateFound?.Invoke(this, newest);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException
            or System.Text.Json.JsonException or IOException)
        {
            Fail(automatic
                ? "Couldn't check for updates. Clasp will try again later."
                : "Couldn't check for updates. Check your internet connection and try again.");
        }
    }

    public async Task InstallAsync()
    {
        if (!CanInstall || AvailableRelease is not { } release) return;
        Phase = UpdatePhase.Downloading;
        DownloadPercent = 0;
        try
        {
            var progress = new Progress<double>(fraction => DownloadPercent = (int)Math.Clamp(Math.Round(fraction * 100), 0, 100));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var installer = await _service.DownloadVerifiedAsync(release, progress, timeout.Token);
            Phase = UpdatePhase.Installing;
            _service.StartInstaller(installer);
            InstallerStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (UpdateVerificationException exception)
        {
            AvailableRelease = null;
            Fail($"Clasp didn't install the update because {exception.Message}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException
            or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Fail("Couldn't download the update. Nothing was changed. Try again later.");
        }
    }

    private void Fail(string message)
    {
        _errorText = message;
        Phase = UpdatePhase.Failed;
        Raise(nameof(StatusText));
    }
}
