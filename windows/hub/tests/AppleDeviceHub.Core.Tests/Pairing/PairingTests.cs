using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Pairing;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Pairing;

public class PairingTests
{
    private sealed class FakeDiscovery : IPairingDiscovery
    {
        private List<PairingCandidate> _candidates = new();
        public TaskCompletionSource<PairingOutcome> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCalls;
        public int StopCalls;
        public int PairCalls;
        public CancellationToken LastToken;

        public event EventHandler? CandidatesChanged;
        public IReadOnlyList<PairingCandidate> Candidates => _candidates;
        public bool IsRunning { get; private set; }

        public void Start() { StartCalls++; IsRunning = true; }
        public void Stop() { StopCalls++; IsRunning = false; }

        public void SetCandidates(params PairingCandidate[] candidates)
        {
            _candidates = candidates.ToList();
            CandidatesChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task<PairingOutcome> PairAsync(string candidateId, CancellationToken cancellationToken)
        {
            PairCalls++;
            LastToken = cancellationToken;
            using var reg = cancellationToken.Register(() => Pending.TrySetResult(PairingOutcome.Canceled));
            return await Pending.Task;
        }

        public void Dispose() { }
    }

    private static readonly PairingCandidate Pods = new("id1", "Sam's AirPods Pro", -50);

    private static (PairingSessionViewModel Vm, FakeDiscovery Discovery, List<PairingCandidate> Adopted, FakeTimeProvider Time) Make(bool adoptResult = true)
    {
        var discovery = new FakeDiscovery();
        var adopted = new List<PairingCandidate>();
        var time = new FakeTimeProvider();
        var vm = new PairingSessionViewModel(discovery, a => a(), (candidate, _) => { adopted.Add(candidate); return Task.FromResult(adoptResult); }, time);
        discovery.SetCandidates(Pods);
        return (vm, discovery, adopted, time);
    }

    [Theory]
    [InlineData("AirPods Pro", null, true)]
    [InlineData("Beats Studio Buds", null, true)]
    [InlineData("Powerbeats Pro", 4, true)]
    [InlineData("Sam's AirPods Pro", 4, true)]
    [InlineData("WH-1000XM5", null, false)]
    [InlineData(null, null, false)]
    [InlineData("AirPods Pro", 1, false)]
    public void Filter_accepts_only_apple_audio_names_with_audio_class(string? name, int? cod, bool expected)
        => Assert.Equal(expected, PairingCandidateFilter.IsCandidate(name, cod));

    [Fact]
    public void Order_puts_strongest_signal_first_unknown_last_and_removes_duplicate_ids()
    {
        var ordered = PairingCandidateFilter.Order(new[]
        {
            new PairingCandidate("a", "Zed AirPods", -70),
            new PairingCandidate("b", "Beats Fit", null),
            new PairingCandidate("c", "AirPods", -40),
            new PairingCandidate("c", "AirPods again", -30),
            new PairingCandidate("d", "AirPods Max", -70),
        });
        Assert.Equal(new[] { "c", "d", "a", "b" }, ordered.Select(c => c.Id));
    }

    [Fact]
    public void Messages_are_filled_in_for_every_outcome_except_canceled()
    {
        foreach (var outcome in Enum.GetValues<PairingOutcome>())
        {
            var text = PairingMessages.Describe(outcome, "Pods");
            if (outcome == PairingOutcome.Canceled) Assert.Equal("", text);
            else Assert.Contains("Pods", text);
        }
    }

