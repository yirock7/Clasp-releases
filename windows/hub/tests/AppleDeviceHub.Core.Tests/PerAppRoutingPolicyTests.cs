using AppleDeviceHub.Core.Audio;

namespace AppleDeviceHub.Core.Tests;

public class PerAppRoutingPolicyTests
{
    private const string Pods = "{0.0.0.00000000}.{aaaa}";
    private const string Speakers = "{0.0.0.00000000}.{bbbb}";

    private sealed class FakeProcesses : IRunningProcessSource
    {
        public List<PerAppProcess> Running { get; } = [];
        public IReadOnlyList<PerAppProcess> GetRunning() => Running.ToList();
    }

    private sealed class FakeRouting(bool supported = true) : IPerAppRouting
    {
        public List<string> Calls { get; } = [];
        public bool Throw { get; set; }
        public bool FailSet { get; set; }
        public bool FailClear { get; set; }
        public bool IsSupported => supported;
        public PerAppRoutingResult SetAppOutput(int processId, string endpointId)
        {
            if (Throw) throw new InvalidOperationException("boom");
            Calls.Add($"set {processId} {endpointId}");
            if (FailSet) return new(PerAppRoutingStatus.Failed, unchecked((int)0x80004005), "refused");
            return new(PerAppRoutingStatus.Success, 0, "ok", endpointId);
        }
        public PerAppRoutingResult ClearAppOutput(int processId)
        {
            Calls.Add($"clear {processId}");
            if (FailClear) return new(PerAppRoutingStatus.Failed, unchecked((int)0x80004005), "refused");
            return new(PerAppRoutingStatus.Success, 0, "ok");
        }
        public PerAppRoutingResult GetAppOutput(int processId) => new(PerAppRoutingStatus.Success, 0, "ok");
        public void Dispose() { }
    }

    private static (PerAppRoutingPolicy Policy, FakeProcesses Processes) Make()
    {
        var processes = new FakeProcesses();
        return (new PerAppRoutingPolicy(processes), processes);
    }

    private static void ConfirmSets(PerAppRoutingPolicy policy, IEnumerable<PerAppAction> actions)
    {
        foreach (var action in actions.Where(action => action.Kind == PerAppActionKind.SetOutput))
            policy.CompleteSet(action, succeeded: true);
    }

    private static void ConfirmClears(PerAppRoutingPolicy policy, IEnumerable<PerAppAction> actions)
    {
        foreach (var action in actions.Where(action => action.Kind == PerAppActionKind.ClearOutput))
            policy.CompleteClear(action, succeeded: true);
    }

    private static readonly PerAppRule[] Rules =
    [
        new("Spotify.exe", PerAppTargetKind.AirPods),
        new("game", PerAppTargetKind.Speakers, Speakers),
        new("chrome.exe", PerAppTargetKind.Default),
    ];

    [Fact]
    public void Disabled_policy_does_nothing()
    {
        var (policy, _) = Make();
        Assert.Empty(policy.Configure(false, Rules, Pods));
        Assert.Empty(policy.OnProcessStarted(new(10, "spotify.exe")));
        Assert.Empty(policy.OnAirPodsChanged(Pods));
    }

    [Fact]
    public void Matching_process_is_routed_case_insensitively()
    {
        var (policy, _) = Make();
        policy.Configure(true, Rules, Pods);
        var action = Assert.Single(policy.OnProcessStarted(new(10, "SPOTIFY.EXE")));
        Assert.Equal(PerAppActionKind.SetOutput, action.Kind);
        Assert.Equal(Pods, action.EndpointId);
    }

    [Fact]
    public void Rule_name_without_extension_matches_and_unmatched_process_is_ignored()
    {
        var (policy, _) = Make();
        policy.Configure(true, Rules, Pods);
        Assert.Equal(Speakers, Assert.Single(policy.OnProcessStarted(new(11, "Game.exe"))).EndpointId);
        Assert.Empty(policy.OnProcessStarted(new(12, "notepad.exe")));
    }

    [Fact]
    public void Airpods_rule_while_disconnected_does_nothing()
    {
        var (policy, _) = Make();
        policy.Configure(true, Rules, null);
        Assert.Empty(policy.OnProcessStarted(new(10, "spotify.exe")));
        Assert.Equal(0, policy.AppliedCount);
    }

    [Fact]
    public void Repeated_session_for_same_process_is_not_reapplied()
    {
        var (policy, _) = Make();
        policy.Configure(true, Rules, Pods);
        Assert.Single(policy.OnProcessStarted(new(10, "spotify.exe")));
        Assert.Empty(policy.OnProcessStarted(new(10, "spotify.exe")));
    }

