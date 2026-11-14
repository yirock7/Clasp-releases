using System.Globalization;
using System.Text;
using System.Text.Json;
using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Engine;

/// <summary>
/// Strict parser for clasp-engine v1 frame bodies. Invalid UTF-8, invalid JSON, a non-object,
/// a missing <c>type</c> or a field of the wrong type is <see cref="EngineFrameError.Malformed"/>;
/// an undefined <c>type</c> is <see cref="EngineFrameError.UnknownType"/>. Unknown extra fields
/// are ignored (minor-version compatibility). There is no fallback to any other protocol.
/// </summary>
public static class EngineCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Parse a message sent by the engine. Client-direction types are refused.</summary>
    public static EngineMessage ParseEngineMessage(ReadOnlySpan<byte> body)
        => Parse(body) as EngineMessage
           ?? throw new EngineProtocolException(EngineFrameError.UnknownType, "The engine sent a client-direction message type.");

    /// <summary>Parse any v1 message (either direction).</summary>
    public static ClaspMessage Parse(ReadOnlySpan<byte> body)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (ArgumentException ex)
        {
            throw new EngineProtocolException("Frame body is not valid UTF-8.", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new EngineProtocolException("Frame body is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Malformed("Frame body is not a JSON object.");
            var type = RequiredString(root, "type");
            return type switch
            {
                "hello" => new HelloMessage(RequiredString(root, "protocol"), RequiredInt32(root, "major"), RequiredInt32(root, "minor"), RequiredString(root, "client")),
                "list_devices" => new ListDevicesRequest(RequiredString(root, "request_id")),
                "open_session" => new OpenSessionRequest(RequiredString(root, "request_id"), RequiredString(root, "device_id"), ParseSessionMode(RequiredString(root, "mode"))),
                "close_session" => new CloseSessionRequest(RequiredString(root, "request_id"), RequiredInt64(root, "session_id")),
                "set_listening_mode" => new SetListeningModeRequest(RequiredString(root, "request_id"), RequiredInt64(root, "session_id"),
                    EngineWire.ParseListeningMode(RequiredString(root, "mode")) ?? throw Malformed("Unknown listening mode in set_listening_mode.")),
                "set_conversation_awareness" => new SetConversationAwarenessRequest(RequiredString(root, "request_id"),
                    RequiredInt64(root, "session_id"), RequiredBool(root, "enabled")),
                "cancel" => new CancelRequest(RequiredString(root, "request_id"), RequiredString(root, "target_request_id")),
                "hello_ack" => new HelloAckMessage(RequiredInt64(root, "seq"), RequiredString(root, "protocol"), RequiredInt32(root, "major"),
                    RequiredInt32(root, "minor"), RequiredString(root, "engine"), RequiredString(root, "engine_epoch"), StringArray(root, "capabilities")),
                "devices" => new DevicesMessage(RequiredInt64(root, "seq"), RequiredString(root, "request_id"), ParseDevices(root)),
                "result" => new ResultMessage(RequiredInt64(root, "seq"), RequiredString(root, "request_id"),
                    ParseOutcome(RequiredString(root, "outcome")), OptionalString(root, "detail"), OptionalInt64(root, "session_id")),
                "session" => new SessionMessage(RequiredInt64(root, "seq"), RequiredInt64(root, "session_id"), RequiredString(root, "device_id"),
                    ParseSessionState(RequiredString(root, "state")), OptionalString(root, "reason"), ParseWritableModes(root),
                    ParseWritableSettings(root)),
                "observation" => new ObservationMessage(RequiredInt64(root, "seq"), RequiredInt64(root, "session_id"), RequiredString(root, "device_id"),
                    ParseTimestamp(RequiredString(root, "observed_at")), ParseFields(root)),
                "error" => new ErrorMessage(RequiredInt64(root, "seq"), RequiredString(root, "code"), OptionalString(root, "detail") ?? "",
                    OptionalString(root, "request_id")),
                _ => throw new EngineProtocolException(EngineFrameError.UnknownType, "Unknown message type."),
            };
        }
    }

    // ---- Field readers -------------------------------------------------------------------

    private static EngineProtocolException Malformed(string message) => new(EngineFrameError.Malformed, message);

    private static JsonElement Required(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) ? value : throw Malformed($"Missing field '{name}'.");

    private static string RequiredString(JsonElement obj, string name)
    {
        var value = Required(obj, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Malformed($"Field '{name}' must be a string.");
    }

    /// <summary>Absent or null: null. A string: the string. Anything else: malformed.</summary>
    private static string? OptionalString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Malformed($"Field '{name}' must be a string or null.");
    }

    private static int RequiredInt32(JsonElement obj, string name)
    {
        var value = Required(obj, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : throw Malformed($"Field '{name}' must be an integer.");
    }

    private static long RequiredInt64(JsonElement obj, string name)
    {
        var value = Required(obj, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) ? n : throw Malformed($"Field '{name}' must be an integer.");
    }

    private static long? OptionalInt64(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) ? n : throw Malformed($"Field '{name}' must be an integer or null.");
    }

    private static bool RequiredBool(JsonElement obj, string name)
    {
        var value = Required(obj, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Malformed($"Field '{name}' must be a boolean."),
        };
    }

    private static bool? OptionalBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw Malformed($"Field '{name}' must be a boolean or null."),
        };
    }

    private static List<string> StringArray(JsonElement obj, string name)
    {
        var value = Required(obj, name);
        if (value.ValueKind != JsonValueKind.Array) throw Malformed($"Field '{name}' must be an array.");
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Malformed($"Field '{name}' must contain strings.");
            list.Add(item.GetString()!);
        }
        return list;
    }

    // ---- Message parts -------------------------------------------------------------------

    private static EngineSessionMode ParseSessionMode(string value) => value switch
    {
        "observe" => EngineSessionMode.Observe,
        "control" => EngineSessionMode.Control,
        _ => throw Malformed("Unknown session mode."),
    };

    private static EngineOutcome ParseOutcome(string value) => value switch
    {
        "accepted" => EngineOutcome.Accepted,
        "rejected" => EngineOutcome.Rejected,
        "failed" => EngineOutcome.Failed,
        "unsupported" => EngineOutcome.Unsupported,
        "timed_out" => EngineOutcome.TimedOut,
        "cancelled" => EngineOutcome.Cancelled,
        _ => EngineOutcome.Unknown,
    };

    private static EngineSessionState ParseSessionState(string value) => value switch
    {
        "opening" => EngineSessionState.Opening,
        "open" => EngineSessionState.Open,
        "closed" => EngineSessionState.Closed,
        "lost" => EngineSessionState.Lost,
        _ => EngineSessionState.Unknown,
    };

    private static DriverInterfaceStatus ParseDriverInterface(string value) => value switch
    {
        "bound" => DriverInterfaceStatus.Bound,
        "not_found" => DriverInterfaceStatus.NotFound,
        "ambiguous" => DriverInterfaceStatus.Ambiguous,
        "unparsed" => DriverInterfaceStatus.Unparsed,
        _ => DriverInterfaceStatus.Unknown,
    };

    private static List<EngineDevice> ParseDevices(JsonElement root)
    {
        var value = Required(root, "devices");
        if (value.ValueKind != JsonValueKind.Array) throw Malformed("Field 'devices' must be an array.");
        var list = new List<EngineDevice>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Malformed("Device entries must be objects.");
            list.Add(new EngineDevice(
                RequiredString(item, "device_id"),
                RequiredString(item, "name"),
                RequiredBool(item, "aap_service"),
                ParseDriverInterface(RequiredString(item, "driver_interface")),
                OptionalBool(item, "windows_connected")));
        }
        return list;
    }

    /// <summary>Known modes only; a mode this client does not know is not offered.</summary>
    private static List<ListeningMode> ParseWritableModes(JsonElement root)
    {
        var list = new List<ListeningMode>();
        if (!root.TryGetProperty("writable_modes", out var value) || value.ValueKind == JsonValueKind.Null) return list;
        foreach (var name in StringArray(root, "writable_modes"))
        {
            if (EngineWire.ParseListeningMode(name) is { } mode && !list.Contains(mode)) list.Add(mode);
        }
        return list;
    }

    /// <summary>Protocol 1.1. Absent or null: null (a 1.0 engine). Known setting names only.</summary>
    private static List<string>? ParseWritableSettings(JsonElement root)
    {
        if (!root.TryGetProperty("writable_settings", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var list = new List<string>();
        foreach (var name in StringArray(root, "writable_settings"))
        {
            if (name is EngineWire.SettingListeningMode or EngineWire.SettingConversationAwareness && !list.Contains(name)) list.Add(name);
        }
        return list;
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            throw Malformed("Field 'observed_at' is not an ISO 8601 time.");
        return at.ToUniversalTime();
    }

    private static ObservationFields ParseFields(JsonElement root)
    {
        var fields = Required(root, "fields");
        if (fields.ValueKind != JsonValueKind.Object) throw Malformed("Field 'fields' must be an object.");
        EngineListeningMode? mode = null;
        if (fields.TryGetProperty("listening_mode", out var m))
        {
            if (m.ValueKind != JsonValueKind.String) throw Malformed("Field 'listening_mode' must be a string.");
            mode = m.GetString() switch
            {
                "off" => EngineListeningMode.Off,
                "noise_cancellation" => EngineListeningMode.NoiseCancellation,
                "transparency" => EngineListeningMode.Transparency,
                "adaptive" => EngineListeningMode.Adaptive,
                _ => EngineListeningMode.Unrecognised,
            };
        }
        EngineConversationAwareness? awareness = null;
        if (fields.TryGetProperty("conversation_awareness", out var ca))
        {
            if (ca.ValueKind != JsonValueKind.String) throw Malformed("Field 'conversation_awareness' must be a string.");
            awareness = ca.GetString() switch
            {
                "on" => EngineConversationAwareness.On,
                "off" => EngineConversationAwareness.Off,
                _ => EngineConversationAwareness.Unrecognised, // "unknown" (or a future value) is never off
            };
        }
        return new ObservationFields(Battery(fields, "battery.left"), Battery(fields, "battery.right"), Battery(fields, "battery.case"), mode,
            awareness, Ears(fields));
    }

    private static ObservedEars? Ears(JsonElement fields)
    {
        if (!fields.TryGetProperty("ear", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Malformed("Field 'ear' must be an object.");
        return new ObservedEars(EarState(RequiredString(value, "primary")), EarState(RequiredString(value, "secondary")));
    }

    private static EngineEarState EarState(string value) => value switch
    {
        "in_ear" => EngineEarState.InEar,
        "out_of_ear" => EngineEarState.OutOfEar,
        "in_case" => EngineEarState.InCase,
        "disconnected" => EngineEarState.Disconnected,
        _ => EngineEarState.Unrecognised,
    };

    private static ObservedBattery? Battery(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Malformed($"Field '{name}' must be an object.");
        int? level = null;
        if (value.TryGetProperty("level", out var l) && l.ValueKind != JsonValueKind.Null)
        {
            if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out int n)) throw Malformed($"Field '{name}.level' must be an integer or null.");
            level = n is >= 0 and <= 100 ? n : null; // out of range: not a usable reading
        }
        return new ObservedBattery(level, OptionalBool(value, "charging"));
    }
}
