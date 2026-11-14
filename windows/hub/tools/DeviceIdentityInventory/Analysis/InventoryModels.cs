namespace DeviceIdentityInventory.Analysis;

public sealed record EnumerationStatus(
    string Kind,
    string Selector,
    string Status,
    int? Count,
    string? ErrorType = null);

public sealed class InventoryReport
{
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; set; }
    public string ToolVersion { get; init; } = "1.0.0";
    public string ApiBoundary { get; init; } = "Windows.Devices.Enumeration.DeviceInformation.FindAllAsync only";
    public string TargetAepIdHash { get; set; } = string.Empty;
    public string? TargetContainerIdHash { get; set; }
    public bool? TargetIsPairedClassicAep { get; set; }
    public string TargetValidation { get; set; } = "unverified";
    public List<EnumerationStatus> Enumerations { get; } = [];
    public List<string> ApiCalls { get; } = [];
    public Dictionary<string, string> Selectors { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> PropertyKeys { get; init; } = new(StringComparer.Ordinal);
    public int PairedLeAepCount { get; set; }
    public int SameContainerLeAepCount { get; set; }
    public int SameContainerPairedLeAepCount { get; set; }
    public List<AepRecord> AssociationEndpoints { get; } = [];
    public List<ContainerRecord> AssociationEndpointContainers { get; } = [];
    public List<DeviceRecord> Devices { get; } = [];
    public string Outcome { get; set; } = "NONE";
    public string OutcomeBasis { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}

public sealed class AepRecord
{
    public string Kind { get; init; } = "AssociationEndpoint";
    public required string IdHash { get; init; }
    public string? NameHash { get; init; }
    public string? Protocol { get; init; }
    public bool? IsPaired { get; init; }
    public bool? IsPresent { get; init; }
    public bool? IsConnected { get; init; }
    public bool? CanPair { get; init; }
    public string? ContainerIdHash { get; init; }
    public string? SameContainerAsTarget { get; init; }
    public string? SameContainerEvidence { get; init; }
    public Dictionary<string, object?> Properties { get; init; } = new(StringComparer.Ordinal);
}

public sealed class ContainerRecord
{
    public string Kind { get; init; } = "AssociationEndpointContainer";
    public required string ContainerIdHash { get; init; }
    public string? NameHash { get; init; }
    public bool? IsPaired { get; init; }
    public bool? IsPresent { get; init; }
    public bool? CanPair { get; init; }
    public string? SameContainerAsTarget { get; init; }
    public string[] ChildIdHashes { get; init; } = [];
    public string[] Protocols { get; init; } = [];
    public Dictionary<string, object?> Properties { get; init; } = new(StringComparer.Ordinal);
}

public sealed class DeviceRecord
{
    public string Kind { get; init; } = "Device";
    public required string IdHash { get; init; }
    public string? NameHash { get; init; }
    public string? Protocol { get; init; }
    public bool? IsPaired { get; init; }
    public bool? IsPresent { get; init; }
    public string? ContainerIdHash { get; init; }
    public string? AepIdHash { get; init; }
    public bool? AepIdMatchesTarget { get; init; }
    public bool? AepIdMatchesEnumeratedEndpoint { get; init; }
    public string? SameContainerAsTarget { get; init; }
    public Dictionary<string, object?> Properties { get; init; } = new(StringComparer.Ordinal);
}