    [Fact]
    public void Disconnect_resets_airpods_apps_but_not_speaker_apps_and_reconnect_reapplies()
    {
        var (policy, processes) = Make();
        processes.Running.Add(new(10, "spotify.exe"));
        processes.Running.Add(new(11, "game.exe"));
        var initial = policy.Configure(true, Rules, Pods);
        Assert.Equal(2, initial.Count);
        ConfirmSets(policy, initial);

        var disconnect = policy.OnAirPodsChanged(null);
        var clear = Assert.Single(disconnect);
        Assert.Equal((10, PerAppActionKind.ClearOutput), (clear.ProcessId, clear.Kind));
        ConfirmClears(policy, disconnect);

        var reconnect = policy.OnAirPodsChanged(Pods);
        var set = Assert.Single(reconnect);
        Assert.Equal((10, PerAppActionKind.SetOutput, Pods), (set.ProcessId, set.Kind, set.EndpointId));
    }

    [Fact]
    public void Changed_airpods_endpoint_id_is_reapplied()
    {
        var (policy, processes) = Make();
        processes.Running.Add(new(10, "spotify.exe"));
        policy.Configure(true, Rules, Pods);
        var action = Assert.Single(policy.OnAirPodsChanged("{0.0.0.00000000}.{cccc}"));
        Assert.Equal("{0.0.0.00000000}.{cccc}", action.EndpointId);
    }

    [Fact]
    public void Default_rule_clears_once_per_process()
    {
        var (policy, _) = Make();
        policy.Configure(true, Rules, Pods);
        var clear = Assert.Single(policy.OnProcessStarted(new(20, "chrome.exe")));
        Assert.Equal(PerAppActionKind.ClearOutput, clear.Kind);
        policy.CompleteClear(clear, succeeded: true);
        Assert.Empty(policy.OnProcessStarted(new(20, "chrome.exe")));
    }

    [Fact]
    public void Disabling_clears_only_what_was_routed()
    {
        var (policy, processes) = Make();
        processes.Running.Add(new(10, "spotify.exe"));
        processes.Running.Add(new(20, "chrome.exe"));
        ConfirmSets(policy, policy.Configure(true, Rules, Pods));
        var actions = policy.Configure(false, null, Pods);
        var clear = Assert.Single(actions);
        Assert.Equal(10, clear.ProcessId);
        Assert.Equal(1, policy.AppliedCount);
        policy.CompleteClear(clear, succeeded: true);
        Assert.Equal(0, policy.AppliedCount);
    }

