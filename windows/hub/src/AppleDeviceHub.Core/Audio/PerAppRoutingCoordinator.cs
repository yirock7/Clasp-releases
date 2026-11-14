namespace AppleDeviceHub.Core.Audio;

/// <summary>One applied action and what Windows said.</summary>
public sealed record PerAppActionOutcome(PerAppAction Action, PerAppRoutingResult Result);

/// <summary>
/// Runs <see cref="PerAppRoutingPolicy"/> actions through an <see cref="IPerAppRouting"/>. Does
/// nothing at all when the Windows interface is unavailable, so a Windows update that breaks it
/// degrades to "feature inactive".
/// </summary>
public sealed class PerAppRoutingCoordinator(PerAppRoutingPolicy policy, IPerAppRouting routing)
{
    public bool IsSupported => routing.IsSupported;

    public IReadOnlyList<PerAppActionOutcome> Configure(bool enabled, IReadOnlyList<PerAppRule>? rules, string? airPodsEndpointId)
        => Apply(policy.Configure(enabled && IsSupported, rules, airPodsEndpointId));

    public IReadOnlyList<PerAppActionOutcome> OnProcessStarted(PerAppProcess process)
        => IsSupported ? Apply(policy.OnProcessStarted(process)) : [];

    public IReadOnlyList<PerAppActionOutcome> OnAirPodsChanged(string? airPodsEndpointId)
        => IsSupported ? Apply(policy.OnAirPodsChanged(airPodsEndpointId)) : [];

    private List<PerAppActionOutcome> Apply(IReadOnlyList<PerAppAction> actions)
    {
        var outcomes = new List<PerAppActionOutcome>(actions.Count);
        foreach (var action in actions)
        {
            PerAppRoutingResult result;
            try
            {
                result = action.Kind == PerAppActionKind.SetOutput
                    ? routing.SetAppOutput(action.ProcessId, action.EndpointId ?? "")
                    : routing.ClearAppOutput(action.ProcessId);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = new(PerAppRoutingStatus.Failed, exception.HResult, "Per-app routing threw unexpectedly: " + exception.Message);
            }
            if (action.Kind == PerAppActionKind.SetOutput)
                policy.CompleteSet(action, result.Succeeded);
            else
                policy.CompleteClear(action, result.Succeeded);
            outcomes.Add(new(action, result));
        }
        return outcomes;
    }
}
