using System.Text.Json.Nodes;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;
using AppleDeviceHub.Core.Interop;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Engine;

/// <summary>
/// Every fixtures/sequences file, played by the fake engine through the real transport and the
/// real view model. The fixture's request ids are replaced by the ids the client generated;
/// everything else (seq, session ids, values, times) is sent exactly as written.
/// </summary>
public class SequenceFixtureTests
{
    private static readonly object Gate = new();

    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    public static TheoryData<string> Sequences()
    {
        var data = new TheoryData<string>();
        foreach (var file in EngineFixtures.Files("sequences")) data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public async Task Sequence_fixture_ends_in_the_expected_state(string name)
    {
        var fixture = EngineFixtures.Load("sequences", name);
        var messages = fixture["engine_messages"]!.AsArray().Select(m => m!.DeepClone().AsObject()).ToList();
        int openResult = messages.FindIndex(m => (string?)m["type"] == "result" && m["session_id"] is not null);
        int writeResult = messages.FindIndex(m => (string?)m["type"] == "result" && m["session_id"] is null);
        Assert.Equal("hello_ack", (string?)messages[0]["type"]);
        Assert.True(openResult > 0);

        await using var engine = new FakeEngine();
        engine.OnHello = _ => engine.SendAsync(messages[0]);
        engine.OnRequest = async m =>
        {
            switch ((string?)m["type"])
            {
                case "open_session":
                    messages[openResult]["request_id"] = (string?)m["request_id"];
                    await SendPhase(openResult, writeResult > 0 ? writeResult : messages.Count);
                    break;
                case "set_listening_mode" when writeResult > 0:
                    messages[writeResult]["request_id"] = (string?)m["request_id"];
                    await SendPhase(writeResult, messages.Count);
                    break;
            }
        };

        // The client's own list_devices rechecks take seq numbers the fixture does not know about.
        // Shift each phase above the engine's current seq, keeping the fixture's relative order
        // (including equal and stale seqs within the phase).
        async Task SendPhase(int from, int to)
        {
            long first = (long)messages[from]["seq"]!;
            long offset = Math.Max(0, engine.CurrentSeq + 1 - first);
            for (int i = from; i < to; i++)
            {
                messages[i]["seq"] = (long)messages[i]["seq"]! + offset;
                await engine.SendAsync(messages[i]);
            }
        }

        var transport = new NtpodsDaemonTransport(FakeEngine.Options(engine, EngineSessionMode.Control));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var vm = new HubViewModel(transport, EngineFixtures.DeviceId, Dispatch, time: time);
        await vm.StartAsync();
        await TestSupport.WaitUntil(() => vm.IsConnectActionEnabled);
        Assert.Equal("Start session", vm.ConnectActionText);

        await vm.ConnectAsync();
        await transport.StartAsync(); // flush: a list_devices round trip after everything sent so far
        if (writeResult > 0)
        {
            Assert.True(vm.IsConnected);
            await vm.RequestModeAsync(ListeningMode.Transparency);
            Assert.Single(engine.Received, m => (string?)m["type"] == "set_listening_mode");
            await transport.StartAsync();
        }

        var state = transport.Current!;
        foreach (var (key, value) in fixture["expect"]!.AsObject())
        {
            switch (key)
            {
                case "listening_mode":
                    Assert.Equal(value is null ? null : EngineWire.ParseListeningMode((string)value!), state.Listening.Actual.Value);
                    break;
                case "listening_mode_live":
                    Assert.Equal((bool)value!, state.Listening.Actual.HasValue);
                    break;
                case "session_live":
                    Assert.Equal((bool)value!, state.Link.ProtocolSession.Value == true);
                    Assert.Equal((bool)value!, vm.IsConnected);
                    break;
                case "request_confirmed":
                    if ((bool)value!)
                    {
                        Assert.False(vm.IsModePending);
                        Assert.Equal(ListeningMode.Transparency, vm.ActualMode);
                        Assert.Equal("Transparency", vm.ModeStatusText);
                    }
                    else
                    {
                        // Accepted by the driver, no readback: still waiting, nothing confirmed.
                        Assert.True(vm.IsModePending);
                        Assert.Equal(ListeningMode.NoiseCancellation, vm.ActualMode);
                        Assert.Equal(ListeningMode.Transparency, vm.DisplayedMode);
                    }
                    break;
                case "battery.case.level":
                    AssertBattery(value, state.Battery.Case.Level);
                    break;
                case "battery.case.charging":
                    Assert.Null(value);
                    Assert.False(state.Battery.Case.Charging.HasValue);
                    break;
                case "battery.left.level":
                    AssertBattery(value, state.Battery.Left.Level);
                    break;
                default:
                    Assert.Fail($"{name}: no check for expectation '{key}'.");
                    break;
            }
        }
    }

    private static void AssertBattery(JsonNode? expected, Observation<int> actual)
    {
        if (expected is null) Assert.False(actual.HasValue);
        else Assert.Equal((int)expected, actual.Value);
    }
}
