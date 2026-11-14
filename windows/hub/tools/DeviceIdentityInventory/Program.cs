using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceIdentityInventory.Analysis;
using Windows.Devices.Enumeration;

namespace DeviceIdentityInventory;

internal static class Program
{
    private static readonly Guid ClassicProtocolId = Guid.Parse("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    private static readonly Guid LeProtocolId = Guid.Parse("bb7bb05e-5972-42b5-94fc-76eaa7084d49");

    private const string AepAepIdKey = "System.Devices.Aep.AepId";
    private const string AepCanPairKey = "System.Devices.Aep.CanPair";
    private const string AepCategoryKey = "System.Devices.Aep.Category";
    private const string AepContainerIdKey = "System.Devices.Aep.ContainerId";
    private const string AepDeviceAddressKey = "System.Devices.Aep.DeviceAddress";
    private const string AepIsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string AepIsPairedKey = "System.Devices.Aep.IsPaired";
    private const string AepIsPresentKey = "System.Devices.Aep.IsPresent";
    private const string AepManufacturerKey = "System.Devices.Aep.Manufacturer";
    private const string AepModelIdKey = "System.Devices.Aep.ModelId";
    private const string AepModelNameKey = "System.Devices.Aep.ModelName";
    private const string AepProtocolIdKey = "System.Devices.Aep.ProtocolId";
    private const string ItemNameKey = "System.ItemNameDisplay";

    private static readonly string[] AepProperties =
    [
        ItemNameKey,
        AepAepIdKey,
        AepCanPairKey,
        AepCategoryKey,
        AepContainerIdKey,
        AepDeviceAddressKey,
        AepIsConnectedKey,
        AepIsPairedKey,
        AepIsPresentKey,
        AepManufacturerKey,
        AepModelIdKey,
        AepModelNameKey,
        AepProtocolIdKey,
    ];

    private static readonly string[] ContainerProperties =
    [
        ItemNameKey,
        "System.Devices.AepContainer.Categories",
        "System.Devices.AepContainer.Children",
        "System.Devices.AepContainer.CanPair",
        "System.Devices.AepContainer.ContainerId",
        "System.Devices.AepContainer.IsPaired",
        "System.Devices.AepContainer.IsPresent",
        "System.Devices.AepContainer.Manufacturer",
        "System.Devices.AepContainer.ModelIds",
        "System.Devices.AepContainer.ModelName",
        "System.Devices.AepContainer.ProtocolIds",
        "System.Devices.AepContainer.SupportsAudio",
        "System.Devices.AepContainer.SupportsImages",
        "System.Devices.AepContainer.SupportsVideo",
    ];

    private static readonly string[] DeviceProperties =
    [
        ItemNameKey,
        "System.Devices.Aep.AepId",
        "System.Devices.ClassGuid",
        "System.Devices.CompatibleIds",
        "System.Devices.ContainerId",
        "System.Devices.DeviceCapabilities",
        "System.Devices.DeviceHasProblem",
        "System.Devices.DeviceInstanceId",
        "System.Devices.DeviceManufacturer",
        "System.Devices.HardwareIds",
        "System.Devices.Parent",
        "System.Devices.Present",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main(string[] args)
    {
        InventoryArgumentResult parsed = InventoryArguments.Parse(args);
        switch (parsed.Command)
        {
            case InventoryCommand.Scope:
                PrintScope();
                return 0;

            case InventoryCommand.ListClassicPaired:
                return await ListClassicPairedAsync();

            case InventoryCommand.Refused:
                Console.Error.WriteLine($"Refused: {parsed.Error}");
                Console.Error.WriteLine("Run with no arguments for scope, or use --list-classic-paired.");
                return 2;

            case InventoryCommand.Inventory when parsed.Options is not null:
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    Console.Error.WriteLine("Refused: Windows 10 build 19041 or later is required.");
                    return 2;
                }
                return await RunInventoryAsync(parsed.Options);

            default:
                Console.Error.WriteLine("Refused: the command line could not be interpreted.");
                return 2;
        }
    }

    private static void PrintScope()
    {
        Console.WriteLine($"""
            DeviceIdentityInventory: read-only inventory of Windows device identity records.

            With no arguments, this prints scope only. It does not enumerate devices or create files.
            --list-classic-paired prints one nameHash,id row for each paired Bluetooth Classic AEP,
            then exits without creating a file.
            --classic-aep-id <id> runs the inventory for that exact paired Classic AEP. An optional
            --out <dir> selects an empty results folder outside every Git repository.

            The inventory reads existing records with DeviceInformation.FindAllAsync for these kinds:
              AssociationEndpoint, AssociationEndpointContainer, Device
            Each call uses an empty AQS selector (no property filter) and requests the property keys
            listed below. All names, record ids, container ids, and device addresses are stored and
            printed only as per-run keyed hashes. The list mode is the one exception for the selected
            purpose of copying a raw paired Classic AEP id.

            APIs:
              Windows.Devices.Enumeration.DeviceInformation.FindAllAsync
              Windows.Devices.Enumeration.DeviceInformationKind
            Selectors:
              AssociationEndpoint: "" (no AQS filter)
              AssociationEndpointContainer: "" (no AQS filter)
              Device: "" (no AQS filter)
            Output root: {OutputFolder.DefaultRoot}\<UTC yyyyMMdd-HHmmss>

            The outcome describes the identity evidence found in this inventory:
              CONFIRMED: a Bluetooth LE AEP shares the selected Classic AEP's container; its
                paired state is reported separately.
              CANDIDATE: the target container reports Bluetooth LE without an enumerated matching
                LE AEP, or enumeration was incomplete.
              NONE: no matching target or LE evidence was found in completed enumeration.
            """);

        PrintPropertyKeys();
    }

    private static void PrintPropertyKeys()
    {
        Console.WriteLine("Property keys requested:");
        PrintKeyList("AssociationEndpoint", AepProperties);
        PrintKeyList("AssociationEndpointContainer", ContainerProperties);
        PrintKeyList("Device", DeviceProperties);
    }

    private static void PrintKeyList(string kind, IEnumerable<string> keys)
    {
        Console.WriteLine($"  {kind}:");
        foreach (string key in keys)
            Console.WriteLine($"    {key}");
    }

    private static async Task<int> ListClassicPairedAsync()
    {
        using var hasher = IdentityHasher.CreateRandom();
        try
        {
            DeviceInformationCollection records = await DeviceInformation.FindAllAsync(
                string.Empty,
                AepProperties,
                DeviceInformationKind.AssociationEndpoint);

            foreach (DeviceInformation record in records)
            {
                if (!IsClassic(record) || ReadBool(record, AepIsPairedKey) != true)
                    continue;
                Console.WriteLine($"nameHash={hasher.HashName(record.Name)} id={record.Id}");
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Classic AEP enumeration failed ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> RunInventoryAsync(InventoryOptions options)
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        IReadOnlyList<string> repositoryRoots = OutputFolder.FindRepositoryRoots(
            AppContext.BaseDirectory,
            Environment.CurrentDirectory);
        (string? outputDirectory, string? folderError) = OutputFolder.Resolve(
            options.OutDirectory,
            startedUtc,
            repositoryRoots);
        if (outputDirectory is null)
        {
            Console.Error.WriteLine($"Refused: {folderError}");
            return 2;
        }

        using var hasher = IdentityHasher.CreateRandom();
        var report = new InventoryReport
        {
            StartedUtc = startedUtc,
            TargetAepIdHash = hasher.HashId(options.ClassicAepId),
            Selectors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AssociationEndpoint"] = "",
                ["AssociationEndpointContainer"] = "",
                ["Device"] = "",
            },
            PropertyKeys = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["AssociationEndpoint"] = AepProperties,
                ["AssociationEndpointContainer"] = ContainerProperties,
                ["Device"] = DeviceProperties,
            },
        };
        report.ApiCalls.Add("DeviceInformation.FindAllAsync(string, IEnumerable<string>, DeviceInformationKind)");

        PrintRunScope(report.TargetAepIdHash);

        (DeviceInformation[] endpointRecords, EnumerationStatus endpointStatus) = await EnumerateAsync(
            DeviceInformationKind.AssociationEndpoint,
            AepProperties);
        report.Enumerations.Add(endpointStatus);

        if (endpointStatus.Status != "Completed")
        {
            Console.Error.WriteLine("Refused: AssociationEndpoint enumeration failed, so the selected id could not be validated.");
            return 2;
        }

        List<RawAep> aeps = endpointRecords.Select(CreateRawAep).ToList();
        RawAep? target = aeps.FirstOrDefault(record =>
            string.Equals(record.Id, options.ClassicAepId, StringComparison.Ordinal));
        if (target is null)
        {
            Console.Error.WriteLine($"Refused: the selected id was not found (idHash={report.TargetAepIdHash}).");
            return 2;
        }
        if (target.ProtocolId != ClassicProtocolId || target.IsPaired != true)
        {
            Console.Error.WriteLine($"Refused: the selected record is not a paired Bluetooth Classic AEP (idHash={report.TargetAepIdHash}).");
            return 2;
        }

        report.TargetValidation = "valid";
        report.TargetIsPairedClassicAep = true;
        report.TargetContainerIdHash = target.ContainerId is null
            ? null
            : hasher.HashContainerId(target.ContainerId);

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Refused: the results folder could not be created ({exception.GetType().Name}).");
            return 2;
        }

        (DeviceInformation[] containerRecords, EnumerationStatus containerStatus) = await EnumerateAsync(
            DeviceInformationKind.AssociationEndpointContainer,
            ContainerProperties);
        report.Enumerations.Add(containerStatus);

        (DeviceInformation[] deviceRecords, EnumerationStatus deviceStatus) = await EnumerateAsync(
            DeviceInformationKind.Device,
            DeviceProperties);
        report.Enumerations.Add(deviceStatus);

        List<RawContainer> containers = containerRecords.Select(CreateRawContainer).ToList();
        List<RawDevice> devices = deviceRecords.Select(CreateRawDevice).ToList();

        List<RawAep> relevantAeps = aeps.Where(aep =>
            string.Equals(aep.Id, target.Id, StringComparison.Ordinal)
            || (aep.ProtocolId == LeProtocolId && aep.IsPaired == true)
            || CompareContainer(target, aep, containers).same == "same").ToList();

        foreach (RawAep aep in relevantAeps)
        {
            (string? same, string? evidence) = CompareContainer(target, aep, containers);
            report.AssociationEndpoints.Add(new AepRecord
            {
                IdHash = hasher.HashId(aep.Id),
                NameHash = hasher.HashName(aep.Name),
                Protocol = ProtocolLabel(aep.ProtocolId),
                IsPaired = aep.IsPaired,
                IsPresent = aep.IsPresent,
                IsConnected = aep.IsConnected,
                CanPair = aep.CanPair,
                ContainerIdHash = aep.ContainerId is null ? null : hasher.HashContainerId(aep.ContainerId),
                SameContainerAsTarget = same,
                SameContainerEvidence = evidence,
                Properties = SnapshotProperties(aep.Info, AepProperties, hasher),
            });
        }

        List<RawContainer> relevantContainers = containers.Where(container =>
            (target.ContainerId is not null && container.ContainerId is not null
                && string.Equals(target.ContainerId, container.ContainerId, StringComparison.OrdinalIgnoreCase))
            || container.Children.Contains(target.Id, StringComparer.Ordinal)).ToList();

        foreach (RawContainer container in relevantContainers)
        {
            bool? isSame = target.ContainerId is not null && container.ContainerId is not null
                ? string.Equals(target.ContainerId, container.ContainerId, StringComparison.OrdinalIgnoreCase)
                : container.Children.Contains(target.Id, StringComparer.Ordinal)
                    ? true
                    : null;

            report.AssociationEndpointContainers.Add(new ContainerRecord
            {
                ContainerIdHash = hasher.HashContainerId(container.ContainerId ?? container.Info.Id),
                NameHash = hasher.HashName(container.Name),
                IsPaired = container.IsPaired,
                IsPresent = container.IsPresent,
                CanPair = container.CanPair,
                SameContainerAsTarget = isSame is null ? "unknown" : isSame.Value ? "same" : "different",
                ChildIdHashes = container.Children.Select(hasher.HashId).ToArray(),
                Protocols = container.ProtocolIds.Select(id => SafeProtocolId(id, hasher) ?? "unknown").ToArray(),
                Properties = SnapshotProperties(container.Info, ContainerProperties, hasher),
            });
        }

        List<(RawDevice device, RawAep? linkedAep)> relevantDevices = devices
            .Select(device => (device, linkedAep: device.AepId is null
                ? null
                : aeps.FirstOrDefault(aep => string.Equals(device.AepId, aep.Id, StringComparison.Ordinal))))
            .Where(item =>
                (target.ContainerId is not null && item.device.ContainerId is not null
                    && string.Equals(target.ContainerId, item.device.ContainerId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.device.AepId, target.Id, StringComparison.Ordinal)
                || (item.linkedAep is not null && CompareContainer(target, item.linkedAep, containers).same == "same"))
            .ToList();

        foreach ((RawDevice device, RawAep? linkedAep) in relevantDevices)
        {
            report.Devices.Add(new DeviceRecord
            {
                IdHash = hasher.HashId(device.Info.Id),
                NameHash = hasher.HashName(device.Name),
                Protocol = ProtocolLabel(linkedAep?.ProtocolId),
                IsPaired = linkedAep?.IsPaired,
                IsPresent = linkedAep?.IsPresent ?? ReadBool(device.Info, "System.Devices.Present"),
                ContainerIdHash = device.ContainerId is null ? null : hasher.HashContainerId(device.ContainerId),
                AepIdHash = device.AepId is null ? null : hasher.HashId(device.AepId),
                AepIdMatchesTarget = target is null || device.AepId is null
                    ? null
                    : string.Equals(device.AepId, target.Id, StringComparison.Ordinal),
                AepIdMatchesEnumeratedEndpoint = device.AepId is null
                    ? null
                    : linkedAep is not null,
                SameContainerAsTarget = CompareDeviceContainer(target, device),
                Properties = SnapshotProperties(device.Info, DeviceProperties, hasher),
            });
        }

        AssessOutcome(report, target!, aeps, containers);
        report.PairedLeAepCount = aeps.Count(aep => aep.ProtocolId == LeProtocolId && aep.IsPaired == true);
        report.SameContainerLeAepCount = aeps.Count(aep =>
            aep.ProtocolId == LeProtocolId
            && CompareContainer(target, aep, containers).same == "same");
        report.SameContainerPairedLeAepCount = aeps.Count(aep =>
            aep.ProtocolId == LeProtocolId
            && aep.IsPaired == true
            && CompareContainer(target, aep, containers).same == "same");
        report.FinishedUtc = DateTimeOffset.UtcNow;
        report.ExitCode = report.Enumerations.All(status => status.Status == "Completed") ? 0 : 1;

        string jsonPath = Path.Combine(outputDirectory, "result.json");
        string summaryPath = Path.Combine(outputDirectory, "summary.txt");
        try
        {
            string json = JsonSerializer.Serialize(report, JsonOptions);
            string summary = BuildSummary(report, outputDirectory);
            await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);
            await File.WriteAllTextAsync(summaryPath, summary, Encoding.UTF8);
            Console.WriteLine(summary);
            Console.WriteLine($"Results folder: {outputDirectory}");
            return report.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Could not write the sanitized inventory files ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static void PrintRunScope(string targetIdHash)
    {
        Console.WriteLine("DeviceIdentityInventory enumeration plan");
        Console.WriteLine("API: DeviceInformation.FindAllAsync(string, IEnumerable<string>, DeviceInformationKind)");
        Console.WriteLine("Kinds: AssociationEndpoint, AssociationEndpointContainer, Device");
        Console.WriteLine("AQS selector for each kind: empty string (no property filter)");
        Console.WriteLine($"Target Classic AEP id hash: {targetIdHash}");
        PrintPropertyKeys();
    }

    private static async Task<(DeviceInformation[] Records, EnumerationStatus Status)> EnumerateAsync(
        DeviceInformationKind kind,
        string[] properties)
    {
        try
        {
            DeviceInformationCollection collection = await DeviceInformation.FindAllAsync(
                string.Empty,
                properties,
                kind);
            return (
                collection.ToArray(),
                new EnumerationStatus(kind.ToString(), "", "Completed", collection.Count));
        }
        catch (Exception exception)
        {
            return (
                [],
                new EnumerationStatus(kind.ToString(), "", "Failed", null, exception.GetType().Name));
        }
    }

    private static RawAep CreateRawAep(DeviceInformation info)
    {
        return new RawAep(
            info,
            info.Id,
            info.Name,
            ReadString(info, AepContainerIdKey),
            ReadProtocolId(info, AepProtocolIdKey),
            ReadBool(info, AepIsPairedKey),
            ReadBool(info, AepIsPresentKey),
            ReadBool(info, AepIsConnectedKey),
            ReadBool(info, AepCanPairKey));
    }

    private static RawContainer CreateRawContainer(DeviceInformation info)
    {
        string? containerId = ReadString(info, "System.Devices.AepContainer.ContainerId");
        if (string.IsNullOrWhiteSpace(containerId))
            containerId = Guid.TryParse(info.Id, out Guid parsedId) ? parsedId.ToString("D") : null;

        return new RawContainer(
            info,
            info.Name,
            containerId,
            ReadStrings(info, "System.Devices.AepContainer.Children"),
            ReadProtocolIds(info, "System.Devices.AepContainer.ProtocolIds"),
            ReadBool(info, "System.Devices.AepContainer.IsPaired"),
            ReadBool(info, "System.Devices.AepContainer.IsPresent"),
            ReadBool(info, "System.Devices.AepContainer.CanPair"));
    }

    private static RawDevice CreateRawDevice(DeviceInformation info)
        => new(
            info,
            info.Name,
            ReadString(info, "System.Devices.ContainerId"),
            ReadString(info, AepAepIdKey));

    private static void AssessOutcome(
        InventoryReport report,
        RawAep target,
        IReadOnlyList<RawAep> aeps,
        IReadOnlyList<RawContainer> containers)
    {
        bool allEnumerationsCompleted = report.Enumerations.All(status => status.Status == "Completed");

        List<RawAep> leAeps = aeps.Where(aep => aep.ProtocolId == LeProtocolId).ToList();
        var sameContainerLe = leAeps
            .Select(aep => (Aep: aep, Relation: CompareContainer(target, aep, containers)))
            .Where(item => item.Relation.same == "same")
            .ToList();

        if (sameContainerLe.Count > 0)
        {
            report.Outcome = "CONFIRMED";
            report.OutcomeBasis = sameContainerLe.Any(item => item.Aep.IsPaired == true)
                ? "At least one Bluetooth LE AssociationEndpoint shares the target's AssociationEndpoint container, and at least one such record is paired."
                : "At least one Bluetooth LE AssociationEndpoint shares the target's AssociationEndpoint container; its paired status is reported separately.";
            return;
        }

        bool containerListsLe = containers.Any(container =>
            ((target.ContainerId is not null && container.ContainerId is not null
                && string.Equals(container.ContainerId, target.ContainerId, StringComparison.OrdinalIgnoreCase))
                || container.Children.Contains(target.Id, StringComparer.Ordinal))
            && container.ProtocolIds.Contains(LeProtocolId));
        if (containerListsLe)
        {
            report.Outcome = "CANDIDATE";
            report.OutcomeBasis = "The target container reports Bluetooth LE as a contributing protocol, but no same-container LE AEP record was enumerated.";
            return;
        }

        if (target.ContainerId is null
            || leAeps.Any(aep => CompareContainer(target, aep, containers).same == "unknown"))
        {
            report.Outcome = "CANDIDATE";
            report.OutcomeBasis = "A container id was unavailable for the target or an LE AEP, so the same-container relationship could not be established.";
            return;
        }

        if (!allEnumerationsCompleted)
        {
            report.Outcome = "CANDIDATE";
            report.OutcomeBasis = "No same-container LE AEP or container protocol evidence was found, and at least one record kind could not be fully enumerated.";
            return;
        }

        report.Outcome = "NONE";
        report.OutcomeBasis = "No Bluetooth LE AEP or container protocol evidence shared the target's container.";
    }

    private static (string? same, string? evidence) CompareContainer(
        RawAep? target,
        RawAep candidate,
        IReadOnlyList<RawContainer> containers)
    {
        if (target is null)
            return (null, null);

        if (target.ContainerId is not null && candidate.ContainerId is not null)
            return string.Equals(target.ContainerId, candidate.ContainerId, StringComparison.OrdinalIgnoreCase)
                ? ("same", "containerId")
                : ("different", "containerId");

        if (ContainerChildrenContainPair(target, candidate, containers))
            return ("same", "containerChildren");

        return ("unknown", "containerIdUnavailable");
    }

    private static bool ContainerChildrenContainPair(
        RawAep target,
        RawAep candidate,
        IEnumerable<RawContainer> containers)
        => containers.Any(container =>
            container.Children.Contains(target.Id, StringComparer.Ordinal)
            && container.Children.Contains(candidate.Id, StringComparer.Ordinal));

    private static string? CompareDeviceContainer(RawAep? target, RawDevice device)
    {
        if (target is null || target.ContainerId is null || device.ContainerId is null)
            return "unknown";
        return string.Equals(target.ContainerId, device.ContainerId, StringComparison.OrdinalIgnoreCase)
            ? "same"
            : "different";
    }

    private static bool IsClassic(DeviceInformation info)
        => ReadProtocolId(info, AepProtocolIdKey) == ClassicProtocolId;

    private static Guid? ReadProtocolId(DeviceInformation info, string propertyKey)
    {
        object? value = ReadProperty(info, propertyKey);
        if (value is Guid guid)
            return guid;
        return value is string text && Guid.TryParse(text, out Guid parsed) ? parsed : null;
    }

    private static string? ProtocolLabel(Guid? protocolId)
    {
        if (protocolId is null)
            return null;
        if (protocolId.Value == ClassicProtocolId)
            return "Bluetooth Classic";
        if (protocolId.Value == LeProtocolId)
            return "Bluetooth LE";
        return "Other protocol";
    }

    private static string? SafeProtocolId(Guid? protocolId, IdentityHasher hasher)
    {
        if (protocolId is null)
            return null;
        Guid value = protocolId.Value;
        if (value == ClassicProtocolId)
            return "Bluetooth Classic";
        if (value == LeProtocolId)
            return "Bluetooth LE";
        return $"other:{hasher.HashProperty(AepProtocolIdKey, value.ToString("D"))}";
    }

    private static Dictionary<string, object?> SnapshotProperties(
        DeviceInformation info,
        IEnumerable<string> keys,
        IdentityHasher hasher)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string key in keys)
            snapshot[key] = ReadProperty(info, key) is { } value
                ? SanitizeValue(key, value, hasher)
                : null;
        return snapshot;
    }

