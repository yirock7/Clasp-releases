namespace AppleDeviceHub.Core.Audio;

/// <summary>Where a rule sends an application's audio.</summary>
public enum PerAppTargetKind
{
    /// <summary>Follow the Windows default output (clears any per-app choice).</summary>
    Default = 0,
    /// <summary>The explicitly selected AirPods output, while it is available.</summary>
    AirPods = 1,
    /// <summary>A specific Windows output chosen by endpoint id.</summary>
    Speakers = 2,
}

/// <summary>
/// One user rule. <see cref="ExecutableName"/> matches case-insensitively with or without ".exe".
/// <see cref="EndpointId"/> is only used by <see cref="PerAppTargetKind.Speakers"/>.
/// </summary>
public sealed record PerAppRule(string ExecutableName, PerAppTargetKind Target, string? EndpointId = null);

/// <summary>A running process, as seen by the injected process source.</summary>
public sealed record PerAppProcess(int ProcessId, string ExecutableName);

/// <summary>Supplies the currently running processes. Injected so the policy is testable.</summary>
public interface IRunningProcessSource
{
    IReadOnlyList<PerAppProcess> GetRunning();
}

public enum PerAppActionKind { SetOutput = 0, ClearOutput = 1 }

/// <summary>One routing change the policy wants. <see cref="EndpointId"/> is null for a clear.</summary>
public sealed record PerAppAction(int ProcessId, string ExecutableName, PerAppActionKind Kind, string? EndpointId);

public enum PerAppRoutingStatus
{
    Success = 0,
    /// <summary>Windows does not expose the undocumented interface (or its layout failed the safety probe).</summary>
    Unsupported = 1,
    InvalidArgument = 2,
    Failed = 3,
}

/// <summary>Outcome of one routing call. Never thrown; <see cref="HResult"/> is 0 when not applicable.</summary>
public sealed record PerAppRoutingResult(PerAppRoutingStatus Status, int HResult, string Detail, string? EndpointId = null)
{
    public bool Succeeded => Status == PerAppRoutingStatus.Success;
}

/// <summary>
/// Windows per-application output choice (Settings > Sound > Volume mixer). Undocumented;
/// every member returns a result instead of throwing. Endpoint ids are ordinary MMDevice ids;
/// the implementation converts them to the persisted form.
/// </summary>
public interface IPerAppRouting : IDisposable
{
    bool IsSupported { get; }
    PerAppRoutingResult SetAppOutput(int processId, string endpointId);
    PerAppRoutingResult ClearAppOutput(int processId);
    PerAppRoutingResult GetAppOutput(int processId);
}
