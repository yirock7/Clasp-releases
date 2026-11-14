namespace AppleDeviceHub.Core.Audio;

/// <summary>
/// Decides per-app output changes from rules, process starts and AirPods availability. It only
/// returns actions; the caller applies them. Session-only memory: it clears an app only when it
/// previously routed it (or the rule says Default), so the user's own Windows choices survive.
/// Not thread-safe; call from one thread.
/// </summary>
public sealed class PerAppRoutingPolicy(IRunningProcessSource processes)
{
    private readonly Dictionary<int, Applied> _applied = new();
    private readonly Dictionary<int, Applied> _pendingSets = new();
    private IReadOnlyList<PerAppRule> _rules = [];
    private bool _enabled;
    private string? _airPodsEndpointId;

    private sealed record Applied(string ExecutableName, PerAppTargetKind Target, string? EndpointId);

    /// <summary>Number of processes this policy currently holds a route for.</summary>
    public int AppliedCount => _applied.Count;

    /// <summary>
    /// Replaces the rules. Disabling returns clears for everything routed. Enabling or changing rules
    /// re-evaluates every running process.
    /// </summary>
    public IReadOnlyList<PerAppAction> Configure(bool enabled, IReadOnlyList<PerAppRule>? rules, string? airPodsEndpointId)
    {
        _airPodsEndpointId = NormalizeId(airPodsEndpointId);
        if (!enabled)
        {
            var clears = ClearAll();
            _enabled = false;
            _rules = [];
            return clears;
        }
        _enabled = true;
        _rules = rules ?? [];
        return Reevaluate();
    }

