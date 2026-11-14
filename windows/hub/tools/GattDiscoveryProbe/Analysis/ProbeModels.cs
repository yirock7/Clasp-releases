namespace GattDiscoveryProbe.Analysis;

public sealed record SourceHash(string File, string Sha256);

/// <summary>Metadata for one service instance as returned in one cache mode.</summary>
public sealed class ServiceMetadata
{
    public required string CacheMode { get; init; }
    public required string Uuid { get; init; }
    public required ushort AttributeHandle { get; init; }
    public List<CharacteristicMetadata> Characteristics { get; } = [];
}

public sealed record CharacteristicMetadata(
    string Uuid,
    ushort AttributeHandle,
    string Properties,
    uint PropertiesValue,
    string ProtectionLevel);

/// <summary>
/// One asynchronous operation. GATT calls include CommunicationStatus and ProtocolError from
/// Windows. Paired-device enumeration/resolution calls have those fields null because their
/// Windows APIs do not return GattCommunicationStatus results.
/// </summary>
public sealed class ProbeCall
{
    public required string Operation { get; init; }
    public string? CacheMode { get; init; }
    public string? ServiceUuid { get; init; }
    public string OperationStatus { get; set; } = "Pending";
    public string? CommunicationStatus { get; set; }
    public byte? ProtocolError { get; set; }
    public bool TimedOut { get; set; }
    public bool CallerCancelledWait { get; set; }
    public bool AccessDenied { get; set; }
    public string? ExceptionType { get; set; }
    public long? ElapsedMilliseconds { get; set; }

    public bool IsAccessDenied
        => AccessDenied
            || string.Equals(CommunicationStatus, "AccessDenied", StringComparison.Ordinal)
            || string.Equals(ExceptionType, nameof(UnauthorizedAccessException), StringComparison.Ordinal);

    public bool IsUnreachableOrIncomplete
        => TimedOut
            || CallerCancelledWait
            || string.Equals(CommunicationStatus, "Unreachable", StringComparison.Ordinal)
            || string.Equals(CommunicationStatus, "ProtocolError", StringComparison.Ordinal);

    public bool IsUnexpectedFailure
        => string.Equals(OperationStatus, "Failed", StringComparison.Ordinal)
            && !IsAccessDenied
            && !TimedOut
            && !CallerCancelledWait;
}

/// <summary>Bluetooth-free result model shared by the runtime and pure tests.</summary>
public sealed class ProbeRunResult
{
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string WindowsVersion { get; init; } = "unknown";
    public int WindowsBuild { get; init; }
    public string ToolVersion { get; init; } = "unknown";
    public string? AssemblySha256 { get; init; }
    public List<SourceHash> SourceSha256s { get; init; } = [];
    public string? DeviceNameHash { get; set; }
    public string? DeviceIdHash { get; set; }
    public bool IdentityEnumerationCompleted { get; set; }
    public bool PairedLeIdentityResolved { get; set; }
    public List<ProbeCall> Calls { get; } = [];
    public List<ServiceMetadata> Services { get; } = [];
    public List<string> ServiceCategories { get; set; } = [];
    public List<string> UnclassifiedSigServiceUuids { get; set; } = [];
    public List<LateCompletionRecord> LateCompletions { get; set; } = [];
    public List<string> OutstandingOperationsAtExit { get; set; } = [];
    public int ExitCode { get; set; }
    public string Outcome { get; set; } = "in-progress";
    public string? Detail { get; set; }
}
