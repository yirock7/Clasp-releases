using System.Text;
using System.Text.Json.Nodes;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;
using AppleDeviceHub.Core.Interop;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Engine;

/// <summary>
/// clasp-engine protocol 1.1 (README section 9): Conversation Awareness and ear observations, and
/// the Conversation Awareness write, against the shared fixtures and an in-process fake engine.
/// No real engine, driver or radio is used.
/// </summary>
public class ConversationAwarenessAndEarTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(3);
    private const string At = "2026-10-15T18:05:00.000Z";

    private static byte[] Bytes(JsonObject message) => Encoding.UTF8.GetBytes(message.ToJsonString());

    // ---- Codec -----------------------------------------------------------------------------

    [Fact]
    public void The_11_fixtures_parse_to_typed_values()
    {
        var obs = Assert.IsType<ObservationMessage>(EngineCodec.Parse(Bytes(EngineFixtures.Message("observation_ca_and_ear"))));
        Assert.Equal(EngineConversationAwareness.On, obs.Fields.ConversationAwareness);
        Assert.Equal(new ObservedEars(EngineEarState.InEar, EngineEarState.OutOfEar), obs.Fields.Ears);
        Assert.Null(obs.Fields.ListeningMode);
        Assert.Null(obs.Fields.BatteryLeft);

        var session = Assert.IsType<SessionMessage>(EngineCodec.Parse(Bytes(EngineFixtures.Message("session_open_control_v11"))));
        Assert.Equal(new[] { EngineWire.SettingListeningMode }, session.WritableSettings);
        var session10 = Assert.IsType<SessionMessage>(EngineCodec.Parse(Bytes(EngineFixtures.Message("session_open_control"))));
        Assert.Null(session10.WritableSettings); // a 1.0 engine: field absent, not "nothing writable"

        var write = Assert.IsType<SetConversationAwarenessRequest>(EngineCodec.Parse(Bytes(EngineFixtures.Message("set_conversation_awareness"))));
        Assert.False(write.Enabled);
        Assert.Equal(1, write.SessionId);

        var unsupported = Assert.IsType<ResultMessage>(EngineCodec.Parse(Bytes(EngineFixtures.Message("result_unsupported_ca_not_observed"))));
        Assert.Equal(EngineOutcome.Unsupported, unsupported.Outcome);

        var hello = Assert.IsType<HelloMessage>(EngineCodec.Parse(Bytes(EngineFixtures.Message("hello_v11"))));
        Assert.Equal(1, hello.Minor);
        Assert.Equal(1, EngineWire.Minor); // this client speaks 1.1
    }

    [Fact]
    public void Unknown_awareness_and_ear_values_stay_unknown_and_never_become_off()
    {
        var message = FakeEngine.Observation(1, At, new JsonObject
        {
            ["conversation_awareness"] = "unknown",
            ["ear"] = new JsonObject { ["primary"] = "unknown", ["secondary"] = "sideways" },
        });
        message["seq"] = 3;
        var obs = Assert.IsType<ObservationMessage>(EngineCodec.Parse(Bytes(message)));
        Assert.Equal(EngineConversationAwareness.Unrecognised, obs.Fields.ConversationAwareness);
        Assert.Null(EngineWire.ToContract(obs.Fields.ConversationAwareness!.Value));
        Assert.Equal(new ObservedEars(EngineEarState.Unrecognised, EngineEarState.Unrecognised), obs.Fields.Ears);
        Assert.Null(EngineWire.ToContract(EngineEarState.Unrecognised));
    }

    [Theory]
    [InlineData("{\"conversation_awareness\":true}")]
    [InlineData("{\"ear\":\"in_ear\"}")]
    [InlineData("{\"ear\":{\"primary\":\"in_ear\"}}")]
    public void Malformed_11_fields_are_refused(string fields)
    {
        var message = FakeEngine.Observation(1, At, JsonNode.Parse(fields)!.AsObject());
        message["seq"] = 3;
        var ex = Assert.Throws<EngineProtocolException>(() => EngineCodec.Parse(Bytes(message)));
        Assert.Equal(EngineFrameError.Malformed, ex.Error);
    }

    [Fact]
    public void Features_need_a_negotiated_11_and_the_advertised_capability()
    {
        var v11 = Assert.IsType<HelloAckMessage>(EngineCodec.Parse(Bytes(EngineFixtures.Message("hello_ack_v11"))));
        Assert.Equal(new EngineFeatures(true, true, true), EngineFeatures.From(v11));
        var v10 = Assert.IsType<HelloAckMessage>(EngineCodec.Parse(Bytes(EngineFixtures.Message("hello_ack"))));
        Assert.Equal(EngineFeatures.None, EngineFeatures.From(v10));
        // A 1.0 ack that lists 1.1 names anyway does not enable them.
        Assert.Equal(EngineFeatures.None, EngineFeatures.From(v11 with { Minor = 0 }));
        // A write without the matching observation could never be confirmed: not used.
        var writeOnly = v11 with { Capabilities = new[] { "observe", "conversation_awareness_write" } };
        Assert.False(EngineFeatures.From(writeOnly).ConversationAwarenessWrite);
    }

    // ---- Transport: observation mapping -----------------------------------------------------

    private static async Task<(FakeEngine, NtpodsDaemonTransport)> OpenAsync(
        string ack = "hello_ack_v11", EngineSessionMode mode = EngineSessionMode.Control, Action<FakeEngine>? setup = null,
        TimeSpan? requestTimeout = null)
    {
        var engine = new FakeEngine { HelloAckFixture = ack };
        engine.AcceptSessions();
        setup?.Invoke(engine);
        var options = FakeEngine.Options(engine, mode);
        if (requestTimeout is { } timeout) options = options with { RequestTimeout = timeout };
        var transport = new NtpodsDaemonTransport(options);
        await transport.StartAsync();
        Assert.Equal(CommandOutcome.Acknowledged, (await transport.SendAsync(new ConnectCommand(Guid.NewGuid()))).Outcome);
        await TestSupport.WaitUntil(() => transport.Current?.Link.ProtocolSession.Value == true);
        return (engine, transport);
    }

    private static async Task Observe(FakeEngine engine, NtpodsDaemonTransport transport, JsonObject fields, long sessionId = 1)
    {
        await engine.SendAsync(FakeEngine.Observation(sessionId, At, fields));
        await transport.StartAsync(); // pipe barrier, not hardware I/O
    }

    private static JsonObject Ca(string value) => new() { ["conversation_awareness"] = value };

    private static int Writes(FakeEngine engine) => engine.Received.Count(m => (string?)m["type"] == "set_conversation_awareness");

    /// <summary>Re-send the open session with the given 1.1 writable_settings (as the engine does when they change).</summary>
    private static async Task Settings(FakeEngine engine, NtpodsDaemonTransport transport, params string[] settings)
    {
        var session = FakeEngine.Session(1, "open", "noise_cancellation", "transparency", "adaptive");
        session["writable_settings"] = new JsonArray(settings.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        await engine.SendAsync(session);
        await transport.StartAsync();
    }

    [Fact]
    public async Task A_10_engine_claims_neither_awareness_nor_ear_even_if_fields_arrive()
    {
        var (engine, t) = await OpenAsync(ack: "hello_ack");
        await using var _ = engine;
        await using var __ = t;
        Assert.Equal(1, (int)engine.Received[0]["minor"]!); // we offer 1.1; the engine answered 1.0
        Assert.Equal(EngineFeatures.None, t.Features);

        await Observe(engine, t, EngineFixtures.Message("observation_ca_and_ear")["fields"]!.DeepClone().AsObject());
        var state = t.Current!;
        Assert.False(state.Supports(DeviceCapabilities.ConversationAwareness));
        Assert.False(state.Supports(DeviceCapabilities.EarDetection));
        Assert.False(state.ConversationAwareness.HasValue);
        Assert.Equal(EarState.Unknown, state.Ears);

        var refused = await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false));
        Assert.Equal(CommandOutcome.Unsupported, refused.Outcome);
        Assert.Equal(NtpodsDaemonTransport.AwarenessWriteUnsupportedReason, refused.Detail);
        Assert.Equal(0, Writes(engine));
    }

    [Fact]
    public async Task An_11_engine_maps_awareness_and_ears_only_once_observed()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        Assert.Equal(new EngineFeatures(true, true, true), t.Features);
        Assert.False(t.Current!.Supports(DeviceCapabilities.ConversationAwareness)); // advertised, not yet observed
        Assert.False(t.Current!.Supports(DeviceCapabilities.EarDetection));
        Assert.False(t.Current!.Supports(DeviceCapabilities.AdaptiveStrength));

        await Observe(engine, t, EngineFixtures.Message("observation_ca_and_ear")["fields"]!.DeepClone().AsObject());
        var state = t.Current!;
        var at = DateTimeOffset.Parse(At, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DataOrigin.Hardware, state.Origin);
        Assert.True(state.Supports(DeviceCapabilities.ConversationAwareness));
        Assert.True(state.Supports(DeviceCapabilities.EarDetection));
        Assert.False(state.Supports(DeviceCapabilities.AdaptiveStrength));
        Assert.Equal(Observation<bool>.At(true, at), state.ConversationAwareness);
        Assert.Equal(Observation<PodPlacement>.At(PodPlacement.InEar, at), state.Ears.Primary);
        Assert.Equal(Observation<PodPlacement>.At(PodPlacement.OutOfEar, at), state.Ears.Secondary);
        Assert.True(state.Ears.AnyInEar);

        // "unknown" is unknown, never off; an unknown bud side is unknown, the other keeps its reading.
        await Observe(engine, t, new JsonObject
        {
            ["conversation_awareness"] = "unknown",
            ["ear"] = new JsonObject { ["primary"] = "unknown", ["secondary"] = "in_case" },
        });
        state = t.Current!;
        Assert.False(state.ConversationAwareness.HasValue);
        Assert.Null(state.ConversationAwareness.Value);
        Assert.True(state.Supports(DeviceCapabilities.ConversationAwareness));
        Assert.False(state.Ears.Primary.HasValue);
        Assert.Equal(PodPlacement.InCase, state.Ears.Secondary.Value);

        // A packet without the fields keeps them (and their time).
        await Observe(engine, t, new JsonObject { ["listening_mode"] = "transparency" });
        Assert.Equal(PodPlacement.InCase, t.Current!.Ears.Secondary.Value);
    }

    [Fact]
    public async Task A_later_battery_report_never_refreshes_the_ear_time()
    {
        // EF-IR-03: ear evidence keeps the time of the ear packet itself, so the media policy can
        // judge its age; other packets (battery, mode) must not make it look fresh.
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        var earAt = DateTimeOffset.Parse(At, System.Globalization.CultureInfo.InvariantCulture);
        await Observe(engine, t, new JsonObject { ["ear"] = new JsonObject { ["primary"] = "in_ear", ["secondary"] = "in_ear" } });

        const string Later = "2026-10-15T19:05:00.000Z"; // an hour later
        await engine.SendAsync(FakeEngine.Observation(1, Later, new JsonObject
        {
            ["battery.left"] = new JsonObject { ["level"] = 70, ["charging"] = false },
        }));
        await t.StartAsync(); // pipe barrier
        var state = t.Current!;
        Assert.Equal(70, state.Battery.Left.Level.Value);
        Assert.Equal(DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture), state.Battery.Left.Level.ObservedAt);
        Assert.Equal(earAt, state.Ears.Primary.ObservedAt);
        Assert.Equal(earAt, state.Ears.Secondary.ObservedAt);
    }

    [Fact]
    public async Task Only_advertised_observations_are_used_and_unknown_alone_claims_nothing()
    {
        var (engine, t) = await OpenAsync(setup: e => e.OnHello = async _ =>
        {
            var ack = EngineFixtures.Message("hello_ack_v11");
            ack["capabilities"] = new JsonArray("list_devices", "observe", "listening_mode_write", "observe_conversation_awareness");
            ack["engine_epoch"] = e.Epoch;
            ack.Remove("seq");
            await e.SendAsync(ack);
        });
        await using var _ = engine;
        await using var __ = t;

        await Observe(engine, t, new JsonObject
        {
            ["conversation_awareness"] = "unknown",
            ["ear"] = new JsonObject { ["primary"] = "in_ear", ["secondary"] = "in_ear" },
        });
        Assert.False(t.Current!.Supports(DeviceCapabilities.ConversationAwareness)); // no real value yet
        Assert.False(t.Current!.Supports(DeviceCapabilities.EarDetection));          // observe_ear not advertised
        Assert.Equal(EarState.Unknown, t.Current!.Ears);
        Assert.Equal(NtpodsDaemonTransport.AwarenessWriteUnsupportedReason, t.ConversationAwarenessReadOnlyReason);
    }

    [Fact]
    public async Task Session_end_clears_awareness_ears_and_their_claims()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, EngineFixtures.Message("observation_ca_and_ear")["fields"]!.DeepClone().AsObject());
        Assert.True(t.Current!.Supports(DeviceCapabilities.ConversationAwareness));

        await t.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == false);
        var state = t.Current!;
        Assert.False(state.Supports(DeviceCapabilities.ConversationAwareness));
        Assert.False(state.Supports(DeviceCapabilities.EarDetection));
        Assert.False(state.ConversationAwareness.HasValue);
        Assert.Equal(EarState.Unknown, state.Ears);
    }

    // ---- Transport: Conversation Awareness writes -------------------------------------------

    [Fact]
    public async Task A_write_is_refused_before_awareness_has_been_reported()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;

        var refused = await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true));
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        Assert.Equal(NtpodsDaemonTransport.AwarenessNotReportedReason, refused.Detail);
        await Observe(engine, t, Ca("unknown"));
        Assert.Equal(CommandOutcome.Rejected, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true))).Outcome);
        Assert.Equal(0, Writes(engine));
    }

    [Fact]
    public async Task An_observe_session_never_writes_awareness()
    {
        var (engine, t) = await OpenAsync(mode: EngineSessionMode.Observe);
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, Ca("off"));
        Assert.True(t.Current!.Supports(DeviceCapabilities.ConversationAwareness)); // readable
        var refused = await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true));
        Assert.Equal(CommandOutcome.Unsupported, refused.Outcome);
        Assert.Equal(NtpodsDaemonTransport.ObserveReadOnlyReason, t.ConversationAwarenessReadOnlyReason);
        Assert.Equal(0, Writes(engine));
    }

    [Fact]
    public async Task An_accepted_write_is_not_confirmation_and_holds_until_a_matching_readback()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, Ca("off"));
        Assert.Null(t.ConversationAwarenessReadOnlyReason);

        var result = await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true));
        Assert.Equal(CommandOutcome.Acknowledged, result.Outcome);
        var sent = engine.LastOfType("set_conversation_awareness");
        Assert.True((bool)sent["enabled"]!);
        Assert.Equal(1, (long)sent["session_id"]!);
        await t.StartAsync();
        Assert.False(t.Current!.ConversationAwareness.Value); // still the device's reading

        // The device reports CA once: without a readback the write stays outstanding.
        Assert.Equal(NtpodsDaemonTransport.AwarenessAwaitingConfirmationReason, t.ConversationAwarenessReadOnlyReason);
        Assert.Equal(CommandOutcome.Rejected, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false))).Outcome);
        await Observe(engine, t, Ca("off")); // a non-matching reading does not release it
        Assert.Equal(CommandOutcome.Rejected, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false))).Outcome);
        Assert.Equal(1, Writes(engine));

        // An acknowledged CA write does not block listening-mode writes.
        await Observe(engine, t, new JsonObject { ["listening_mode"] = "noise_cancellation" });
        Assert.Equal(CommandOutcome.Acknowledged,
            (await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency))).Outcome);

        await Observe(engine, t, Ca("on"));
        Assert.True(t.Current!.ConversationAwareness.Value);
        Assert.Null(t.ConversationAwarenessReadOnlyReason);
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false))).Outcome);
        Assert.Equal(2, Writes(engine));
    }

    [Fact]
    public async Task Overlapping_writes_are_refused_in_both_directions()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, new JsonObject { ["conversation_awareness"] = "off", ["listening_mode"] = "noise_cancellation" });
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] is "set_conversation_awareness" or "set_listening_mode" ? Task.CompletedTask : accept(m);

        var ca = t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true));
        await TestSupport.WaitUntil(() => Writes(engine) == 1);
        Assert.Equal(CommandOutcome.Rejected, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false))).Outcome);
        var mode = await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency));
        Assert.Equal(CommandOutcome.Rejected, mode.Outcome);
        Assert.Empty(t.Current!.Listening.Supported); // the mode selector is not offered meanwhile
        Assert.DoesNotContain(engine.Received, m => (string?)m["type"] == "set_listening_mode");
        await engine.SendAsync(FakeEngine.Result((string)engine.LastOfType("set_conversation_awareness")["request_id"]!, "accepted"));
        Assert.Equal(CommandOutcome.Acknowledged, (await ca.WaitAsync(Prompt)).Outcome);
        await Observe(engine, t, Ca("on"));

        // ...and the other way round: a mode write on the wire blocks a CA write.
        var modeWrite = t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency));
        await TestSupport.WaitUntil(() => engine.Received.Any(m => (string?)m["type"] == "set_listening_mode"));
        var refused = await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false));
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        Assert.Equal(NtpodsDaemonTransport.OtherWriteInFlightReason, refused.Detail);
        Assert.Equal(1, Writes(engine));
        await engine.SendAsync(FakeEngine.Result((string)engine.LastOfType("set_listening_mode")["request_id"]!, "accepted"));
        await modeWrite.WaitAsync(Prompt);
    }

    [Fact]
    public async Task An_engine_refusal_releases_cleanly()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, Ca("off"));
        var accept = engine.OnRequest!;
        engine.OnRequest = async m =>
        {
            if ((string?)m["type"] != "set_conversation_awareness")
            {
                await accept(m);
                return;
            }
            var reply = EngineFixtures.Message("result_unsupported_ca_not_observed");
            reply["request_id"] = (string)m["request_id"]!;
            reply.Remove("seq");
            await engine.SendAsync(reply);
        };
        var result = await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true));
        Assert.Equal(CommandOutcome.Unsupported, result.Outcome);
        Assert.Contains("has not been reported", result.Detail);
        Assert.Null(t.ConversationAwarenessReadOnlyReason); // a refusal changed nothing: not uncertain
    }

    [Fact]
    public async Task An_unanswered_write_stays_uncertain_despite_a_late_ack_and_readback_until_a_new_session()
    {
        var (engine, t) = await OpenAsync(requestTimeout: TimeSpan.FromMilliseconds(150));
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, Ca("off"));
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_conversation_awareness" ? Task.CompletedTask : accept(m);

        var result = await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true));
        Assert.Equal(CommandOutcome.TimedOut, result.Outcome);
        var oldRequest = (string)engine.LastOfType("set_conversation_awareness")["request_id"]!;
        await engine.SendAsync(FakeEngine.Result(oldRequest, "accepted"));
        await Observe(engine, t, Ca("on"));
        Assert.True(t.Current!.ConversationAwareness.Value); // the late reading is shown honestly...
        Assert.Equal(NtpodsDaemonTransport.UncertainAwarenessWriteReason, t.ConversationAwarenessReadOnlyReason); // ...but stays latched
        Assert.Equal(CommandOutcome.Rejected, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false))).Outcome);
        Assert.Equal(1, Writes(engine));

        await t.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == false);
        engine.AcceptSessions(sessionId: 2);
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new ConnectCommand(Guid.NewGuid()))).Outcome);
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);
        Assert.Equal(NtpodsDaemonTransport.AwarenessNotReportedReason, t.ConversationAwarenessReadOnlyReason);
        await Observe(engine, t, Ca("on"), sessionId: 2);
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false))).Outcome);
    }

    [Fact]
    public async Task An_uncertain_mode_write_also_blocks_awareness_writes_like_the_engine()
    {
        // Review EF-REVIEW P3: the engine keeps an uncertain mode write pending for the session.
        var (engine, t) = await OpenAsync(requestTimeout: TimeSpan.FromMilliseconds(150));
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, new JsonObject { ["conversation_awareness"] = "off", ["listening_mode"] = "noise_cancellation" });
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_listening_mode" ? Task.CompletedTask : accept(m);

        Assert.Equal(CommandOutcome.TimedOut, (await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency))).Outcome);
        var refused = await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true));
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        Assert.Equal(NtpodsDaemonTransport.UncertainModeWriteReason, refused.Detail);
        Assert.Equal(0, Writes(engine));
    }

    [Fact]
    public async Task A_stale_ack_from_an_ended_session_is_ignored()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, Ca("off"));
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_conversation_awareness" ? Task.CompletedTask : accept(m);

        var first = t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true));
        await TestSupport.WaitUntil(() => Writes(engine) == 1);
        var oldRequest = (string)engine.LastOfType("set_conversation_awareness")["request_id"]!;
        await engine.SendAsync(FakeEngine.Session(1, "lost"));
        await t.StartAsync();

        engine.AcceptSessions(sessionId: 2);
        await t.SendAsync(new ConnectCommand(Guid.NewGuid()));
        await TestSupport.WaitUntil(() => t.Current?.Link.ProtocolSession.Value == true);
        await Observe(engine, t, Ca("off"), sessionId: 2);
        Assert.Null(t.ConversationAwarenessReadOnlyReason);

        // The old session's late ack (and an old-session reading) must not touch session 2.
        await engine.SendAsync(FakeEngine.Result(oldRequest, "accepted"));
        await Observe(engine, t, Ca("on"), sessionId: 1);
        await first.WaitAsync(Prompt);
        Assert.False(t.Current!.ConversationAwareness.Value);
        Assert.Null(t.ConversationAwarenessReadOnlyReason);

        engine.OnRequest = accept;
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true))).Outcome);
        Assert.Equal(2, (long)engine.LastOfType("set_conversation_awareness")["session_id"]!);
    }

    [Fact]
    public async Task Cancelling_a_sent_write_leaves_it_uncertain()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, Ca("off"));
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_conversation_awareness" ? Task.CompletedTask : accept(m);
        using var cancel = new CancellationTokenSource();
        var first = t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true), cancel.Token);
        await TestSupport.WaitUntil(() => Writes(engine) == 1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(NtpodsDaemonTransport.UncertainAwarenessWriteReason, t.ConversationAwarenessReadOnlyReason);
        Assert.Equal(CommandOutcome.Rejected, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true))).Outcome);
        Assert.Equal(1, Writes(engine));
    }

    [Fact]
    public async Task The_engines_writable_settings_are_honoured()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, new JsonObject { ["conversation_awareness"] = "off", ["listening_mode"] = "noise_cancellation" });

        await Settings(engine, t, "listening_mode");
        Assert.Equal(NtpodsDaemonTransport.EngineRefusesAwarenessNowReason, t.ConversationAwarenessReadOnlyReason);
        Assert.Equal(CommandOutcome.Rejected, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true))).Outcome);
        Assert.Equal(0, Writes(engine));

        // Without listening_mode, no mode is offered or sent, whatever writable_modes says.
        await Settings(engine, t, "conversation_awareness");
        Assert.Empty(t.Current!.Listening.Supported);
        Assert.Equal(CommandOutcome.Unsupported,
            (await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency))).Outcome);
        Assert.DoesNotContain(engine.Received, m => (string?)m["type"] == "set_listening_mode");

        await Settings(engine, t, "listening_mode", "conversation_awareness");
        Assert.NotEmpty(t.Current!.Listening.Supported);
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true))).Outcome);
    }

    [Fact]
    public async Task A_negotiated_minor_0_disables_11_features_even_if_capabilities_are_listed()
    {
        var (engine, t) = await OpenAsync(setup: e => e.OnHello = async _ =>
        {
            var ack = EngineFixtures.Message("hello_ack_v11");
            ack["minor"] = 0; // the engine negotiated min(client, engine) = 0
            ack["engine_epoch"] = e.Epoch;
            ack.Remove("seq");
            await e.SendAsync(ack);
        });
        await using var _ = engine;
        await using var __ = t;
        Assert.Equal(EngineFeatures.None, t.Features);
        await Observe(engine, t, EngineFixtures.Message("observation_ca_and_ear")["fields"]!.DeepClone().AsObject());
        Assert.False(t.Current!.Supports(DeviceCapabilities.ConversationAwareness));
        Assert.False(t.Current!.Supports(DeviceCapabilities.EarDetection));
        Assert.Equal(CommandOutcome.Unsupported, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true))).Outcome);
        Assert.Equal(0, Writes(engine));
    }

    [Fact]
    public async Task A_resent_open_session_is_an_update_that_keeps_reservations_and_latches()
    {
        var (engine, t) = await OpenAsync(requestTimeout: TimeSpan.FromMilliseconds(150));
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, new JsonObject { ["conversation_awareness"] = "off", ["listening_mode"] = "noise_cancellation" });
        await Settings(engine, t, "listening_mode", "conversation_awareness");

        // An acknowledged, unconfirmed CA write: the engine re-sends the session without CA...
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), true))).Outcome);
        await Settings(engine, t, "listening_mode");
        Assert.True(t.Current!.Link.ProtocolSession.Value);
        Assert.False(t.Current!.ConversationAwareness.Value); // observations survive the update
        // ...and even if it re-sends CA as writable, our reservation still holds without a readback.
        await Settings(engine, t, "listening_mode", "conversation_awareness");
        Assert.Equal(NtpodsDaemonTransport.AwarenessAwaitingConfirmationReason, t.ConversationAwarenessReadOnlyReason);
        Assert.Equal(CommandOutcome.Rejected, (await t.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false))).Outcome);

        // An uncertain listening-mode write stays latched across the same update.
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_listening_mode" ? Task.CompletedTask : accept(m);
        Assert.Equal(CommandOutcome.TimedOut,
            (await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency))).Outcome);
        await Settings(engine, t, "listening_mode", "conversation_awareness");
        Assert.Equal(NtpodsDaemonTransport.UncertainModeWriteReason, t.ReadOnlyReason);
        Assert.Empty(t.Current!.Listening.Supported);
        Assert.Equal(CommandOutcome.Rejected,
            (await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Adaptive))).Outcome);
        Assert.Equal(1, Writes(engine));
        Assert.Single(engine.Received, m => (string?)m["type"] == "set_listening_mode");
        Assert.Equal(1, engine.ConnectionsAccepted);
    }

    [Fact]
    public async Task Adaptive_strength_stays_unavailable_on_an_11_engine()
    {
        var (engine, t) = await OpenAsync();
        await using var _ = engine;
        await using var __ = t;
        await Observe(engine, t, new JsonObject { ["conversation_awareness"] = "on", ["listening_mode"] = "adaptive" });
        Assert.False(t.Current!.Supports(DeviceCapabilities.AdaptiveStrength));
        Assert.Equal(CommandOutcome.Unsupported, (await t.SendAsync(new SetAdaptiveStrengthCommand(Guid.NewGuid(), 40))).Outcome);
    }

    // ---- View model: requested versus actual on hardware -------------------------------------

    private static readonly object Gate = new();

    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    private static (ScriptedHardwareTransport, HubViewModel, FakeTimeProvider) CreateVm()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedHardwareTransport
        {
            ReadOnlyReason = null,
            Capabilities = NtpodsDaemonTransport.HardwareCapabilities | DeviceCapabilities.ConversationAwareness,
        };
        var vm = new HubViewModel(transport, ScriptedHardwareTransport.Id, Dispatch, time: time);
        return (transport, vm, time);
    }

    [Fact]
    public async Task An_unconfirmed_hardware_write_shows_requested_against_actual_and_locks_until_the_session_ends()
    {
        var (t, vm, time) = CreateVm();
        await using var _ = vm;
        var sent = new List<DeviceCommand>();
        t.OnSend = c =>
        {
            sent.Add(c);
            return new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware);
        };
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), awareness: true);
        Assert.True(vm.IsConversationAwarenessInputEnabled);
        Assert.Equal("Lowers media when you speak", vm.ConversationAwarenessCaption);

        await vm.SetConversationAwarenessAsync(false);
        Assert.True(vm.IsConversationAwarenessPending);
        Assert.False(vm.ConversationAwarenessOn);                  // the request, shown as pending
        Assert.False(vm.IsConversationAwarenessInputEnabled);
        await vm.SetConversationAwarenessAsync(true);              // no second write over the first
        Assert.Single(sent.OfType<SetConversationAwarenessCommand>());

        time.Advance(HubViewModel.RequestTimeout);                 // the device never reports it again
        Assert.False(vm.IsConversationAwarenessPending);
        Assert.True(vm.ConversationAwarenessOn);                   // actual, not the request
        Assert.Equal("You asked for off, but it isn't confirmed. Your AirPods last said on.", vm.ConversationAwarenessCaption);
        Assert.False(vm.IsConversationAwarenessInputEnabled);
        await vm.SetConversationAwarenessAsync(false);
        Assert.Single(sent.OfType<SetConversationAwarenessCommand>());

        // A late matching reading is displayed, but the lock holds until the session ends.
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), awareness: false);
        Assert.False(vm.ConversationAwarenessOn);
        Assert.False(vm.IsConversationAwarenessInputEnabled);
        Assert.Equal(NtpodsDaemonTransport.UncertainAwarenessWriteReason, vm.ConversationAwarenessCaption);

        t.Publish(nearby: true, session: false, audio: true, time.GetUtcNow());
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), awareness: false);
        Assert.True(vm.IsConversationAwarenessInputEnabled);
        await vm.SetConversationAwarenessAsync(true);
        Assert.Equal(2, sent.OfType<SetConversationAwarenessCommand>().Count());
    }

    [Theory]
    [InlineData(CommandOutcome.TimedOut)]
    [InlineData(CommandOutcome.Failed)]
    [InlineData(CommandOutcome.TransportUnavailable)]
    public async Task An_uncertain_hardware_result_locks_awareness(CommandOutcome outcome)
    {
        var (t, vm, time) = CreateVm();
        await using var _ = vm;
        int sends = 0;
        t.OnSend = c =>
        {
            sends++;
            return new CommandResult(c.CommandId, outcome, DataOrigin.Hardware, "no answer");
        };
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), awareness: false);
        await vm.SetConversationAwarenessAsync(true);
        Assert.False(vm.ConversationAwarenessOn);
        Assert.False(vm.IsConversationAwarenessInputEnabled);
        Assert.Equal("You asked for on, but it isn't confirmed. Your AirPods last said off.", vm.ConversationAwarenessCaption);
        await vm.SetConversationAwarenessAsync(true);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task A_transport_that_cannot_write_awareness_gets_no_command_and_says_why()
    {
        var (t, vm, time) = CreateVm();
        await using var _ = vm;
        int sends = 0;
        t.OnSend = c =>
        {
            sends++;
            return new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware);
        };
        t.ConversationAwarenessReadOnlyReason = NtpodsDaemonTransport.AwarenessWriteUnsupportedReason;
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), awareness: true);
        Assert.Equal("On", vm.ConversationAwarenessStateText);  // readable...
        Assert.False(vm.IsConversationAwarenessInputEnabled);   // ...but not writable
        Assert.Equal(NtpodsDaemonTransport.AwarenessWriteUnsupportedReason, vm.ConversationAwarenessCaption);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await vm.SetConversationAwarenessAsync(false);
        Assert.Equal(0, sends);
        Assert.True(vm.ConversationAwarenessToggleValue);
        Assert.Contains(nameof(HubViewModel.ConversationAwarenessToggleValue), raised); // the view is put back
    }
}
