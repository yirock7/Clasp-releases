namespace GattDiscoveryProbe.Analysis;

public enum ProbeExitCode
{
    Success = 0,
    UnexpectedError = 1,
    Refused = 2,
    VendorDiscovery = 10,
    UsefulStandard = 11,
    AccessDenied = 12,
    UnreachableOrTimeout = 13,
    NoPairedLeIdentity = 14,
    PlumbingOnly = 15,
    OtherSigServices = 16,
}

public enum ServiceOutcomeCategory
{
    USEFUL_STANDARD,
    PLUMBING_ONLY,
    VENDOR_DISCOVERY,
}

public sealed record ServiceAnalysis(
    IReadOnlyList<ServiceOutcomeCategory> Categories,
    IReadOnlyList<string> UnclassifiedSigServiceUuids);

public sealed record ProbeAssessment(
    ProbeExitCode ExitCode,
    IReadOnlyList<ServiceOutcomeCategory> ServiceCategories,
    IReadOnlyList<string> UnclassifiedSigServiceUuids);

/// <summary>Pure result classification. Tests pass fabricated result objects and never use Bluetooth.</summary>
public static class OutcomeClassifier
{
    private const string BluetoothBaseSuffix = "-0000-1000-8000-00805f9b34fb";

    // These assigned SIG services expose a concrete device capability or device information.
    // The list is intentionally explicit so plumbing and unknown profile UUIDs are not treated
    // as useful findings by guesswork.
    private static readonly HashSet<string> UsefulStandardServiceIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "1802", // Immediate Alert
        "1803", // Link Loss
        "1804", // Tx Power
        "1805", // Current Time
        "1808", // Glucose
        "1809", // Health Thermometer
        "180A", // Device Information
        "180D", // Heart Rate
        "180F", // Battery
        "1810", // Blood Pressure
        "1812", // Human Interface Device
        "1814", // Running Speed and Cadence
        "1816", // Cycling Speed and Cadence
        "1818", // Cycling Power
        "181A", // Environmental Sensing
        "1826", // Fitness Machine
    };

    private static readonly HashSet<string> PlumbingServiceIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "1800", // Generic Access
        "1801", // Generic Attribute
    };

    public static ProbeExitCode Classify(ProbeRunResult result) => Assess(result).ExitCode;

    public static ProbeAssessment Assess(ProbeRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ServiceAnalysis serviceAnalysis = AnalyzeServices(result.Services);

        // A positive functional or vendor discovery survives a later incomplete call. The vendor
        // code takes precedence when both positive categories were found.
        if (serviceAnalysis.Categories.Contains(ServiceOutcomeCategory.VENDOR_DISCOVERY))
            return Assessment(ProbeExitCode.VendorDiscovery, serviceAnalysis);
        if (serviceAnalysis.Categories.Contains(ServiceOutcomeCategory.USEFUL_STANDARD))
            return Assessment(ProbeExitCode.UsefulStandard, serviceAnalysis);

        if (result.Calls.Any(call => call.IsAccessDenied))
            return Assessment(ProbeExitCode.AccessDenied, serviceAnalysis);

        if (!result.IdentityEnumerationCompleted)
        {
            if (result.Calls.Any(call => call.IsUnreachableOrIncomplete))
                return Assessment(ProbeExitCode.UnreachableOrTimeout, serviceAnalysis);
            return Assessment(
                result.Calls.Any(call => call.IsUnexpectedFailure) ? ProbeExitCode.UnexpectedError : ProbeExitCode.UnreachableOrTimeout,
                serviceAnalysis);
        }

        if (!result.PairedLeIdentityResolved)
        {
            if (result.Calls.Any(call => call.IsUnreachableOrIncomplete))
                return Assessment(ProbeExitCode.UnreachableOrTimeout, serviceAnalysis);
            if (result.Calls.Any(call => call.IsUnexpectedFailure))
                return Assessment(ProbeExitCode.UnexpectedError, serviceAnalysis);
            return Assessment(ProbeExitCode.NoPairedLeIdentity, serviceAnalysis);
        }

        if (result.Calls.Any(call => call.IsUnreachableOrIncomplete))
            return Assessment(ProbeExitCode.UnreachableOrTimeout, serviceAnalysis);

        if (result.Calls.Any(call => call.IsUnexpectedFailure))
            return Assessment(ProbeExitCode.UnexpectedError, serviceAnalysis);

        if (result.Services.Count == 0)
            return Assessment(ProbeExitCode.UnreachableOrTimeout, serviceAnalysis);

        if (serviceAnalysis.Categories.Contains(ServiceOutcomeCategory.PLUMBING_ONLY))
            return Assessment(ProbeExitCode.PlumbingOnly, serviceAnalysis);

        if (serviceAnalysis.UnclassifiedSigServiceUuids.Count > 0)
            return Assessment(ProbeExitCode.OtherSigServices, serviceAnalysis);

        return Assessment(ProbeExitCode.UnreachableOrTimeout, serviceAnalysis);
    }

    public static ServiceAnalysis AnalyzeServices(IEnumerable<ServiceMetadata> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var categories = new HashSet<ServiceOutcomeCategory>();
        var unknownSig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        bool allPlumbing = true;

        foreach (ServiceMetadata service in services)
        {
            if (!Guid.TryParse(service.Uuid, out Guid uuid))
            {
                allPlumbing = false;
                continue;
            }

            count++;
            if (TryGetBluetoothSig16BitId(uuid, out string id))
            {
                if (PlumbingServiceIds.Contains(id))
                    continue;

                allPlumbing = false;
                if (UsefulStandardServiceIds.Contains(id))
                    categories.Add(ServiceOutcomeCategory.USEFUL_STANDARD);
                else
                    unknownSig.Add(uuid.ToString("D").ToUpperInvariant());
            }
            else if (IsBluetoothSigBaseUuid(uuid))
            {
                // SIG-base 32-bit UUIDs are not 16-bit services. Keep them visible as unclassified
                // SIG records rather than calling them vendor UUIDs.
                allPlumbing = false;
                unknownSig.Add(uuid.ToString("D").ToUpperInvariant());
            }
            else
            {
                allPlumbing = false;
                categories.Add(ServiceOutcomeCategory.VENDOR_DISCOVERY);
            }
        }

        if (count > 0 && allPlumbing)
            categories.Add(ServiceOutcomeCategory.PLUMBING_ONLY);

        return new ServiceAnalysis(
            Categories: new[]
            {
                ServiceOutcomeCategory.USEFUL_STANDARD,
                ServiceOutcomeCategory.PLUMBING_ONLY,
                ServiceOutcomeCategory.VENDOR_DISCOVERY,
            }.Where(categories.Contains).ToArray(),
            UnclassifiedSigServiceUuids: unknownSig.OrderBy(uuid => uuid, StringComparer.Ordinal).ToArray());
    }

    public static bool IsBluetoothSigBaseUuid(Guid uuid)
        => uuid.ToString("D").EndsWith(BluetoothBaseSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool TryGetBluetoothSig16BitId(Guid uuid, out string serviceId)
    {
        string value = uuid.ToString("D");
        if (value.StartsWith("0000", StringComparison.OrdinalIgnoreCase)
            && value.AsSpan(8).Equals(BluetoothBaseSuffix.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            serviceId = value.Substring(4, 4).ToUpperInvariant();
            return true;
        }

        serviceId = string.Empty;
        return false;
    }

    private static ProbeAssessment Assessment(ProbeExitCode exitCode, ServiceAnalysis serviceAnalysis)
        => new(exitCode, serviceAnalysis.Categories, serviceAnalysis.UnclassifiedSigServiceUuids);
}
