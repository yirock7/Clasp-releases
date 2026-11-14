using GattDiscoveryProbe.Analysis;

namespace GattDiscoveryProbe.TestProbe.Tests;

public sealed class ProbeArgumentsTests
{
    [Fact]
    public void NoArgumentsRequestOnlyTheScope()
    {
        ProbeArgumentResult result = ProbeArguments.Parse([]);

        Assert.Equal(ProbeCommandKind.Scope, result.Command);
        Assert.Null(result.Options);
    }

    [Fact]
    public void PairedListingIsItsOwnNonLiveCommand()
    {
        ProbeArgumentResult result = ProbeArguments.Parse([ProbeArguments.ListPairedFlag]);

        Assert.Equal(ProbeCommandKind.ListPaired, result.Command);
    }

    [Fact]
    public void AnyOtherArgumentsWithoutExactAuthorizationAreRefused()
    {
        Assert.Equal(ProbeCommandKind.Refused, ProbeArguments.Parse(["--bogus"]).Command);
        Assert.Equal(ProbeCommandKind.Refused, ProbeArguments.Parse(["--device-id", "private-id"]).Command);
        Assert.Equal(ProbeCommandKind.Refused, ProbeArguments.Parse(["--I-authorize-gatt-metadata-discovery", "--device-id", "private-id"]).Command);
    }

    [Fact]
    public void LiveArgumentsRequireExactFlagAndPairedId()
    {
        ProbeArgumentResult result = ProbeArguments.Parse([
            ProbeArguments.AuthorizeFlag,
            "--device-id",
            "paired-le-id",
            "--out",
            @"D:\results\one"]);

        Assert.Equal(ProbeCommandKind.Live, result.Command);
        Assert.Equal("paired-le-id", result.Options!.DeviceId);
        Assert.Equal(@"D:\results\one", result.Options.OutDir);
    }

    [Fact]
    public void InvalidLiveArgumentsAreRefused()
    {
        string[][] invalidArguments =
        [
            [ProbeArguments.AuthorizeFlag],
            [ProbeArguments.AuthorizeFlag, "--device-id"],
            [ProbeArguments.AuthorizeFlag, "--device-id", "id", "--device-id", "id2"],
            [ProbeArguments.AuthorizeFlag, "--device-id", "id", "--list-paired"],
        ];

        foreach (string[] args in invalidArguments)
            Assert.Equal(ProbeCommandKind.Refused, ProbeArguments.Parse(args).Command);
    }
}

public sealed class OutcomeClassifierTests
{
    private static readonly Guid BatteryService = Guid.Parse("0000180F-0000-1000-8000-00805F9B34FB");
    private static readonly Guid DeviceInformationService = Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB");
    private static readonly Guid HeartRateService = Guid.Parse("0000180D-0000-1000-8000-00805F9B34FB");
    private static readonly Guid GapService = Guid.Parse("00001800-0000-1000-8000-00805F9B34FB");
    private static readonly Guid GattService = Guid.Parse("00001801-0000-1000-8000-00805F9B34FB");
    private static readonly Guid VendorService = Guid.Parse("4715650B-5E9D-4AC2-B898-A4FC0AA5DF78");
    private static readonly Guid OtherSigService = Guid.Parse("000018FE-0000-1000-8000-00805F9B34FB");

    [Fact]
    public void UnresolvedPairedIdentityMapsToFourteen()
    {
        var result = new ProbeRunResult { IdentityEnumerationCompleted = true };

        Assert.Equal(ProbeExitCode.NoPairedLeIdentity, OutcomeClassifier.Classify(result));
    }

    [Fact]
    public void UnexpectedPairedIdResolutionFailureMapsToOne()
    {
        var result = new ProbeRunResult { IdentityEnumerationCompleted = true, PairedLeIdentityResolved = false };
        result.Calls.Add(new ProbeCall
        {
            Operation = "fake-resolver-call",
            OperationStatus = "Failed",
            ExceptionType = "COMException",
        });

        Assert.Equal(ProbeExitCode.UnexpectedError, OutcomeClassifier.Classify(result));
    }

    [Fact]
    public void BatteryDeviceInformationAndHeartRateMapToUsefulStandard()
    {
        foreach (Guid uuid in new[] { BatteryService, DeviceInformationService, HeartRateService })
        {
            var result = SuccessfulIdentity();
            result.Services.Add(Service(uuid));

            ProbeAssessment assessment = OutcomeClassifier.Assess(result);
            Assert.Equal(ProbeExitCode.UsefulStandard, assessment.ExitCode);
            Assert.Equal(new[] { ServiceOutcomeCategory.USEFUL_STANDARD }, assessment.ServiceCategories);
        }
    }