    [Fact]
    public async Task Happy_path_goes_searching_pairing_connecting_paired_and_stops_discovery()
    {
        var (vm, discovery, adopted, _) = Make();
        var states = new List<PairingState>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.State)) states.Add(vm.State); };

        vm.Activate();
        Assert.Equal(PairingState.Searching, vm.State);
        Assert.Equal("Sam's AirPods Pro is ready to pair.", vm.StatusText);
        Assert.True(vm.HasCandidates);

        var task = vm.PairAsync("id1");
        Assert.Equal(PairingState.Pairing, vm.State);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanPair);
        Assert.Equal("id1", vm.PairingCandidateId);
        Assert.False(discovery.IsRunning);

        discovery.Pending.SetResult(PairingOutcome.Paired);
        await task;

        Assert.Equal(new[] { PairingState.Searching, PairingState.Pairing, PairingState.Connecting, PairingState.Paired }, states);
        Assert.Equal(new[] { Pods }, adopted);
        Assert.Equal("Sam's AirPods Pro is paired and ready.", vm.StatusText);
        Assert.False(discovery.IsRunning);
        Assert.True(vm.CanPair);
    }

    [Fact]
    public async Task Adopter_receives_the_candidate_that_was_paired()
    {
        var (vm, discovery, adopted, _) = Make();
        vm.Activate();
        var task = vm.PairAsync("id1");
        discovery.Pending.SetResult(PairingOutcome.Paired);
        await task;
        var received = Assert.Single(adopted);
        Assert.Equal("id1", received.Id);
        Assert.Equal("Sam's AirPods Pro", received.Name);
    }

    [Fact]
    public async Task Adoption_timeout_cancels_the_adopter_token_and_ends_paired_with_the_refresh_hint()
    {
        var discovery = new FakeDiscovery();
        var time = new FakeTimeProvider();
        CancellationToken seen = default;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new PairingSessionViewModel(discovery, a => a(), async (_, token) =>
        {
            seen = token;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, time);
        discovery.SetCandidates(Pods);
        vm.Activate();
        var task = vm.PairAsync("id1");
        discovery.Pending.SetResult(PairingOutcome.Paired);
        await started.Task;
        Assert.False(seen.IsCancellationRequested);

        time.Advance(PairingSessionViewModel.AdoptTimeout + TimeSpan.FromSeconds(1));
        await task;

        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(PairingState.Paired, vm.State);
        Assert.Contains("click Refresh", vm.StatusText);
    }

    [Fact]
    public async Task Unadopted_device_gets_the_refresh_hint()
    {
        var (vm, discovery, _, _) = Make(adoptResult: false);
        vm.Activate();
        var task = vm.PairAsync("id1");
        discovery.Pending.SetResult(PairingOutcome.Paired);
        await task;
        Assert.Equal(PairingState.Paired, vm.State);
        Assert.Contains("click Refresh", vm.StatusText);
    }

    [Fact]
    public async Task Rejected_pairing_shows_the_message_and_restarts_discovery()
    {
        var (vm, discovery, adopted, _) = Make();
        vm.Activate();
        var task = vm.PairAsync("id1");
        discovery.Pending.SetResult(PairingOutcome.Rejected);
        await task;

        Assert.Equal(PairingState.Failed, vm.State);
        Assert.Equal(PairingMessages.Describe(PairingOutcome.Rejected, "Sam's AirPods Pro"), vm.StatusText);
        Assert.True(discovery.IsRunning);
        Assert.Empty(adopted);
        Assert.True(vm.CanPair);
    }

    [Fact]
    public async Task Pairing_that_takes_too_long_ends_as_timed_out()
    {
        var (vm, discovery, _, time) = Make();
        vm.Activate();
        var task = vm.PairAsync("id1");
        time.Advance(PairingSessionViewModel.PairTimeout + TimeSpan.FromSeconds(1));
        await task;

        Assert.Equal(PairingState.Failed, vm.State);
        Assert.Equal(PairingMessages.Describe(PairingOutcome.TimedOut, "Sam's AirPods Pro"), vm.StatusText);
        Assert.True(discovery.IsRunning);
    }

    [Fact]
    public async Task A_late_result_after_deactivate_changes_nothing()
    {
        var (vm, discovery, adopted, _) = Make();
        vm.Activate();
        var task = vm.PairAsync("id1");
        vm.Deactivate();
        Assert.Equal(PairingState.Idle, vm.State);

        discovery.Pending.TrySetResult(PairingOutcome.Paired);
        await task;

        Assert.Equal(PairingState.Idle, vm.State);
        Assert.Empty(adopted);
        Assert.False(discovery.IsRunning);
    }

    [Fact]
    public async Task A_second_pair_while_busy_is_ignored()
    {
        var (vm, discovery, _, _) = Make();
        vm.Activate();
        var first = vm.PairAsync("id1");
        await vm.PairAsync("id1");
        Assert.Equal(1, discovery.PairCalls);
        discovery.Pending.SetResult(PairingOutcome.Paired);
        await first;
    }

    [Fact]
    public void Activate_and_deactivate_are_idempotent()
    {
        var (vm, discovery, _, _) = Make();
        vm.Activate();
        vm.Activate();
        Assert.Equal(1, discovery.StartCalls);
        vm.Deactivate();
        vm.Deactivate();
        Assert.Equal(1, discovery.StopCalls);
    }

    [Fact]
    public void Candidate_changes_update_the_list_and_status()
    {
        var (vm, discovery, _, _) = Make();
        vm.Activate();
        discovery.SetCandidates(Pods, new PairingCandidate("id2", "AirPods", -80));
        Assert.Equal(2, vm.Candidates.Count);
        Assert.Equal("Found 2 ready to pair.", vm.StatusText);
        discovery.SetCandidates();
        Assert.False(vm.HasCandidates);
        Assert.StartsWith("Looking for AirPods", vm.StatusText);
    }

    [Fact]
    public async Task Demo_discovery_shows_a_candidate_after_two_seconds_and_pairs_it()
    {
        var time = new FakeTimeProvider();
        using var demo = new DemoPairingDiscovery(time);
        var changes = 0;
        demo.CandidatesChanged += (_, _) => changes++;

        demo.Start();
        demo.Start();
        Assert.Empty(demo.Candidates);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(demo.Candidates);
        Assert.Equal("AirPods Pro", demo.Candidates[0].Name);

        var pairing = demo.PairAsync("demo-airpods-pro", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(PairingOutcome.Paired, await pairing);
        Assert.Empty(demo.Candidates);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Demo_discovery_can_appear_immediately()
    {
        using var demo = new DemoPairingDiscovery(new FakeTimeProvider(), appearImmediately: true);
        demo.Start();
        Assert.Single(demo.Candidates);
    }

    [Fact]
    public async Task Demo_pairing_reports_canceled_when_the_token_is_canceled()
    {
        var time = new FakeTimeProvider();
        using var demo = new DemoPairingDiscovery(time, appearImmediately: true);
        using var cts = new CancellationTokenSource();
        var pairing = demo.PairAsync("demo-airpods-pro", cts.Token);
        cts.Cancel();
        Assert.Equal(PairingOutcome.Canceled, await pairing);
    }

    [Fact]
    public void Welcome_page_four_parses()
        => Assert.Equal(3, WelcomePolicy.ParseStartPage(new[] { "--welcome-page=4" }));

    [Fact]
    public void InAppSteps_replaces_only_the_windows_step()
    {
        var setup = PairingInstructions.ForModel(0x0220);
        var steps = PairingInstructions.InAppSteps(setup);
        Assert.Equal(setup.Steps.Count, steps.Count);
        Assert.Equal(setup.Steps.Take(steps.Count - 1), steps.Take(steps.Count - 1));
        Assert.Equal("Wait for them to appear below, then click Pair.", steps[^1]);
        Assert.StartsWith("In Windows, open Bluetooth settings", setup.Steps[^1]);

        var other = new PairingGuide("x", "X", "X", ["One.", "Two."], []);
        Assert.Equal(other.Steps, PairingInstructions.InAppSteps(other));
    }
}
