using System.IO.Pipes;
using System.Text.Json.Nodes;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;
using AppleDeviceHub.Core.Interop;

namespace AppleDeviceHub.Core.Tests.Engine;

/// <summary>The shared clasp-engine v1 fixtures (windows/protocol/clasp-engine-v1/fixtures).</summary>
internal static class EngineFixtures
{
    public const string DeviceId = "dev-3f9a1c0e8b7d6a52";
    public const string Epoch = "6f1c2a9e-3b7d-4c11-9a52-0d8e4f7b2c10";

    public static string Root { get; } = FindRoot();

    public static IEnumerable<string> Files(string folder)
        => Directory.EnumerateFiles(Path.Combine(Root, folder), "*.json").OrderBy(f => f, StringComparer.Ordinal);

    public static JsonObject Load(string folder, string name)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, folder, name + ".json")))!.AsObject();

    /// <summary>The <c>message</c> member of a messages/ fixture, as a fresh mutable copy.</summary>
    public static JsonObject Message(string name) => Load("messages", name)["message"]!.DeepClone().AsObject();

    public static byte[] FromHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "protocol", "clasp-engine-v1", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("windows/protocol/clasp-engine-v1/fixtures not found above the test output folder.");
    }
}

/// <summary>
/// An in-process stand-in for clasp-engine: a named-pipe server (unique name per test) in THIS
/// test process. No real engine, driver or radio is involved. Because the server is this
/// process, the client's identity check passes only when the configured engine path is this
/// process's own image (<see cref="Environment.ProcessPath"/>).
/// </summary>
internal sealed class FakeEngine : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly List<JsonObject> _received = new();
    private readonly Task _loop;
    private NamedPipeServerStream? _current;
    private long _seq;
    private int _accepted;
    private int _ended;

    public FakeEngine()
    {
        PipeName = "Clasp.Engine.Test." + Guid.NewGuid().ToString("N");
        _loop = Task.Run(LoopAsync);
    }

    public string PipeName { get; }

    public string Epoch { get; set; } = EngineFixtures.Epoch;

    /// <summary>The messages/ fixture used as the default hello_ack ("hello_ack" is a 1.0 engine, "hello_ack_v11" a 1.1 engine).</summary>
    public string HelloAckFixture { get; set; } = "hello_ack";

    /// <summary>When set, replaces the default hello_ack.</summary>
    public Func<JsonObject, Task>? OnHello { get; set; }

    /// <summary>Requests other than hello and list_devices.</summary>
    public Func<JsonObject, Task>? OnRequest { get; set; }

    /// <summary>The device entries returned for list_devices.</summary>
    public JsonArray Devices { get; set; } = (JsonArray)EngineFixtures.Message("devices")["devices"]!.DeepClone();

    /// <summary>The highest seq sent so far.</summary>
    public long CurrentSeq => _seq;

    public int ConnectionsAccepted => Volatile.Read(ref _accepted);

    public int ConnectionsEnded => Volatile.Read(ref _ended);

    public IReadOnlyList<JsonObject> Received
    {
        get { lock (_received) return _received.ToList(); }
    }

    public JsonObject LastOfType(string type) => Received.Last(m => (string?)m["type"] == type);

    public static ClaspEngineOptions Options(FakeEngine engine, EngineSessionMode mode = EngineSessionMode.Control, string? sha = null, string? enginePath = null)
        => new(enginePath ?? Environment.ProcessPath!, sha, EngineFixtures.DeviceId, mode)
        {
            PipeName = engine.PipeName,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            HandshakeTimeout = TimeSpan.FromSeconds(3),
            RequestTimeout = TimeSpan.FromSeconds(3),
            OpenSessionTimeout = TimeSpan.FromSeconds(3),
        };

    /// <summary>
    /// Send one engine message. A message without <c>seq</c> gets the next one. The seq is assigned
    /// and the frame written under one lock, so concurrent senders (the test body and the request
    /// handler on the read loop) put frames on the pipe in seq order, as the real engine does. The
    /// client drops a non-increasing seq, so an inversion here silently loses a message.
    /// </summary>
    public async Task SendAsync(JsonObject message)
    {
        await _write.WaitAsync();
        try
        {
            // Parse the JSON text: a JsonValue built from an int does not convert to long via TryGetValue.
            if (message["seq"] is JsonValue v && long.TryParse(v.ToJsonString(), out long seq)) _seq = Math.Max(_seq, seq);
            else message["seq"] = ++_seq;
            await WriteFrameAsync(EngineFraming.Encode(System.Text.Encoding.UTF8.GetBytes(message.ToJsonString())));
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task SendRawAsync(byte[] bytes)
    {
        await _write.WaitAsync();
        try
        {
            await WriteFrameAsync(bytes);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task WriteFrameAsync(byte[] bytes)
    {
        try
        {
            var pipe = _current ?? throw new InvalidOperationException("No client connected.");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
        }
        catch (IOException)
        {
            // The client already closed (for example right after reading a bad header): fine.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Close the current client connection (the engine "goes away").</summary>
    public void DropClient() => _current?.Dispose();

    /// <summary>Reset the sequence (a new engine process starts again at 1).</summary>
    public void RestartSequence() => _seq = 0;

    public static JsonObject Result(string requestId, string outcome, long? sessionId = null, string? detail = null)
    {
        var o = new JsonObject { ["type"] = "result", ["request_id"] = requestId, ["outcome"] = outcome, ["detail"] = detail };
        if (sessionId is { } id) o["session_id"] = id;
        return o;
    }

    public static JsonObject Session(long sessionId, string state, params string[] writable) => new()
    {
        ["type"] = "session",
        ["session_id"] = sessionId,
        ["device_id"] = EngineFixtures.DeviceId,
        ["state"] = state,
        ["reason"] = null,
        ["writable_modes"] = new JsonArray(writable.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
    };

    public static JsonObject Observation(long sessionId, string observedAt, JsonObject fields) => new()
    {
        ["type"] = "observation",
        ["session_id"] = sessionId,
        ["device_id"] = EngineFixtures.DeviceId,
        ["observed_at"] = observedAt,
        ["fields"] = fields,
    };

    /// <summary>
    /// Answer open_session in the engine's reference order: session "opening", result accepted
    /// with the session id, session "open" (control: three writable modes; observe: none).
    /// </summary>
    public void AcceptSessions(long sessionId = 1, params string[] writable)
    {
        var modes = writable.Length == 0 ? new[] { "noise_cancellation", "transparency", "adaptive" } : writable;
        OnRequest = async m =>
        {
            switch ((string?)m["type"])
            {
                case "open_session":
                    await SendAsync(Session(sessionId, "opening"));
                    await SendAsync(Result((string)m["request_id"]!, "accepted", sessionId));
                    await SendAsync(Session(sessionId, "open", (string?)m["mode"] == "control" ? modes : Array.Empty<string>()));
                    break;
                case "close_session":
                    await SendAsync(Result((string)m["request_id"]!, "accepted"));
                    await SendAsync(Session((long)m["session_id"]!, "closed"));
                    break;
                case "set_listening_mode":
                case "set_conversation_awareness":
                    await SendAsync(Result((string)m["request_id"]!, "accepted"));
                    break;
            }
        };
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
            }
            catch (Exception)
            {
                await server.DisposeAsync();
                return;
            }
            _current = server;
            Interlocked.Increment(ref _accepted);
            try
            {
                while (true)
                {
                    var body = await EngineFraming.ReadFrameAsync(server, _stop.Token);
                    if (body is null) break;
                    var message = JsonNode.Parse(body)!.AsObject();
                    lock (_received) _received.Add(message);
                    await HandleAsync(message);
                }
            }
            catch (Exception)
            {
                // The client went away, or the test dropped it.
            }
            finally
            {
                _current = null;
                await server.DisposeAsync();
                Interlocked.Increment(ref _ended);
            }
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "hello":
                if (OnHello is { } hello)
                {
                    await hello(message);
                    return;
                }
                var ack = EngineFixtures.Message(HelloAckFixture);
                ack["engine_epoch"] = Epoch;
                ack.Remove("seq");
                await SendAsync(ack);
                return;
            case "list_devices":
                await SendAsync(new JsonObject
                {
                    ["type"] = "devices",
                    ["request_id"] = (string?)message["request_id"],
                    ["devices"] = Devices.DeepClone(),
                });
                return;
            default:
                if (OnRequest is { } handler) await handler(message);
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _current?.Dispose();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
        _stop.Dispose();
        _write.Dispose();
    }
}

/// <summary>A hardware transport (ControlSessionOnly) whose states and status each test scripts.</summary>
internal sealed class ScriptedHardwareTransport : IAirPodsTransport, IHardwareTransportStatus
{
    private long _sequence;
    private string? _reason;

    public const string Id = EngineFixtures.DeviceId;

    public Func<DeviceCommand, CommandResult> OnSend { get; set; }
        = c => new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware);

    public TransportInfo Info { get; } = new(NtpodsDaemonTransport.EngineName, DataOrigin.Hardware, HubContract.Version, "test");

    public DeviceState? Current { get; private set; }

    public string? UnavailableReason => _reason;

    public string SourceText { get; set; } = "Hardware: Clasp engine, observe session";

    public string? ReadOnlyReason { get; set; } = NtpodsDaemonTransport.ObserveReadOnlyReason;

    public string? ConversationAwarenessReadOnlyReason { get; set; }

    /// <summary>Capabilities of the published states (default: what the real hardware transport claims).</summary>
    public DeviceCapabilities Capabilities { get; set; } = NtpodsDaemonTransport.HardwareCapabilities;

    public event EventHandler<DeviceState>? StateChanged;

    public event EventHandler? StatusChanged;

    public event EventHandler<string>? Diagnostic { add { } remove { } }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
        => Task.FromResult(OnSend(command));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void SetReason(string? reason)
    {
        _reason = reason;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public DeviceState Publish(bool? nearby, bool? session, bool? audio, DateTimeOffset at,
        IReadOnlyList<ListeningMode>? writable = null, ListeningMode? mode = null, bool? connecting = false, bool? awareness = null)
    {
        static Observation<bool> Obs(bool? v, DateTimeOffset t) => v is bool b ? Observation<bool>.At(b, t) : default;
        var state = new DeviceState(Id, "AirPods Pro", "", DataOrigin.Hardware, ++_sequence, at,
            Capabilities,
            new LinkState(Obs(nearby, at), Obs(session is null ? null : connecting, at), Obs(session, at), Obs(audio, at)),
            new BatteryState(BatteryComponent.From(80, false, at), BatteryComponent.From(79, null, at), BatteryComponent.Unknown),
            new ListeningState(mode is { } m ? Observation<ListeningMode>.At(m, at) : default, writable ?? Array.Empty<ListeningMode>()),
            awareness is bool on ? Observation<bool>.At(on, at) : default, default);
        Current = state;
        StateChanged?.Invoke(this, state);
        return state;
    }
}