    [Fact]
    public void ListedFunctionalSigServiceMapsToUsefulStandard()
    {
        var result = SuccessfulIdentity();
        result.Services.Add(Service(Guid.Parse("00001812-0000-1000-8000-00805F9B34FB"))); // HID

        Assert.Equal(ProbeExitCode.UsefulStandard, OutcomeClassifier.Classify(result));
    }

    [Fact]
    public void Vendor128BitServiceMapsToVendorDiscovery()
    {
        var result = SuccessfulIdentity();
        result.Services.Add(Service(VendorService));

        ProbeAssessment assessment = OutcomeClassifier.Assess(result);
        Assert.Equal(ProbeExitCode.VendorDiscovery, assessment.ExitCode);
        Assert.Equal(new[] { ServiceOutcomeCategory.VENDOR_DISCOVERY }, assessment.ServiceCategories);
    }

    [Fact]
    public void OnlyGapAndGattMapToPlumbingOnly()
    {
        var result = SuccessfulIdentity();
        result.Services.Add(Service(GapService));
        result.Services.Add(Service(GattService));

        ProbeAssessment assessment = OutcomeClassifier.Assess(result);
        Assert.Equal(ProbeExitCode.PlumbingOnly, assessment.ExitCode);
        Assert.Equal(new[] { ServiceOutcomeCategory.PLUMBING_ONLY }, assessment.ServiceCategories);
    }

    [Fact]
    public void MixedFunctionalAndVendorResultsReportBothAndVendorCodeWins()
    {
        var result = SuccessfulIdentity();
        result.Services.Add(Service(BatteryService));
        result.Services.Add(Service(GapService));
        result.Services.Add(Service(VendorService));

        ProbeAssessment assessment = OutcomeClassifier.Assess(result);

        Assert.Equal(ProbeExitCode.VendorDiscovery, assessment.ExitCode);
        Assert.Equal(
            new[] { ServiceOutcomeCategory.USEFUL_STANDARD, ServiceOutcomeCategory.VENDOR_DISCOVERY },
            assessment.ServiceCategories);
    }

    [Fact]
    public void FunctionalAndPlumbingMixIsNotMisreportedAsPlumbingOnly()
    {
        var result = SuccessfulIdentity();
        result.Services.Add(Service(BatteryService));
        result.Services.Add(Service(GapService));

        ProbeAssessment assessment = OutcomeClassifier.Assess(result);

        Assert.Equal(ProbeExitCode.UsefulStandard, assessment.ExitCode);
        Assert.Equal(new[] { ServiceOutcomeCategory.USEFUL_STANDARD }, assessment.ServiceCategories);
    }

    [Fact]
    public void UnknownSigServiceIsReportedWithoutBeingCalledUsefulOrVendor()
    {
        var result = SuccessfulIdentity();
        result.Services.Add(Service(OtherSigService));

        ProbeAssessment assessment = OutcomeClassifier.Assess(result);

        Assert.Equal(ProbeExitCode.OtherSigServices, assessment.ExitCode);
        Assert.Empty(assessment.ServiceCategories);
        Assert.Equal(new[] { OtherSigService.ToString("D").ToUpperInvariant() }, assessment.UnclassifiedSigServiceUuids);
    }

    [Fact]
    public void VendorDiscoveryTakesPrecedenceOverLaterIncompleteCall()
    {
        var result = SuccessfulIdentity();
        result.Services.Add(Service(VendorService));
        result.Calls.Add(new ProbeCall { Operation = "later-call", OperationStatus = "TimedOut", TimedOut = true });

        Assert.Equal(ProbeExitCode.VendorDiscovery, OutcomeClassifier.Classify(result));
    }

    [Fact]
    public void AccessDeniedMapsToTwelve()
    {
        var result = SuccessfulIdentity();
        result.Calls.Add(new ProbeCall
        {
            Operation = "fake-gatt-call",
            OperationStatus = "Completed",
            CommunicationStatus = "AccessDenied",
        });

        Assert.Equal(ProbeExitCode.AccessDenied, OutcomeClassifier.Classify(result));
    }

    [Fact]
    public void TimeoutAndEmptyDiscoveryMapToThirteen()
    {
        var timedOut = SuccessfulIdentity();
        timedOut.Calls.Add(new ProbeCall { Operation = "fake-call", OperationStatus = "TimedOut", TimedOut = true });
        Assert.Equal(ProbeExitCode.UnreachableOrTimeout, OutcomeClassifier.Classify(timedOut));

        Assert.Equal(ProbeExitCode.UnreachableOrTimeout, OutcomeClassifier.Classify(SuccessfulIdentity()));
    }

    [Fact]
    public void UnexpectedEnumerationFailureMapsToOne()
    {
        var result = new ProbeRunResult();
        result.Calls.Add(new ProbeCall { Operation = "fake-list", OperationStatus = "Failed", ExceptionType = "InvalidOperationException" });

        Assert.Equal(ProbeExitCode.UnexpectedError, OutcomeClassifier.Classify(result));
    }