    /// <summary>A process (or a new audio session of one) appeared.</summary>
    public IReadOnlyList<PerAppAction> OnProcessStarted(PerAppProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!_enabled) return [];
        var action = Evaluate(process);
        return action is null ? [] : [action];
    }

    /// <summary>A process ended; forget it (a reused id must start fresh). No action is needed.</summary>
    public void OnProcessExited(int processId)
    {
        _applied.Remove(processId);
        _pendingSets.Remove(processId);
    }

    /// <summary>Commits a route only after Windows confirmed the requested write.</summary>
    internal void CompleteSet(PerAppAction action, bool succeeded)
    {
        if (action.Kind != PerAppActionKind.SetOutput || !_pendingSets.TryGetValue(action.ProcessId, out var pending)) return;
        if (!string.Equals(pending.ExecutableName, action.ExecutableName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pending.EndpointId, NormalizeId(action.EndpointId), StringComparison.OrdinalIgnoreCase))
            return;
        _pendingSets.Remove(action.ProcessId);
        if (succeeded) _applied[action.ProcessId] = pending;
    }

    /// <summary>Commits a clear only after Windows confirmed that it reset the process route.</summary>
    internal void CompleteClear(PerAppAction action, bool succeeded)
    {
        if (action.Kind != PerAppActionKind.ClearOutput || !succeeded) return;

        _pendingSets.Remove(action.ProcessId);
        if (_enabled && FindRule(action.ExecutableName)?.Target == PerAppTargetKind.Default)
            _applied[action.ProcessId] = new(action.ExecutableName, PerAppTargetKind.Default, null);
        else
            _applied.Remove(action.ProcessId);
    }

    /// <summary>
    /// AirPods output availability changed. <paramref name="airPodsEndpointId"/> is the current
    /// AirPods render endpoint id when available, otherwise null/empty. On disconnect, apps routed to
    /// the AirPods are reset to Default; on connect they are re-applied.
    /// </summary>
    public IReadOnlyList<PerAppAction> OnAirPodsChanged(string? airPodsEndpointId)
    {
        _airPodsEndpointId = NormalizeId(airPodsEndpointId);
        return _enabled ? Reevaluate() : [];
    }

    private List<PerAppAction> Reevaluate()
    {
        var actions = new List<PerAppAction>();
        var running = processes.GetRunning();
        var livePids = new HashSet<int>(running.Select(p => p.ProcessId));
        foreach (var pid in _applied.Keys.Where(pid => !livePids.Contains(pid)).ToList()) _applied.Remove(pid);
        foreach (var pid in _pendingSets.Keys.Where(pid => !livePids.Contains(pid)).ToList()) _pendingSets.Remove(pid);

        foreach (var process in running)
        {
            var action = Evaluate(process);
            if (action is not null) actions.Add(action);
        }
        // Still tracked but no longer matched by any rule (rule removed): reset what we routed.
        foreach (var (pid, applied) in _applied.ToList())
        {
            if (FindRule(applied.ExecutableName) is not null) continue;
            if (applied.EndpointId is not null)
                actions.Add(new(pid, applied.ExecutableName, PerAppActionKind.ClearOutput, null));
            else
                _applied.Remove(pid);
        }
        foreach (var (pid, pending) in _pendingSets.ToList())
            if (FindRule(pending.ExecutableName) is null) _pendingSets.Remove(pid);
        return actions;
    }

    private PerAppAction? Evaluate(PerAppProcess process)
    {
        if (process.ProcessId <= 0) return null;
        var rule = FindRule(process.ExecutableName);
        if (rule is null) return null;
        _applied.TryGetValue(process.ProcessId, out var current);

        string? desired = rule.Target switch
        {
            PerAppTargetKind.AirPods => _airPodsEndpointId,
            PerAppTargetKind.Speakers => NormalizeId(rule.EndpointId),
            _ => null,
        };

        if (desired is null)
        {
            _pendingSets.Remove(process.ProcessId);
            // Default rule, speakers rule without an id, or AirPods rule while disconnected.
            if (rule.Target == PerAppTargetKind.Default)
            {
                if (current is { EndpointId: null, Target: PerAppTargetKind.Default }) return null;
                return new(process.ProcessId, process.ExecutableName, PerAppActionKind.ClearOutput, null);
            }
            if (current is { EndpointId: not null })
            {
                return new(process.ProcessId, process.ExecutableName, PerAppActionKind.ClearOutput, null);
            }
            return null;
        }

        if (current is not null && string.Equals(current.EndpointId, desired, StringComparison.OrdinalIgnoreCase))
        {
            _pendingSets.Remove(process.ProcessId);
            return null;
        }
        if (_pendingSets.TryGetValue(process.ProcessId, out var pending)
            && string.Equals(pending.EndpointId, desired, StringComparison.OrdinalIgnoreCase)) return null;
        _pendingSets[process.ProcessId] = new(process.ExecutableName, rule.Target, desired);
        return new(process.ProcessId, process.ExecutableName, PerAppActionKind.SetOutput, desired);
    }

    private List<PerAppAction> ClearAll()
    {
        var actions = _applied
            .Where(pair => pair.Value.EndpointId is not null)
            .Select(pair => new PerAppAction(pair.Key, pair.Value.ExecutableName, PerAppActionKind.ClearOutput, null))
            .ToList();
        foreach (var pid in _applied.Where(pair => pair.Value.EndpointId is null).Select(pair => pair.Key).ToList())
            _applied.Remove(pid);
        _pendingSets.Clear();
        return actions;
    }

    private PerAppRule? FindRule(string executableName)
    {
        var name = NormalizeName(executableName);
        if (name.Length == 0) return null;
        // Later rules win, so the last line for an app is its effective rule.
        for (var i = _rules.Count - 1; i >= 0; i--)
            if (string.Equals(NormalizeName(_rules[i].ExecutableName), name, StringComparison.OrdinalIgnoreCase)) return _rules[i];
        return null;
    }

    /// <summary>File name only, lower-case, ".exe" added when there is no extension.</summary>
    public static string NormalizeName(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName)) return "";
        var name = Path.GetFileName(executableName.Trim()).ToLowerInvariant();
        if (name.Length == 0) return "";
        return Path.HasExtension(name) ? name : name + ".exe";
    }

    private static string? NormalizeId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
}