    [Fact]
    public void Removing_a_rule_resets_the_app_it_had_routed()
    {
        var (policy, processes) = Make();
        processes.Running.Add(new(10, "spotify.exe"));
        ConfirmSets(policy, policy.Configure(true, Rules, Pods));
        var actions = policy.Configure(true, [new("game", PerAppTargetKind.Speakers, Speakers)], Pods);
        Assert.Equal(PerAppActionKind.ClearOutput, Assert.Single(actions).Kind);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("disconnect")]
    [InlineData("rule-removal")]
    [InlineData("disable")]
    public void Coordinator_retries_failed_clears_until_windows_confirms(string scenario)
    {
        var (policy, processes) = Make();
        if (scenario != "default") processes.Running.Add(new(10, "spotify.exe"));
        var routing = new FakeRouting();
        var coordinator = new PerAppRoutingCoordinator(policy, routing);
        coordinator.Configure(true, Rules, Pods);
        if (scenario == "default") processes.Running.Add(new(10, "chrome.exe"));

        routing.FailClear = true;
        IReadOnlyList<PerAppActionOutcome> failed = scenario switch
        {
            "default" => coordinator.OnProcessStarted(new(10, "chrome.exe")),
            "disconnect" => coordinator.OnAirPodsChanged(null),
            "rule-removal" => coordinator.Configure(true, [new("game", PerAppTargetKind.Speakers, Speakers)], Pods),
            "disable" => coordinator.Configure(false, null, Pods),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.Equal(PerAppRoutingStatus.Failed, Assert.Single(failed).Result.Status);

        routing.FailClear = false;
        IReadOnlyList<PerAppActionOutcome> retry = scenario switch
        {
            "default" => coordinator.OnProcessStarted(new(10, "chrome.exe")),
            "disconnect" => coordinator.OnAirPodsChanged(null),
            "rule-removal" => coordinator.Configure(true, [new("game", PerAppTargetKind.Speakers, Speakers)], Pods),
            "disable" => coordinator.Configure(false, null, Pods),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.True(Assert.Single(retry).Result.Succeeded);
        Assert.Equal(scenario == "default" ? 1 : 0, policy.AppliedCount);
        Assert.Empty(scenario switch
        {
            "default" => coordinator.OnProcessStarted(new(10, "chrome.exe")),
            "disconnect" => coordinator.OnAirPodsChanged(null),
            "rule-removal" => coordinator.Configure(true, [new("game", PerAppTargetKind.Speakers, Speakers)], Pods),
            "disable" => coordinator.Configure(false, null, Pods),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        });
    }

    [Fact]
    public void Rule_changed_to_speakers_moves_the_running_app()
    {
        var (policy, processes) = Make();
        processes.Running.Add(new(10, "spotify.exe"));
        policy.Configure(true, Rules, Pods);
        var actions = policy.Configure(true, [new("spotify.exe", PerAppTargetKind.Speakers, Speakers)], Pods);
        Assert.Equal(Speakers, Assert.Single(actions).EndpointId);
    }

    [Fact]
    public void Exited_process_is_forgotten_so_a_reused_id_is_routed_again()
    {
        var (policy, _) = Make();
        policy.Configure(true, Rules, Pods);
        policy.OnProcessStarted(new(10, "spotify.exe"));
        policy.OnProcessExited(10);
        Assert.Single(policy.OnProcessStarted(new(10, "spotify.exe")));
    }

    [Fact]
    public void Speakers_rule_without_endpoint_id_and_invalid_pid_do_nothing()
    {
        var (policy, _) = Make();
        policy.Configure(true, [new("a.exe", PerAppTargetKind.Speakers, null)], Pods);
        Assert.Empty(policy.OnProcessStarted(new(5, "a.exe")));
        Assert.Empty(policy.OnProcessStarted(new(0, "a.exe")));
    }

    [Fact]
    public void Last_rule_for_an_app_wins()
    {
        var (policy, _) = Make();
        policy.Configure(true, [new("a.exe", PerAppTargetKind.AirPods), new("A.EXE", PerAppTargetKind.Speakers, Speakers)], Pods);
        Assert.Equal(Speakers, Assert.Single(policy.OnProcessStarted(new(5, "a.exe"))).EndpointId);
    }

    [Fact]
    public void Coordinator_does_nothing_when_unsupported()
    {
        var (policy, processes) = Make();
        processes.Running.Add(new(10, "spotify.exe"));
        var routing = new FakeRouting(supported: false);
        var coordinator = new PerAppRoutingCoordinator(policy, routing);
        Assert.Empty(coordinator.Configure(true, Rules, Pods));
        Assert.Empty(coordinator.OnProcessStarted(new(10, "spotify.exe")));
        Assert.Empty(routing.Calls);
    }

    [Fact]
    public void Coordinator_applies_actions_and_converts_exceptions_to_results()
    {
        var (policy, processes) = Make();
        processes.Running.Add(new(10, "spotify.exe"));
        var routing = new FakeRouting();
        var coordinator = new PerAppRoutingCoordinator(policy, routing);
        var outcomes = coordinator.Configure(true, Rules, Pods);
        Assert.True(Assert.Single(outcomes).Result.Succeeded);
        Assert.Equal([$"set 10 {Pods}"], routing.Calls);

        routing.Throw = true;
        var failed = Assert.Single(coordinator.OnProcessStarted(new(30, "spotify.exe")));
        Assert.Equal(PerAppRoutingStatus.Failed, failed.Result.Status);
        routing.Throw = false;
        Assert.True(Assert.Single(coordinator.OnProcessStarted(new(30, "spotify.exe"))).Result.Succeeded);
        Assert.Empty(coordinator.OnProcessStarted(new(30, "spotify.exe")));
    }

    [Fact]
    public void Coordinator_retries_set_when_windows_rejects_the_route()
    {
        var (policy, _) = Make();
        var routing = new FakeRouting();
        var coordinator = new PerAppRoutingCoordinator(policy, routing);
        coordinator.Configure(true, Rules, Pods);

        routing.FailSet = true;
        var failed = Assert.Single(coordinator.OnProcessStarted(new(30, "spotify.exe")));
        Assert.Equal(PerAppRoutingStatus.Failed, failed.Result.Status);
        Assert.Equal(0, policy.AppliedCount);

        routing.FailSet = false;
        var retried = Assert.Single(coordinator.OnProcessStarted(new(30, "spotify.exe")));
        Assert.True(retried.Result.Succeeded);
        Assert.Equal(1, policy.AppliedCount);
        Assert.Empty(coordinator.OnProcessStarted(new(30, "spotify.exe")));
    }

    [Fact]
    public void Endpoint_id_round_trips_through_persisted_form()
    {
        var persisted = PerAppEndpointId.ToPersisted(Pods);
        Assert.Equal(@"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{aaaa}#{e6327cad-dcec-4949-ae8a-991e976a79d2}", persisted);
        Assert.Equal(Pods, PerAppEndpointId.FromPersisted(persisted));
        Assert.Equal(persisted, PerAppEndpointId.ToPersisted(persisted));
        Assert.Null(PerAppEndpointId.ToPersisted("  "));
        Assert.Equal("", PerAppEndpointId.FromPersisted(null));
    }

    [Fact]
    public void Rule_text_parses_formats_and_reports_bad_lines()
    {
        var rules = PerAppRuleText.Parse(
            "# comment\nspotify.exe=airpods\r\ngame.exe = speakers:" + Speakers + "\nchrome=default\nbad line\nx.exe=speakers:\n..\\evil.exe=airpods\n",
            out var rejected);
        Assert.Equal(3, rules.Count);
        Assert.Equal(PerAppTargetKind.Speakers, rules[1].Target);
        Assert.Equal(Speakers, rules[1].EndpointId);
        Assert.Equal(3, rejected.Count);
        var again = PerAppRuleText.Parse(PerAppRuleText.Format(rules), out var none);
        Assert.Equal(rules, again);
        Assert.Empty(none);
    }
}