    [Fact]
    public void BluetoothSigBaseMatcherSeparatesSixteenBitFromVendorUuid()
    {
        Assert.True(OutcomeClassifier.TryGetBluetoothSig16BitId(BatteryService, out string serviceId));
        Assert.Equal("180F", serviceId);
        Assert.False(OutcomeClassifier.IsBluetoothSigBaseUuid(VendorService));
    }

    private static ProbeRunResult SuccessfulIdentity()
        => new() { IdentityEnumerationCompleted = true, PairedLeIdentityResolved = true };

    private static ServiceMetadata Service(Guid uuid)
        => new() { CacheMode = "Cached", Uuid = uuid.ToString("D"), AttributeHandle = 1 };
}

public sealed class OutputFolderTests
{
    private static readonly DateTimeOffset StartedUtc = new(2026, 10, 17, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public void DefaultFolderUsesUtcTimestampUnderTheRequiredRoot()
    {
        string actual = OutputFolder.DefaultFor(StartedUtc, @"C:\cache\gatt-probe");

        Assert.Equal(Path.Combine(@"C:\cache\gatt-probe", "20261017-040506"), actual);
    }

    [Fact]
    public void FolderInsideKnownRepositoryIsRefused()
    {
        string repo = Path.Combine(Path.GetTempPath(), "gatt-probe-repo");
        string candidate = Path.Combine(repo, "out");
        var result = Resolve(candidate, [repo]);

        Assert.Null(result.Path);
        Assert.Contains("Git repository", result.Error);
    }

    [Fact]
    public void FolderWithGitMarkerInAnAncestorIsRefused()
    {
        string repo = Path.Combine(Path.GetTempPath(), "gatt-probe-marker-repo");
        string candidate = Path.Combine(repo, "out");
        string marker = Path.Combine(repo, ".git");
        var result = OutputFolder.Resolve(
            candidate,
            StartedUtc,
            [],
            fileExists: path => false,
            directoryExists: path => string.Equals(path, marker, StringComparison.OrdinalIgnoreCase),
            directoryHasEntries: path => false);

        Assert.Null(result.Path);
        Assert.Contains("Git repository", result.Error);
    }

    [Fact]
    public void ExistingNonEmptyFolderIsRefusedButEmptyFolderIsAllowed()
    {
        string candidate = Path.Combine(Path.GetTempPath(), "gatt-probe-output");
        var nonEmpty = OutputFolder.Resolve(
            candidate,
            StartedUtc,
            [],
            fileExists: path => false,
            directoryExists: path => string.Equals(path, Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase),
            directoryHasEntries: path => true);
        var empty = OutputFolder.Resolve(
            candidate,
            StartedUtc,
            [],
            fileExists: path => false,
            directoryExists: path => string.Equals(path, Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase),
            directoryHasEntries: path => false);

        Assert.Null(nonEmpty.Path);
        Assert.Contains("not empty", nonEmpty.Error);
        Assert.Equal(Path.GetFullPath(candidate), empty.Path);
        Assert.Null(empty.Error);
    }

    [Fact]
    public void ExistingFileAtTheTargetIsRefused()
    {
        string candidate = Path.Combine(Path.GetTempPath(), "gatt-probe-file");
        var result = OutputFolder.Resolve(candidate, StartedUtc, [], fileExists: path =>
            string.Equals(path, Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase));

        Assert.Null(result.Path);
        Assert.Contains("file", result.Error);
    }

    private static (string? Path, string? Error) Resolve(string candidate, IEnumerable<string> roots)
        => OutputFolder.Resolve(
            candidate,
            StartedUtc,
            roots,
            fileExists: _ => false,
            directoryExists: _ => false,
            directoryHasEntries: _ => false);
}

public sealed class DeviceIdentityHasherTests
{
    [Fact]
    public void SameRunIsStableAndUsesSeparateNameAndIdDomains()
    {
        byte[] key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var hasher = new DeviceIdentityHasher(key);

        string first = hasher.HashName("AirPods");
        Assert.Equal(first, hasher.HashName("AirPods"));
        Assert.NotEqual(first, hasher.HashDeviceId("AirPods"));
        Assert.Equal(24, first.Length);
        Assert.DoesNotContain("AirPods", first, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentRunKeysProduceUnlinkableTags()
    {
        using var firstRun = new DeviceIdentityHasher(Enumerable.Repeat((byte)1, 32).ToArray());
        using var secondRun = new DeviceIdentityHasher(Enumerable.Repeat((byte)2, 32).ToArray());

        Assert.NotEqual(firstRun.HashDeviceId("paired-id"), secondRun.HashDeviceId("paired-id"));
    }
}