    private static object? SanitizeValue(string key, object value, IdentityHasher hasher)
    {
        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return value;

        if (IsProtocolProperty(key))
        {
            if (value is Guid protocolGuid)
                return SafeProtocolId(protocolGuid, hasher);
            if (value is string protocolText && Guid.TryParse(protocolText, out Guid parsedProtocol))
                return SafeProtocolId(parsedProtocol, hasher);
        }

        if (value is Guid guid)
            return SanitizeText(key, guid.ToString("D"), hasher);
        if (value is string text)
            return SanitizeText(key, text, hasher);
        if (value is DateTimeOffset dateTimeOffset)
            return dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
        if (value is DateTime dateTime)
            return dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (value is byte[] bytes)
            return hasher.HashProperty(key, Convert.ToHexString(bytes));

        if (value is IEnumerable values)
        {
            var sanitized = new List<object?>();
            foreach (object? item in values)
            {
                if (item is null)
                    sanitized.Add(null);
                else if (item is Guid itemGuid && IsProtocolProperty(key))
                    sanitized.Add(SafeProtocolId(itemGuid, hasher));
                else if (item is Guid nonProtocolGuid)
                    sanitized.Add(SanitizeText(key, nonProtocolGuid.ToString("D"), hasher));
                else if (item is string itemText)
                    sanitized.Add(SanitizeText(key, itemText, hasher));
                else if (item is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                    sanitized.Add(item);
                else
                    sanitized.Add(hasher.HashProperty(key, item.GetType().FullName));
            }
            return sanitized;
        }

        return hasher.HashProperty(key, value.GetType().FullName);
    }

