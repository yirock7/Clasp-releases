using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>
/// The wait that lets a Stop or Dispose end a start stuck in a slow radio check. The WinRT source
/// itself needs Windows types and cannot be hosted by this project, so its use of this helper is
/// not covered here.
/// </summary>
public class StopSignalTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);

    private static Task<string?> NeverAnswers() => new TaskCompletionSource<string?>().Task;

    [Fact]
    public async Task A_stop_ends_a_wait_on_a_check_that_never_answers()
    {
        var signal = new StopSignal();
        var wait = StopSignal.RunUnlessSignalledAsync(NeverAnswers, signal.Current);
        Assert.False(wait.IsCompleted);

        signal.Replace()();

        var (completed, _) = await wait.WaitAsync(Prompt);
        Assert.False(completed);
    }

    [Fact]
    public async Task A_check_that_blocks_synchronously_is_still_interrupted()
    {
        var signal = new StopSignal();
        using var release = new ManualResetEventSlim();
        var wait = StopSignal.RunUnlessSignalledAsync<string?>(() => { release.Wait(); return Task.FromResult<string?>(null); }, signal.Current);

        signal.Replace()();

        var (completed, _) = await wait.WaitAsync(Prompt);
        Assert.False(completed);
        release.Set();
    }

    [Fact]
    public async Task A_check_that_answers_first_returns_its_result()
    {
        var signal = new StopSignal();
        var (completed, result) = await StopSignal.RunUnlessSignalledAsync(() => Task.FromResult<string?>("Bluetooth is turned off."), signal.Current)
            .WaitAsync(Prompt);
        Assert.True(completed);
        Assert.Equal("Bluetooth is turned off.", result);
    }

    [Fact]
    public async Task A_wait_begun_after_a_stop_is_not_cancelled_by_it()
    {
        var signal = new StopSignal();
        signal.Replace()();

        var wait = StopSignal.RunUnlessSignalledAsync(NeverAnswers, signal.Current);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        signal.Replace()();
        var (completed, _) = await wait.WaitAsync(Prompt);
        Assert.False(completed);
    }
}
