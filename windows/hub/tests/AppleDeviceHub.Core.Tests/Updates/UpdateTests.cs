using System.Security.Cryptography;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Updates;

namespace AppleDeviceHub.Core.Tests.Updates;

public sealed class UpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static ClaspVersion V(string text) => ClaspVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    // ----- Versions -----

    [Theory]
    [InlineData("0.1.3", true)]
    [InlineData("10.20.300", true)]
    [InlineData("0.1", false)]
    [InlineData("0.1.3.0", false)]
    [InlineData("0.1.3-beta", false)]
    [InlineData("v0.1.3", false)]
    [InlineData(" ", false)]
    [InlineData("1.-1.0", false)]
    public void Versions_are_exactly_three_numbers(string text, bool valid)
        => Assert.Equal(valid, ClaspVersion.TryParse(text, out _));

    [Fact]
    public void Versions_compare_numerically_not_as_text()
    {
        Assert.True(V("0.1.10") > V("0.1.9"));
        Assert.True(V("1.0.0") > V("0.99.99"));
        Assert.Equal(V("0.1.3"), V("0.1.3"));
    }

    [Fact]
    public void Tags_and_informational_versions_parse()
    {
        Assert.True(ClaspVersion.TryParseTag("clasp-v0.1.3", out var tag));
        Assert.Equal(V("0.1.3"), tag);
        Assert.False(ClaspVersion.TryParseTag("v0.1.3", out _));
        Assert.Equal(V("0.1.4"), ClaspVersion.FromInformationalVersion("0.1.4+9749797"));
        Assert.Null(ClaspVersion.FromInformationalVersion("0.0.0"));
        Assert.Null(ClaspVersion.FromInformationalVersion("1.0.0-dev"));
        Assert.Null(ClaspVersion.FromInformationalVersion(null));
    }

    // ----- Schedule -----

    [Theory]
    [InlineData(UpdateCheckFrequency.NotAsked, null, false)]
    [InlineData(UpdateCheckFrequency.Manual, null, false)]
    [InlineData(UpdateCheckFrequency.Daily, null, true)]
    [InlineData(UpdateCheckFrequency.Daily, 23.0, false)]
    [InlineData(UpdateCheckFrequency.Daily, 24.0, true)]
    [InlineData(UpdateCheckFrequency.Weekly, 24.0 * 6, false)]
    [InlineData(UpdateCheckFrequency.Weekly, 24.0 * 7, true)]
    [InlineData(UpdateCheckFrequency.Monthly, 24.0 * 29, false)]
    [InlineData(UpdateCheckFrequency.Monthly, 24.0 * 30, true)]
    [InlineData(UpdateCheckFrequency.Weekly, -5.0, true)]
    public void Checks_are_due_only_for_automatic_frequencies_after_their_interval(UpdateCheckFrequency frequency, double? hoursAgo, bool due)
    {
        DateTimeOffset? last = hoursAgo is double hours ? Now - TimeSpan.FromHours(hours) : null;
        Assert.Equal(due, UpdateSchedule.IsDue(frequency, last, Now));
    }

    // ----- Release feed -----

    private const string Feed = """
        [
          { "tag_name": "clasp-v0.1.5", "draft": true, "prerelease": true, "html_url": "https://github.com/yirock7/Clasp-releases/releases/tag/clasp-v0.1.5", "assets": [] },
          { "tag_name": "clasp-v0.1.4", "draft": false, "prerelease": true, "html_url": "https://github.com/yirock7/Clasp-releases/releases/tag/clasp-v0.1.4",
            "assets": [
              { "name": "Clasp-0.1.4-win-x64-setup.exe", "size": 18000000, "browser_download_url": "https://github.com/yirock7/Clasp-releases/releases/download/clasp-v0.1.4/Clasp-0.1.4-win-x64-setup.exe" },
              { "name": "Clasp-0.1.4-win-x64-setup.exe.sig", "size": 88, "browser_download_url": "https://github.com/yirock7/Clasp-releases/releases/download/clasp-v0.1.4/Clasp-0.1.4-win-x64-setup.exe.sig" }
            ] },
          { "tag_name": "clasp-v0.1.3", "draft": false, "prerelease": true, "html_url": "https://github.com/yirock7/Clasp-releases/releases/tag/clasp-v0.1.3",
            "assets": [
              { "name": "Clasp-0.1.3-win-x64-setup.exe", "size": 18000000, "browser_download_url": "https://github.com/yirock7/Clasp-releases/releases/download/clasp-v0.1.3/Clasp-0.1.3-win-x64-setup.exe" }
            ] },
          { "tag_name": "ntpods-v9.9.9", "draft": false, "assets": [] },
          { "tag_name": "clasp-v9.0.0", "draft": false, "html_url": "https://evil.example/x",
            "assets": [
              { "name": "Clasp-9.0.0-win-x64-setup.exe", "size": 5, "browser_download_url": "https://evil.example/Clasp-9.0.0-win-x64-setup.exe" },
              { "name": "Clasp-9.0.0-win-x64-setup.exe.sig", "size": 5, "browser_download_url": "http://github.com/yirock7/Clasp-releases/releases/download/clasp-v9.0.0/Clasp-9.0.0-win-x64-setup.exe.sig" }
            ] },
          "not an object"
        ]
        """;

    [Fact]
    public void Feed_skips_drafts_and_foreign_tags_and_keeps_only_GitHub_HTTPS_assets()
    {
        var releases = ReleaseFeed.Parse(Feed);
        Assert.Equal(["clasp-v0.1.4", "clasp-v0.1.3", "clasp-v9.0.0"], releases.Select(r => r.TagName));

        var current = releases[0];
        Assert.True(current.IsInstallable);
        Assert.Equal(18000000, current.InstallerSize);
        Assert.True(current.IsPrerelease);

        Assert.False(releases[1].IsInstallable); // no signature
        var foreign = releases[2];
        Assert.False(foreign.IsInstallable);       // assets off GitHub or not HTTPS are dropped
        Assert.Null(foreign.InstallerUrl);
        Assert.Equal(ReleaseFeed.ReleasesPageUrl, foreign.PageUrl);
    }

    [Fact]
    public void Newest_above_picks_the_highest_newer_version_or_none()
    {
        var releases = ReleaseFeed.Parse(Feed);
        Assert.Equal(V("0.1.4"), ReleaseFeed.NewestAbove(releases, V("0.1.3"))!.Version);
        Assert.Null(ReleaseFeed.NewestAbove(releases, V("0.1.4")));
        Assert.Null(ReleaseFeed.NewestAbove(releases, V("9.0.0")));
    }

    [Fact]
    public void Newest_above_skips_releases_that_are_not_installable_even_when_newer()
    {
        var releases = ReleaseFeed.Parse(Feed);
        // 9.0.0 has off-GitHub assets and 0.1.3 has no signature: neither may be offered.
        Assert.DoesNotContain(releases.Where(r => !r.IsInstallable), r => ReferenceEquals(r, ReleaseFeed.NewestAbove(releases, V("0.0.1"))));
        Assert.Null(ReleaseFeed.NewestAbove(releases.Where(r => !r.IsInstallable), V("0.0.1")));
    }

    [Fact]
    public void A_non_array_feed_has_no_releases()
        => Assert.Empty(ReleaseFeed.Parse("""{ "message": "API rate limit exceeded" }"""));

    // ----- Signatures -----

    [Fact]
    public void Signatures_bind_key_version_file_and_hash()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var hash = SHA256.HashData("installer bytes"u8);
        var version = V("0.1.4");
        var name = ReleaseFeed.InstallerName(version);
        var signature = UpdateSignature.Sign(key, version, name, hash);

        Assert.True(UpdateSignature.Verify(publicKey, version, name, hash, signature));
        Assert.False(UpdateSignature.Verify(publicKey, V("0.1.5"), name, hash, signature));                      // replayed as a newer version
        Assert.False(UpdateSignature.Verify(publicKey, version, "Other.exe", hash, signature));
        Assert.False(UpdateSignature.Verify(publicKey, version, name, SHA256.HashData("tampered"u8), signature));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateSignature.Verify(Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), version, name, hash, signature));

        foreach (var junk in new[] { null, "", "not base64!", Convert.ToBase64String(new byte[64]) })
            Assert.False(UpdateSignature.Verify(publicKey, version, name, hash, junk));
        Assert.False(UpdateSignature.Verify("bad key", version, name, hash, signature));
    }

    [Fact]
    public void The_shipped_public_key_is_a_P256_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateSignature.PublicKey), out _);
        Assert.Equal(256, key.KeySize);
    }

    // ----- Settings -----

    [Fact]
    public void Frequency_round_trips_and_the_combo_box_cannot_answer_the_question_by_loading()
    {
        var settings = new HubSettings();
        Assert.Equal(UpdateCheckFrequency.NotAsked, settings.UpdateCheckFrequency);
        settings.UpdateCheckFrequencyIndex = 0; // ComboBox writing back its initial selection
        Assert.Equal(UpdateCheckFrequency.NotAsked, settings.UpdateCheckFrequency);
        settings.UpdateCheckFrequencyIndex = 2;
        Assert.Equal(UpdateCheckFrequency.Weekly, settings.UpdateCheckFrequency);
        settings.UpdateCheckFrequencyIndex = 0;
        Assert.Equal(UpdateCheckFrequency.Manual, settings.UpdateCheckFrequency);

        var path = Path.Combine(Path.GetTempPath(), $"clasp-update-settings-{Guid.NewGuid():N}.json");
        try
        {
            settings.UpdateCheckFrequency = UpdateCheckFrequency.Monthly;
            settings.LastUpdateCheckAt = Now;
            Assert.True(HubSettingsStore.Save(settings, path));
            var loaded = HubSettingsStore.Load(path);
            Assert.Equal(UpdateCheckFrequency.Monthly, loaded.UpdateCheckFrequency);
            Assert.Equal(Now, loaded.LastUpdateCheckAt);
        }
        finally { File.Delete(path); }
    }

    // ----- View model -----

    private sealed class FakeUpdates : IUpdateService
    {
        public ClaspVersion? CurrentVersion { get; set; } = V("0.1.4");
        public IReadOnlyList<ReleaseCandidate> Releases { get; set; } = [];
        public Exception? CheckFailure { get; set; }
        public Exception? DownloadFailure { get; set; }
        public int Checks { get; private set; }
        public string? Started { get; private set; }

        public Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return CheckFailure is { } failure ? Task.FromException<IReadOnlyList<ReleaseCandidate>>(failure) : Task.FromResult(Releases);
        }

        public Task<string> DownloadVerifiedAsync(ReleaseCandidate release, IProgress<double>? progress, CancellationToken cancellationToken)
            => DownloadFailure is { } failure ? Task.FromException<string>(failure) : Task.FromResult(@"C:\updates\" + release.InstallerName);

        public void StartInstaller(string installerPath) => Started = installerPath;
    }

    private static ReleaseCandidate Release(string version, bool signed = true)
    {
        var v = V(version);
        return new ReleaseCandidate(v, "clasp-v" + version, ReleaseFeed.ReleasesPageUrl, true, ReleaseFeed.InstallerName(v),
            "https://github.com/yirock7/Clasp-releases/releases/download/x/" + ReleaseFeed.InstallerName(v), 100,
            signed ? "https://github.com/yirock7/Clasp-releases/releases/download/x/" + ReleaseFeed.SignatureName(v) : null);
    }

    [Fact]
    public async Task Nothing_is_checked_automatically_until_the_user_agrees()
    {
        var settings = new HubSettings();
        var service = new FakeUpdates();
        var vm = new UpdateViewModel(service, settings, () => Now);
        Assert.True(vm.ShowConsentPrompt);

        await vm.CheckIfDueAsync();
        Assert.Equal(0, service.Checks);

        vm.DeclineAutomaticChecks();
        Assert.False(vm.ShowConsentPrompt);
        await vm.CheckIfDueAsync();
        Assert.Equal(0, service.Checks);

        await vm.CheckNowAsync(); // a manual check always runs
        Assert.Equal(1, service.Checks);
        Assert.Equal(Now, settings.LastUpdateCheckAt);
    }

    [Fact]
    public async Task Accepting_checks_daily_and_announces_a_new_version_once()
    {
        var settings = new HubSettings();
        var service = new FakeUpdates { Releases = [Release("0.1.3"), Release("0.1.5")] };
        var clock = Now;
        var vm = new UpdateViewModel(service, settings, () => clock);
        var announced = new List<ClaspVersion>();
        vm.UpdateFound += (_, release) => announced.Add(release.Version);

        vm.AcceptAutomaticChecks();
        await Task.Yield();
        Assert.Equal(UpdateCheckFrequency.Daily, settings.UpdateCheckFrequency);
        Assert.Equal(UpdatePhase.Available, vm.Phase);
        Assert.True(vm.ShowUpdateBanner);
        Assert.True(vm.CanInstall);
        Assert.Equal("Update to 0.1.5", vm.InstallButtonText);

        clock = Now.AddDays(2);
        await vm.CheckIfDueAsync(); // an unanswered update is not re-checked
        Assert.Equal(1, service.Checks);
        Assert.Equal([V("0.1.5")], announced);
    }

    [Fact]
    public async Task Up_to_date_unsigned_and_failed_checks_are_reported_without_install()
    {
        var service = new FakeUpdates { Releases = [Release("0.1.4")] };
        var vm = new UpdateViewModel(service, new HubSettings());
        await vm.CheckNowAsync();
        Assert.Equal(UpdatePhase.UpToDate, vm.Phase);
        Assert.False(vm.CanInstall);

        service.Releases = [Release("0.1.5", signed: false)];
        await vm.CheckNowAsync();
        Assert.Equal(UpdatePhase.UpToDate, vm.Phase);
        Assert.Null(vm.AvailableRelease);
        Assert.False(vm.ShowUpdateBanner);
        Assert.False(vm.CanInstall);
        Assert.DoesNotContain("release page", vm.StatusText, StringComparison.Ordinal);

        service.CheckFailure = new HttpRequestException("offline");
        await vm.CheckNowAsync();
        Assert.Equal(UpdatePhase.Failed, vm.Phase);
        Assert.Contains("Couldn't check for updates", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_starts_the_verified_installer_and_asks_the_host_to_quit()
    {
        var service = new FakeUpdates { Releases = [Release("0.1.5")] };
        var vm = new UpdateViewModel(service, new HubSettings());
        var quit = false;
        vm.InstallerStarted += (_, _) => quit = true;
        await vm.CheckNowAsync();
        await vm.InstallAsync();
        Assert.Equal(@"C:\updates\Clasp-0.1.5-win-x64-setup.exe", service.Started);
        Assert.Equal(UpdatePhase.Installing, vm.Phase);
        Assert.True(quit);
    }

    [Fact]
    public async Task A_failed_verification_installs_nothing_and_forgets_the_release()
    {
        var service = new FakeUpdates { Releases = [Release("0.1.5")], DownloadFailure = new UpdateVerificationException("its signature doesn't match.") };
        var vm = new UpdateViewModel(service, new HubSettings());
        await vm.CheckNowAsync();
        await vm.InstallAsync();
        Assert.Null(service.Started);
        Assert.Equal(UpdatePhase.Failed, vm.Phase);
        Assert.Null(vm.AvailableRelease);
        Assert.Contains("signature doesn't match", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_builds_never_check()
    {
        var service = new FakeUpdates { CurrentVersion = null };
        var settings = new HubSettings { UpdateCheckFrequency = UpdateCheckFrequency.Daily };
        var vm = new UpdateViewModel(service, settings);
        Assert.False(vm.ShowConsentPrompt);
        Assert.False(vm.CanCheck);
        await vm.CheckNowAsync();
        await vm.CheckIfDueAsync();
        Assert.Equal(0, service.Checks);
    }
}