    private static string SanitizeText(string key, string value, IdentityHasher hasher)
    {
        if (key.Contains("ContainerId", StringComparison.OrdinalIgnoreCase))
            return hasher.HashContainerId(value);
        if (key.Contains("DeviceAddress", StringComparison.OrdinalIgnoreCase))
            return hasher.HashAddress(value);
        if (key.EndsWith("AepId", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("DeviceInstanceId", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Parent", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Children", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("HardwareIds", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("CompatibleIds", StringComparison.OrdinalIgnoreCase))
            return hasher.HashId(value);
        if (key.Equals(ItemNameKey, StringComparison.OrdinalIgnoreCase)
            || key.Contains("Name", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Manufacturer", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Category", StringComparison.OrdinalIgnoreCase))
            return hasher.HashName(value);
        return hasher.HashProperty(key, value);
    }

    private static bool IsProtocolProperty(string key)
        => key.EndsWith("ProtocolId", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("ProtocolIds", StringComparison.OrdinalIgnoreCase);

    private static object? ReadProperty(DeviceInformation info, string key)
        => info.Properties.TryGetValue(key, out object? value) ? value : null;

    private static string? ReadString(DeviceInformation info, string key)
    {
        object? value = ReadProperty(info, key);
        return value switch
        {
            null => null,
            string text => text,
            Guid guid => guid.ToString("D"),
            _ => null,
        };
    }

    private static bool? ReadBool(DeviceInformation info, string key)
        => ReadProperty(info, key) is bool value ? value : null;

    private static string[] ReadStrings(DeviceInformation info, string key)
    {
        object? value = ReadProperty(info, key);
        return value switch
        {
            string[] strings => strings,
            IEnumerable<string> strings => strings.ToArray(),
            _ => [],
        };
    }

    private static Guid[] ReadProtocolIds(DeviceInformation info, string key)
    {
        object? value = ReadProperty(info, key);
        return value switch
        {
            Guid[] ids => ids,
            IEnumerable<Guid> ids => ids.ToArray(),
            string[] ids => ids.Select(text => Guid.TryParse(text, out Guid parsed) ? parsed : Guid.Empty)
                .Where(id => id != Guid.Empty).ToArray(),
            _ => [],
        };
    }

    private static string BuildSummary(InventoryReport report, string outputDirectory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DeviceIdentityInventory result");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Started UTC: {report.StartedUtc:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Finished UTC: {report.FinishedUtc:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Target Classic AEP id hash: {report.TargetAepIdHash}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Target is paired Classic AEP: {report.TargetIsPairedClassicAep?.ToString() ?? "unknown"}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Target validation: {report.TargetValidation}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Target container id hash: {report.TargetContainerIdHash ?? "unavailable"}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Paired Bluetooth LE AEPs enumerated: {report.PairedLeAepCount}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Bluetooth LE AEPs in target container: {report.SameContainerLeAepCount}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Paired Bluetooth LE AEPs in target container: {report.SameContainerPairedLeAepCount}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Outcome: {report.Outcome}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Basis: {report.OutcomeBasis}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Exit code: {report.ExitCode}");
        builder.AppendLine();
        builder.AppendLine("API calls:");
        foreach (string api in report.ApiCalls)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {api}");
        builder.AppendLine("Selectors:");
        foreach ((string kind, string selector) in report.Selectors)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {kind}: {(selector.Length == 0 ? "empty (no AQS filter)" : selector)}");
        builder.AppendLine("Property keys requested:");
        foreach ((string kind, string[] keys) in report.PropertyKeys)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {kind}");
            foreach (string key in keys)
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - {key}");
        }
        builder.AppendLine("Enumeration status:");
        foreach (EnumerationStatus status in report.Enumerations)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {status.Kind}: {status.Status}; count={status.Count?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; error={status.ErrorType ?? "none"}");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"AssociationEndpoint records: {report.AssociationEndpoints.Count}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"AssociationEndpointContainer records: {report.AssociationEndpointContainers.Count}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Device records: {report.Devices.Count}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Results folder: {outputDirectory}");
        builder.AppendLine("Raw names, record ids, container ids, and device addresses are not stored in these files.");
        return builder.ToString();
    }

    private sealed record RawAep(
        DeviceInformation Info,
        string Id,
        string? Name,
        string? ContainerId,
        Guid? ProtocolId,
        bool? IsPaired,
        bool? IsPresent,
        bool? IsConnected,
        bool? CanPair);

    private sealed record RawContainer(
        DeviceInformation Info,
        string? Name,
        string? ContainerId,
        string[] Children,
        Guid[] ProtocolIds,
        bool? IsPaired,
        bool? IsPresent,
        bool? CanPair);

    private sealed record RawDevice(
        DeviceInformation Info,
        string? Name,
        string? ContainerId,
        string? AepId);
}
