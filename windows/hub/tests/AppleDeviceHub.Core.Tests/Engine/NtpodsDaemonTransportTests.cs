using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;
using AppleDeviceHub.Core.Interop;

namespace AppleDeviceHub.Core.Tests.Engine;

/// <summary>
/// The hardware transport against an in-process fake engine pipe (unique name per test). No real
/// engine, driver or radio is used.
/// </summary>
public class NtpodsDaemonTransportTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);
    private static readonly string[] HelloThenList = ["hello", "list_devices"];
    private static readonly string[] HelloOnly = ["hello"];

    private static async Task<(FakeEngine Engine, NtpodsDaemonTransport Transport)> StartedAsync(
        EngineSessionMode mode = EngineSessionMode.Control, Action<FakeEngine>? setup = null)
    {
        var engine = new FakeEngine();
        engine.AcceptSessions();
        setup?.Invoke(engine);
        var transport = new NtpodsDaemonTransport(FakeEngine.Options(engine, mode));
        await transport.StartAsync();
        return (engine, transport);
    }

    private static Task<CommandResult> Connect(NtpodsDaemonTransport t) => t.SendAsync(new ConnectCommand(Guid.NewGuid()));

    /// <summary>A list_devices round trip: once it returns, every earlier engine message has been processed.</summary>
    private static Task Flush(NtpodsDaemonTransport t) => t.StartAsync();

    // ---- Handshake and identity -----------------------------------------------------------

    [Fact]
    public async Task Handshake_then_device_listing_without_opening_a_session()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;

        Assert.Null(t.UnavailableReason);
        var hello = engine.Received[0];
        Assert.Equal("hello", (string?)hello["type"]);
        Assert.Equal("clasp-engine", (string?)hello["protocol"]);
        Assert.Equal(1, (int)hello["major"]!);
        Assert.Equal(1, (int)hello["minor"]!); // protocol 1.1 client
        Assert.Equal(HelloThenList, engine.Received.Select(m => (string?)m["type"]));

        var state = Assert.IsType<DeviceState>(t.Current);
        Assert.Equal(DataOrigin.Hardware, state.Origin);
        Assert.Equal(NtpodsDaemonTransport.HardwareCapabilities, state.Capabilities);
        Assert.False(state.Supports(DeviceCapabilities.ConversationAwareness));
        Assert.False(state.Supports(DeviceCapabilities.AdaptiveStrength));
        Assert.False(state.Supports(DeviceCapabilities.CaseLidEvents));
        Assert.True(state.Link.Nearby.Value);                 // driver bound: available for a session
        Assert.False(state.Link.ProtocolSession.HasValue);    // no session information yet
        Assert.True(state.Link.AudioConnected.Value);         // windows_connected from the listing
        Assert.False(state.Battery.Left.Level.HasValue);      // nothing observed yet
        Assert.False(state.Listening.Actual.HasValue);
        Assert.Empty(state.Listening.Supported);
        Assert.Equal("Hardware: Clasp engine, control session", t.SourceText);
        Assert.Equal(HubContract.Version, t.Info.ContractVersion);
    }

    [Fact]
    public async Task An_incompatible_major_version_is_reported_without_fallback()
    {
        await using var engine = new FakeEngine();
        engine.OnHello = async _ =>
        {
            await engine.SendAsync(EngineFixtures.Message("error_incompatible_version"));
            engine.DropClient();
        };
        await using var t = new NtpodsDaemonTransport(FakeEngine.Options(engine));
        await t.StartAsync();

        Assert.Contains("Incompatible engine protocol version", t.UnavailableReason);
        Assert.Null(t.Current);
        Assert.Equal(HelloOnly, engine.Received.Select(m => (string?)m["type"]));
    }

    [Fact]
    public async Task A_hello_ack_with_another_major_is_refused()
    {
        await using var engine = new FakeEngine();
        engine.OnHello = async _ =>
        {
            var ack = EngineFixtures.Message("hello_ack");
            ack["major"] = 2;
            await engine.SendAsync(ack);
        };
        await using var t = new NtpodsDaemonTransport(FakeEngine.Options(engine));
        await t.StartAsync();

        Assert.Contains("Incompatible engine protocol", t.UnavailableReason);
        Assert.Null(t.Current);
    }

    [Fact]
    public async Task A_server_that_is_not_the_configured_engine_receives_nothing()
    {
        await using var engine = new FakeEngine();
        var notTheEngine = Path.Combine(Path.GetTempPath(), "clasp-engine-elsewhere", "clasp-engine.exe");
        await using var t = new NtpodsDaemonTransport(FakeEngine.Options(engine, enginePath: notTheEngine));
        await t.StartAsync();

        Assert.StartsWith("Engine identity check failed", t.UnavailableReason);
        await TestSupport.WaitUntil(() => engine.ConnectionsEnded >= 1);
        Assert.Empty(engine.Received); // not even hello
        Assert.Null(t.Current);
    }

    [Fact]
    public async Task The_engine_hash_is_checked_when_configured()
    {
        string actual;
        using (var file = File.OpenRead(Environment.ProcessPath!)) actual = Convert.ToHexString(SHA256.HashData(file));

        await using (var engine = new FakeEngine())
        await using (var t = new NtpodsDaemonTransport(FakeEngine.Options(engine, sha: actual.ToLowerInvariant())))
        {
            await t.StartAsync();
            Assert.Null(t.UnavailableReason);
            Assert.NotNull(t.Current);
        }

        await using (var engine = new FakeEngine())
        await using (var t = new NtpodsDaemonTransport(FakeEngine.Options(engine, sha: new string('0', 64))))
        {
            await t.StartAsync();
            Assert.Contains("SHA-256 does not match", t.UnavailableReason);
            await TestSupport.WaitUntil(() => engine.ConnectionsEnded >= 1);
            Assert.Empty(engine.Received);
        }
    }

    [Fact]
    public async Task No_engine_running_is_a_precise_reason_not_an_exception()
    {
        var options = new ClaspEngineOptions(Environment.ProcessPath!, null, EngineFixtures.DeviceId, EngineSessionMode.Observe)
        {
            PipeName = "Clasp.Engine.Test.absent." + Guid.NewGuid().ToString("N"),
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
        };
        await using var t = new NtpodsDaemonTransport(options);
        await t.StartAsync();
        Assert.Contains("not running", t.UnavailableReason);
        Assert.Null(t.Current);
        var result = await Connect(t);
        Assert.Equal(CommandOutcome.TransportUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Invalid_options_are_refused_before_any_connection()
    {
        var options = new ClaspEngineOptions("clasp-engine.exe", null, EngineFixtures.DeviceId, EngineSessionMode.Observe);
        await using var t = new NtpodsDaemonTransport(options);
        await t.StartAsync();
        Assert.Contains("--engine-path", t.UnavailableReason);
    }

    // ---- Device availability ----------------------------------------------------------------

    [Fact]
    public async Task A_device_without_a_driver_interface_is_unavailable_with_the_reason()
    {
        var (engine, t) = await StartedAsync(setup: e => e.Devices = (JsonArray)EngineFixtures.Message("devices_unknown_link")["devices"]!.DeepClone());
        await using var _ = engine;
        await using var __ = t;

        Assert.Equal("Driver interface not found for this device.", t.UnavailableReason);
        var state = Assert.IsType<DeviceState>(t.Current);
        Assert.False(state.Link.Nearby.Value);
        Assert.False(state.Link.AudioConnected.HasValue); // windows_connected null stays unknown

        var result = await Connect(t);
        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.Equal("Driver interface not found for this device.", result.Detail);
        Assert.DoesNotContain(engine.Received, m => (string?)m["type"] == "open_session");
    }

    [Fact]
    public async Task A_device_the_engine_does_not_list_is_unavailable()
    {
        var (engine, t) = await StartedAsync(setup: e => e.Devices = new JsonArray());
        await using var _ = engine;
        await using var __ = t;
        Assert.Contains("does not list " + EngineFixtures.DeviceId, t.UnavailableReason);
        Assert.False(t.Current!.Link.Nearby.Value);
    }

    // ---- Sessions ------------------------------------------------------------------------------

    [Fact]
    public async Task Connect_opens_the_configured_session_and_Disconnect_closes_it()
    {
        var (engine, t) = await StartedAsync(EngineSessionMode.Observe);
        await using var _ = engine;
        await using var __ = t;

        Assert.Equal(CommandOutcome.Acknowledged, (await Connect(t)).Outcome);
        var open = engine.LastOfType("open_session");
        Assert.Equal("observe", (string?)open["mode"]);
        Assert.Equal(EngineFixtures.DeviceId, (string?)open["device_id"]);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);
        Assert.Empty(t.Current!.Listening.Supported); // observe: nothing writable

        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new DisconnectCommand(Guid.NewGuid()))).Outcome);
        Assert.Equal(1, (long)engine.LastOfType("close_session")["session_id"]!);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == false);
    }

    [Theory]
    [InlineData("rejected", CommandOutcome.Rejected)]
    [InlineData("failed", CommandOutcome.Failed)]
    [InlineData("unsupported", CommandOutcome.Unsupported)]
    [InlineData("timed_out", CommandOutcome.TimedOut)]
    [InlineData("cancelled", CommandOutcome.Failed)]
    public async Task Open_session_outcomes_map_to_command_outcomes(string outcome, CommandOutcome expected)
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;
        engine.OnRequest = m => engine.SendAsync(FakeEngine.Result((string)m["request_id"]!, outcome, detail: "because"));

        var result = await Connect(t);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal("because", result.Detail);
        Assert.False(t.Current!.Link.ProtocolSession.Value ?? false);
    }

    [Fact]
    public async Task Writes_are_refused_without_sending_in_an_observe_session()
    {
        var (engine, t) = await StartedAsync(EngineSessionMode.Observe);
        await using var _ = engine;
        await using var __ = t;
        await Connect(t);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);

        var result = await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency));
        Assert.Equal(CommandOutcome.Unsupported, result.Outcome);
        Assert.Equal(NtpodsDaemonTransport.ObserveReadOnlyReason, result.Detail);
        Assert.Equal(NtpodsDaemonTransport.ObserveReadOnlyReason, t.ReadOnlyReason);
        Assert.DoesNotContain(engine.Received, m => (string?)m["type"] == "set_listening_mode");
    }

    [Fact]
    public async Task Only_writable_modes_are_sent_and_awareness_and_strength_are_unsupported()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;

        // No session yet: refused locally.
        Assert.Equal(CommandOutcome.Unsupported, (await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency))).Outcome);

        await Connect(t);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);
        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:05.000Z", new JsonObject { ["listening_mode"] = "noise_cancellation" }));
        await Flush(t);
        Assert.Equal(new[] { ListeningMode.NoiseCancellation, ListeningMode.Transparency, ListeningMode.Adaptive }, t.Current!.Listening.Supported);

        Assert.Equal(CommandOutcome.Unsupported, (await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Off))).Outcome);
        Assert.Equal(CommandOutcome.Unsupported, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true))).Outcome);
        Assert.Equal(CommandOutcome.Unsupported, (await t.SendAsync(new SetAdaptiveStrengthCommand(Guid.NewGuid(), 40))).Outcome);
        Assert.DoesNotContain(engine.Received, m => (string?)m["type"] == "set_listening_mode");

        var accepted = await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency));
        Assert.Equal(CommandOutcome.Acknowledged, accepted.Outcome);
        var sent = engine.LastOfType("set_listening_mode");
        Assert.Equal("transparency", (string?)sent["mode"]);
        Assert.Equal(1, (long)sent["session_id"]!);
        // Accepted is not a readback: the original actual mode remains unchanged.
        await Flush(t);
        Assert.Equal(ListeningMode.NoiseCancellation, t.Current!.Listening.Actual.Value);
    }

    [Fact]
    public async Task An_unanswered_write_times_out_once_and_is_not_retried()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;
        await Connect(t);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);
        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:05.000Z", new JsonObject { ["listening_mode"] = "noise_cancellation" }));
        await Flush(t);
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_listening_mode" ? Task.CompletedTask : accept(m);

        var result = await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Adaptive));
        Assert.Equal(CommandOutcome.TimedOut, result.Outcome);
        await Flush(t);
        Assert.Single(engine.Received, m => (string?)m["type"] == "set_listening_mode");
    }

    // ---- Observations -----------------------------------------------------------------------------

    private static async Task<(FakeEngine, NtpodsDaemonTransport)> OpenSessionAsync()
    {
        var (engine, t) = await StartedAsync();
        await Connect(t);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);
        return (engine, t);
    }

    private static JsonObject Battery(int? level, bool? charging) => new() { ["level"] = level, ["charging"] = charging };

    [Fact]
    public async Task Zero_and_false_are_values_while_null_stays_unknown()
    {
        var (engine, t) = await OpenSessionAsync();
        await using var _ = engine;
        await using var __ = t;

        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:05.123Z", new JsonObject
        {
            ["battery.left"] = Battery(0, false),
            ["battery.right"] = Battery(50, null),
            ["battery.case"] = Battery(null, null),
        }));
        await Flush(t);

        var b = t.Current!.Battery;
        Assert.Equal(0, b.Left.Level.Value);               // zero is a reading
        Assert.False(b.Left.Charging.Value);               // false was observed
        Assert.Equal(50, b.Right.Level.Value);
        Assert.False(b.Right.Charging.HasValue);           // charging null: unknown, never false
        Assert.False(b.Case.Level.HasValue);               // level null: unknown
        Assert.False(b.Case.Charging.HasValue);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 18, 4, 5, 123, TimeSpan.Zero), b.Left.Level.ObservedAt);
    }

    [Fact]
    public async Task A_field_absent_from_an_observation_keeps_its_value_and_time()
    {
        var (engine, t) = await OpenSessionAsync();
        await using var _ = engine;
        await using var __ = t;

        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:05.000Z", new JsonObject { ["battery.left"] = Battery(70, true) }));
        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:09:00.000Z", new JsonObject { ["listening_mode"] = "adaptive" }));
        await Flush(t);

        var s = t.Current!;
        Assert.Equal(70, s.Battery.Left.Level.Value);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 18, 4, 5, TimeSpan.Zero), s.Battery.Left.Level.ObservedAt);
        Assert.Equal(ListeningMode.Adaptive, s.Listening.Actual.Value);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 18, 9, 0, TimeSpan.Zero), s.Listening.Actual.ObservedAt);
    }

    [Fact]
    public async Task Observations_for_another_session_or_device_are_dropped()
    {
        var (engine, t) = await OpenSessionAsync();
        await using var _ = engine;
        await using var __ = t;

        await engine.SendAsync(FakeEngine.Observation(2, "2026-10-15T18:04:05.000Z", new JsonObject { ["listening_mode"] = "transparency" }));
        var stranger = FakeEngine.Observation(1, "2026-10-15T18:04:05.000Z", new JsonObject { ["listening_mode"] = "off" });
        stranger["device_id"] = "dev-0000000000000000";
        await engine.SendAsync(stranger);
        await Flush(t);

        Assert.False(t.Current!.Listening.Actual.HasValue);
    }

    [Fact]
    public async Task Stale_sequence_numbers_are_dropped()
    {
        var (engine, t) = await OpenSessionAsync();
        await using var _ = engine;
        await using var __ = t;

        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:05.000Z", new JsonObject { ["listening_mode"] = "transparency" }));
        var stale = FakeEngine.Observation(1, "2026-10-15T18:04:06.000Z", new JsonObject { ["listening_mode"] = "off" });
        stale["seq"] = 2; // far behind the engine's current sequence
        await engine.SendAsync(stale);
        await Flush(t);

        Assert.Equal(ListeningMode.Transparency, t.Current!.Listening.Actual.Value);
    }

    [Fact]
    public async Task Session_end_clears_live_observations_and_old_session_data_stays_out()
    {
        var (engine, t) = await OpenSessionAsync();
        await using var _ = engine;
        await using var __ = t;

        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:05.000Z", new JsonObject
        {
            ["battery.left"] = Battery(70, false),
            ["listening_mode"] = "transparency",
        }));
        await engine.SendAsync(FakeEngine.Session(1, "lost"));
        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:06.000Z", new JsonObject { ["listening_mode"] = "adaptive" }));
        await Flush(t);

        var s = t.Current!;
        Assert.False(s.Link.ProtocolSession.Value);
        Assert.False(s.Battery.Left.Level.HasValue);
        Assert.False(s.Listening.Actual.HasValue);
        Assert.Empty(s.Listening.Supported);

        // A late "open" for the ended session cannot revive it.
        await engine.SendAsync(FakeEngine.Session(1, "open", "transparency"));
        await Flush(t);
        Assert.False(t.Current!.Link.ProtocolSession.Value);
    }

    [Fact]
    public async Task Device_state_sequence_increases_strictly()
    {
        var engine = new FakeEngine();
        engine.AcceptSessions();
        await using var _ = engine;
        await using var t = new NtpodsDaemonTransport(FakeEngine.Options(engine));
        var sequences = new List<long>();
        t.StateChanged += (_, s) => { lock (sequences) sequences.Add(s.Sequence); };
        await t.StartAsync();
        await Connect(t);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);
        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:05.000Z", new JsonObject { ["listening_mode"] = "off" }));
        await Flush(t);

        lock (sequences)
        {
            Assert.True(sequences.Count >= 3);
            Assert.Equal(sequences.Distinct().Count(), sequences.Count);
            Assert.All(sequences, s => Assert.True(s > 0));
        }
    }

    // ---- Connection loss, reconnect and epochs ------------------------------------------------

    [Fact]
    public async Task A_new_epoch_after_reconnect_discards_everything_from_before()
    {
        var (engine, t) = await OpenSessionAsync();
        await using var _ = engine;
        await using var __ = t;
        await engine.SendAsync(FakeEngine.Observation(1, "2026-10-15T18:04:05.000Z", new JsonObject
        {
            ["battery.left"] = Battery(70, false),
            ["listening_mode"] = "transparency",
        }));
        await Flush(t);
        Assert.Equal(70, t.Current!.Battery.Left.Level.Value);

        engine.DropClient();
        await TestSupport.WaitUntil(() => t.UnavailableReason is not null);
        Assert.Contains("connection to the Clasp engine closed", t.UnavailableReason);
        Assert.False(t.Current!.Link.ProtocolSession.Value);
        Assert.False(t.Current.Battery.Left.Level.HasValue);
        Assert.False(t.Current.Link.Nearby.HasValue); // the device list is no longer current
        Assert.Equal(CommandOutcome.TransportUnavailable, (await Connect(t)).Outcome);

        // A new engine process: new epoch, sequence restarts at 1 (lower than before: still accepted).
        engine.Epoch = "11111111-2222-4333-8444-555555555555";
        engine.RestartSequence();
        await t.StartAsync();
        Assert.Null(t.UnavailableReason);
        Assert.Equal(engine.Epoch, t.EngineEpoch);
        Assert.True(t.Current!.Link.Nearby.Value);
        Assert.False(t.Current.Link.ProtocolSession.Value ?? false);
        Assert.False(t.Current.Battery.Left.Level.HasValue);
        Assert.False(t.Current.Listening.Actual.HasValue);

        // Session ids restart in the new epoch; session 1 there is a new session, not the old one.
        Assert.Equal(CommandOutcome.Acknowledged, (await Connect(t)).Outcome);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);
        Assert.False(t.Current!.Listening.Actual.HasValue); // no replay of anything from before
    }

    public static TheoryData<string> BadFrames()
    {
        var data = new TheoryData<string>();
        foreach (var file in EngineFixtures.Files("frames"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if ((string?)EngineFixtures.Load("frames", name)["expect"] != "ok") data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(BadFrames))]
    public async Task A_bad_frame_closes_the_connection_without_hanging(string name)
    {
        var (engine, t) = await OpenSessionAsync();
        await using var _ = engine;
        await using var __ = t;
        int ended = engine.ConnectionsEnded;

        var bytes = EngineFixtures.FromHex((string)EngineFixtures.Load("frames", name)["bytes_hex"]!);
        await engine.SendRawAsync(bytes);
        if ((string?)EngineFixtures.Load("frames", name)["expect"] == "reject_truncated") engine.DropClient();

        await TestSupport.WaitUntil(() => t.UnavailableReason is not null);
        await TestSupport.WaitUntil(() => engine.ConnectionsEnded > ended); // the client closed its end
        // The reason is visible as soon as OnClosed updates it under the lock, but the new state is
        // published (Current) just after, outside the lock: wait for it rather than assert at once.
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == false);
        var result = await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency)).WaitAsync(Prompt);
        Assert.Equal(CommandOutcome.TransportUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Cancelling_an_open_returns_promptly_and_tells_the_engine()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;
        engine.OnRequest = _ => Task.CompletedTask; // never answers

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var send = t.SendAsync(new ConnectCommand(Guid.NewGuid()), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(Prompt));

        var open = engine.LastOfType("open_session");
        await TestSupport.WaitUntil(() => engine.Received.Any(m => (string?)m["type"] == "cancel"));
        Assert.Equal((string?)open["request_id"], (string?)engine.LastOfType("cancel")["target_request_id"]);
    }

    [Fact]
    public async Task Disposing_mid_request_returns_promptly()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        engine.OnRequest = _ => Task.CompletedTask; // never answers

        var send = t.SendAsync(new ConnectCommand(Guid.NewGuid()));
        await TestSupport.WaitUntil(() => engine.Received.Any(m => (string?)m["type"] == "open_session"));
        await t.DisposeAsync().AsTask().WaitAsync(Prompt);

        var result = await send.WaitAsync(Prompt);
        Assert.Equal(CommandOutcome.TransportUnavailable, result.Outcome);
        Assert.Equal(CommandOutcome.TransportUnavailable, (await Connect(t)).Outcome);
    }

    // ---- Engine reference behaviour (clasp-engine commit 2f193f5) ---------------------------

    [Fact]
    public async Task A_busy_error_for_the_open_request_is_a_rejection()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;
        engine.OnRequest = m => engine.SendAsync(new JsonObject
        {
            ["type"] = "error", ["code"] = "busy", ["detail"] = "a session is already open", ["request_id"] = (string?)m["request_id"],
        });

        var result = await Connect(t);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Contains("busy", result.Detail);
        Assert.Null(t.UnavailableReason); // request-level error: the connection stays up
        await Flush(t);
    }

    [Fact]
    public async Task A_failed_open_ends_with_the_session_closed_not_connecting()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;
        engine.OnRequest = async m =>
        {
            await engine.SendAsync(FakeEngine.Session(4, "opening"));
            await engine.SendAsync(FakeEngine.Result((string)m["request_id"]!, "failed", detail: "driver connect failed"));
            var closed = FakeEngine.Session(4, "closed");
            closed["reason"] = "open failed: driver connect failed";
            await engine.SendAsync(closed);
        };

        var result = await Connect(t);
        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        await Flush(t);
        Assert.False(t.Current!.Link.ProtocolSession.Value);
        Assert.False(t.Current.Link.Connecting.Value);
    }

    [Fact]
    public async Task Closing_an_opening_session_ends_it()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;
        string? openId = null;
        engine.OnRequest = async m =>
        {
            switch ((string?)m["type"])
            {
                case "open_session":
                    openId = (string?)m["request_id"];
                    await engine.SendAsync(FakeEngine.Session(1, "opening")); // and then nothing until closed
                    break;
                case "close_session":
                    await engine.SendAsync(FakeEngine.Result(openId!, "cancelled", detail: "closed while opening"));
                    await engine.SendAsync(FakeEngine.Session(1, "closed"));
                    await engine.SendAsync(FakeEngine.Result((string)m["request_id"]!, "accepted"));
                    break;
            }
        };

        var open = Connect(t);
        await TestSupport.WaitUntil(() => t.Current?.Link.Connecting.Value == true);
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new DisconnectCommand(Guid.NewGuid()))).Outcome);
        Assert.Equal(CommandOutcome.Failed, (await open.WaitAsync(Prompt)).Outcome);
        await Flush(t);
        Assert.False(t.Current!.Link.ProtocolSession.Value);
        Assert.False(t.Current.Link.Connecting.Value);
    }

    [Fact]
    public async Task A_connection_level_error_closes_the_client_end_promptly()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;
        int ended = engine.ConnectionsEnded;

        // The engine keeps its end open (it would wait up to 2 s): the client must close first.
        await engine.SendAsync(new JsonObject { ["type"] = "error", ["code"] = "internal", ["detail"] = "driver gone", ["request_id"] = null });
        await TestSupport.WaitUntil(() => engine.ConnectionsEnded > ended, 1500);
        Assert.Contains("engine error internal", t.UnavailableReason);
    }

    [Fact]
    public async Task The_device_list_is_rechecked_after_session_open_and_close()
    {
        var (engine, t) = await StartedAsync();
        await using var _ = engine;
        await using var __ = t;
        int Lists() => engine.Received.Count(m => (string?)m["type"] == "list_devices");
        Assert.Equal(1, Lists());
        var firstCheck = t.Current!.Link.AudioConnected.ObservedAt;

        // The link changes while the session opens; the recheck after "open" picks it up.
        ((JsonObject)engine.Devices[0]!)["windows_connected"] = false;
        await Task.Delay(20); // a later check time than the first listing
        await Connect(t);
        await TestSupport.WaitUntil(() => Lists() == 2 && t.Current?.Link.AudioConnected.Value == false);
        Assert.True(t.Current!.Link.AudioConnected.ObservedAt > firstCheck);

        await t.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await TestSupport.WaitUntil(() => Lists() == 3);
    }

    [Fact]
    public async Task Fixture_failures_for_open_session_are_readable()
    {
        foreach (var (fixture, outcome, detail) in new[]
        {
            ("result_failed_not_connected", CommandOutcome.Failed,
                "The AirPods are not connected to Windows. Connect the AirPods to Windows first (Bluetooth settings), then start the session"),
            ("result_open_timed_out", CommandOutcome.TimedOut, "the device did not answer within 20 s"),
            ("error_busy", CommandOutcome.Rejected, "The engine is busy: a session is already open"),
        })
        {
            var (engine, t) = await StartedAsync();
            await using var _ = engine;
            await using var __ = t;
            engine.OnRequest = m =>
            {
                var reply = EngineFixtures.Message(fixture);
                reply.Remove("seq");
                reply["request_id"] = (string?)m["request_id"];
                return engine.SendAsync(reply);
            };

            var result = await Connect(t);
            Assert.Equal(outcome, result.Outcome);
            Assert.Equal(detail, result.Detail);
            await Flush(t); // the connection is still up
            Assert.Null(t.UnavailableReason);
        }
    }

    [Fact]
    public async Task A_busy_pipe_is_not_reported_as_a_missing_engine()
    {
        var (engine, first) = await StartedAsync();
        await using var _ = engine;
        await using var __ = first;

        // The fake engine has one instance and it is serving the first client.
        var options = FakeEngine.Options(engine) with { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        await using var second = new NtpodsDaemonTransport(options);
        await second.StartAsync();
        Assert.Equal("The Clasp engine is unavailable or busy with another Clasp window. Close the other window and try again.", second.UnavailableReason);
    }

    [Fact]
    public void Open_session_waits_longer_than_the_engine()
        => Assert.Equal(TimeSpan.FromSeconds(25),
            new ClaspEngineOptions("C:/x/clasp-engine.exe", null, EngineFixtures.DeviceId, EngineSessionMode.Observe).OpenSessionTimeout);

    [Fact]
    public async Task A_silent_engine_handshake_times_out()
    {
        await using var engine = new FakeEngine();
        engine.OnHello = _ => Task.CompletedTask; // never acknowledges
        var options = FakeEngine.Options(engine) with { };
        options = options with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) };
        await using var t = new NtpodsDaemonTransport(options);
        await t.StartAsync().WaitAsync(Prompt);
        Assert.Contains("did not answer the handshake", t.UnavailableReason);
    }
}
