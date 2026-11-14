using AppleDeviceHub.Core.Audio;

namespace AppleDeviceHub.Core.Tests;

public class PhoneStreamingStateTests
{
    [Fact]
    public void Rejected_default_uses_the_plain_language_sentence()
    {
        var state = PhoneStreamingStates.Rejected("Test iPhone", PhoneOpenOutcome.Failed);

        Assert.Equal(PhoneStreamingPhase.Failed, state.Phase);
        Assert.Equal("Your phone didn't accept the connection. Make sure Bluetooth is on and the phone is nearby.", state.Message);
        Assert.Equal("Test iPhone", state.DeviceName);
    }

    [Fact]
    public void Rejected_denied_and_timeout_have_distinct_messages()
    {
        var denied = PhoneStreamingStates.Rejected("Test iPhone", PhoneOpenOutcome.DeniedBySystem).Message;
        var timedOut = PhoneStreamingStates.Rejected("Test iPhone", PhoneOpenOutcome.TimedOut).Message;
        var generic = PhoneStreamingStates.Rejected("Test iPhone", PhoneOpenOutcome.Failed).Message;

        Assert.NotEqual(denied, generic);
        Assert.NotEqual(timedOut, generic);
        Assert.NotEqual(denied, timedOut);
        Assert.Contains("blocked", denied, StringComparison.Ordinal);
        Assert.Contains("timed out", timedOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Streaming_names_the_phone()
    {
        var state = PhoneStreamingStates.Streaming("Test iPhone");

        Assert.Equal(PhoneStreamingPhase.Streaming, state.Phase);
        Assert.Equal("Streaming from Test iPhone", state.Message);
        Assert.True(state.IsActive);
    }

    [Fact]
    public void Blank_or_missing_name_falls_back_to_a_neutral_phrase()
    {
        Assert.Equal("Streaming from your phone", PhoneStreamingStates.Streaming(null).Message);
        Assert.Equal("Connecting to your phone…", PhoneStreamingStates.Connecting("   ").Message);
        Assert.Equal("Your phone didn't accept the connection. Make sure Bluetooth is on and the phone is nearby.",
            PhoneStreamingStates.Rejected("", PhoneOpenOutcome.Failed).Message);
    }

    [Fact]
    public void Name_is_trimmed_before_it_is_shown()
    {
        Assert.Equal("Streaming from Test iPhone", PhoneStreamingStates.Streaming("  Test iPhone  ").Message);
    }

    [Fact]
    public void Ended_is_idle_and_not_active()
    {
        var state = PhoneStreamingStates.Ended("Test iPhone");

        Assert.Equal(PhoneStreamingPhase.Idle, state.Phase);
        Assert.False(state.IsActive);
        Assert.Equal("Streaming from Test iPhone ended. Press Connect to start again.", state.Message);
    }

    [Theory]
    [InlineData(PhoneStreamingPhase.Connecting, true)]
    [InlineData(PhoneStreamingPhase.Streaming, true)]
    [InlineData(PhoneStreamingPhase.Idle, false)]
    [InlineData(PhoneStreamingPhase.Failed, false)]
    [InlineData(PhoneStreamingPhase.Unavailable, false)]
    public void Only_connecting_and_streaming_count_as_active(PhoneStreamingPhase phase, bool expected)
    {
        var state = new PhoneStreamingState(phase, "Test iPhone", "message");

        Assert.Equal(expected, state.IsActive);
    }

    [Fact]
    public void Connect_needs_an_idle_state_and_a_selected_device()
    {
        var idle = PhoneStreamingStates.Idle();
        var streaming = PhoneStreamingStates.Streaming("Test iPhone");

        Assert.True(PhoneStreamingStates.CanConnect(idle, "device-1"));
        Assert.False(PhoneStreamingStates.CanConnect(idle, null));
        Assert.False(PhoneStreamingStates.CanConnect(idle, "  "));
        Assert.False(PhoneStreamingStates.CanConnect(streaming, "device-1"));
    }

    [Fact]
    public void Unavailable_and_no_phones_messages_are_plain_language()
    {
        Assert.Equal(PhoneStreamingPhase.Unavailable, PhoneStreamingStates.Unavailable().Phase);
        Assert.Contains("Bluetooth", PhoneStreamingStates.NoPhonesFound().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_without_audio_streaming_is_reported_with_its_name()
    {
        var state = PhoneStreamingStates.NotSupportedByDevice("Old Speaker");

        Assert.Equal(PhoneStreamingPhase.Failed, state.Phase);
        Assert.Equal("Old Speaker can't stream audio to this PC.", state.Message);
    }

    [Fact]
    public void Normalize_drops_blank_ids_and_duplicates_ignoring_case()
    {
        var list = PhoneDeviceList.Normalize([
            new PhoneStreamingCandidate("", "No id"),
            new PhoneStreamingCandidate("  ", "Blank id"),
            new PhoneStreamingCandidate("DEV-A", "Test iPhone"),
            new PhoneStreamingCandidate("dev-a", "Test iPhone again"),
        ]);

        var only = Assert.Single(list);
        Assert.Equal("DEV-A", only.DeviceId);
        Assert.Equal("Test iPhone", only.Name);
    }

    [Fact]
    public void Normalize_sorts_by_name_then_id_so_the_picker_is_stable()
    {
        var list = PhoneDeviceList.Normalize([
            new PhoneStreamingCandidate("dev-3", "Zoe's iPhone"),
            new PhoneStreamingCandidate("dev-2", null),
            new PhoneStreamingCandidate("dev-1", "anna's iPhone"),
            new PhoneStreamingCandidate("dev-0", null),
        ]);

        // "anna's iPhone" < "your phone" (the unnamed fallback) < "Zoe's iPhone", ignoring case.
        // Unnamed entries keep ID order among themselves.
        Assert.Equal(["dev-1", "dev-0", "dev-2", "dev-3"], list.Select(candidate => candidate.DeviceId).ToArray());
    }

    [Fact]
    public void ChooseSelection_keeps_previous_device_when_it_is_still_listed()
    {
        var list = PhoneDeviceList.Normalize([
            new PhoneStreamingCandidate("dev-1", "A"),
            new PhoneStreamingCandidate("dev-2", "B"),
        ]);

        Assert.Equal("dev-2", PhoneDeviceList.ChooseSelection(list, "DEV-2"));
    }

    [Fact]
    public void ChooseSelection_falls_back_to_first_device_or_null()
    {
        var list = PhoneDeviceList.Normalize([new PhoneStreamingCandidate("dev-1", "A")]);

        Assert.Equal("dev-1", PhoneDeviceList.ChooseSelection(list, "gone"));
        Assert.Equal("dev-1", PhoneDeviceList.ChooseSelection(list, null));
        Assert.Null(PhoneDeviceList.ChooseSelection([], "dev-1"));
    }
}
