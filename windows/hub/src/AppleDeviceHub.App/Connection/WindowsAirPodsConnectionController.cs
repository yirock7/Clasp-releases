using AppleDeviceHub.Core.Presentation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.InteropServices.WindowsRuntime;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Resources;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace AppleDeviceHub.App.Connection;

/// <summary>
/// Reads paired Bluetooth identities and their Windows render endpoints, then exposes the
/// documented one-shot Bluetooth-audio reconnect property. All WinRT/Core Audio/KS work runs
/// on a worker thread. It never pairs, scans, opens GATT/RFCOMM, selects an output, or changes
/// Bluetooth profiles.
/// </summary>
public sealed partial class WindowsAirPodsConnectionController : IAirPodsConnectionController, IDisposable
{
    private const string AepContainerIdKey = "System.Devices.Aep.ContainerId";
    private const string AepIsPairedKey = "System.Devices.Aep.IsPaired";
    private const string AepProtocolIdKey = "System.Devices.Aep.ProtocolId";
    private const string ItemNameKey = "System.ItemNameDisplay";
    // Optional Windows battery level of the Handsfree (BTHENUM HFP) child node: DEVPKEY {104EA319-...} pid 2.
    // Absent whenever Handsfree is disabled; absence is normal.
    private const string HandsfreeBatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    // Handsfree service class 0x111E in a BTHENUM service node id. Windows only reports a battery
    // through this node, so its absence (service turned off in Windows) explains a missing reading.
    private const string HandsfreeServiceMarker = "{0000111E-0000-1000-8000-00805F9B34FB}";
    // Missing device nodes can be a transient Windows enumeration snapshot. Retry a given
    // ContainerId at most once every 30 seconds instead of keeping a negative result forever.
    private static readonly AppleIdentityRetryCache AppleIdentityRetries = new(TimeProvider.System, TimeSpan.FromSeconds(30));
    private const uint AllDeviceStates = 0xF;
    private const uint ActiveDeviceState = 0x1;
    private const int RenderFlow = 0;
    private const int NotFound = unchecked((int)0x80070490);
    private const uint ClsctxAll = 0x17;
    private const uint StgmRead = 0;
    private const int KsPropertyGet = 0x1;
    private const int KsPropertyBasicSupport = 0x200;
    private const int KsPropertyOneshotReconnect = 0;
    private const int KsPropertyOneshotDisconnect = 1;
    private const int CoInitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private static readonly Guid ClassicProtocolId = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    private static readonly Guid MmDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MmDeviceEnumeratorInterfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid ContainerPropertySet = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");
    private static readonly Guid BluetoothAudioPropertySet = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const string DeviceTopologyInterfaceIdText = "2A07407E-6497-4A18-9787-32F79BD0D98F";
    private static readonly Guid DeviceTopologyInterfaceId = new(DeviceTopologyInterfaceIdText);
    private static readonly Guid KsControlInterfaceId = new("28F54685-06FD-11D2-B27A-00A0C9223196");

    private static readonly object MetadataLogGate = new();
    private static (int Paired, int AssociatedEndpoints, int ReconnectSupportedEndpoints)? _lastMetadataCounts;
    private static int _firstMetadataProgressLogged;

    private readonly object _gate = new();
    private readonly TimeSpan _operationWait = TimeSpan.FromSeconds(5);
    private Task<ConnectionSnapshot>? _activeRead;
    private Task<ConnectionAttemptResult>? _activeConnect;
    private Task? _activeOperation;
    private ConnectionSnapshot? _pendingReadResult;
    private ConnectionSnapshot _lastSnapshot = ConnectionSnapshot.Incomplete(Strings.WinConn_StillChecking);
    private bool _activeReadTimedOut;
    private bool _disposed;

    public event EventHandler? Changed;

    public bool IsBusy
    {
        get { lock (_gate) return _activeOperation is not null; }
    }

    public async Task<ConnectionSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Task<ConnectionSnapshot>? task;
        ConnectionSnapshot? immediateResult = null;
        ConnectionSnapshot? busyResult = null;
        lock (_gate)
        {
            if (_disposed)
                return ConnectionSnapshot.Incomplete(Strings.WinConn_Closed);

            if (_pendingReadResult is not null)
            {
                immediateResult = _pendingReadResult;
                _pendingReadResult = null;
                task = null;
            }
            else if (_activeConnect is not null)
            {
                busyResult = IncompleteWithLastSnapshot(Strings.WinConn_WindowsBusy);
                task = null;
            }
            else
            {
                task = _activeRead ?? StartReadLocked();
            }
        }

        if (immediateResult is not null) return immediateResult;
        if (busyResult is not null) return busyResult;
        try
        {
            return await task!.WaitAsync(_operationWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                // A scan can finish between WaitAsync timing out and this lock. In that case,
                // return its result directly instead of discarding it or retaining a duplicate.
                if (task!.IsCompletedSuccessfully) return task.Result;
                if (ReferenceEquals(_activeRead, task)) _activeReadTimedOut = true;
                return IncompleteWithLastSnapshot(Strings.WinConn_SlowCheck);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                // The view model can stop waiting before the controller's longer timeout. Keep
                // the late result only while this controller remains usable.
                if (!_disposed)
                {
                    if (task!.IsCompletedSuccessfully)
                        _pendingReadResult ??= task.Result;
                    else if (ReferenceEquals(_activeRead, task))
                        _activeReadTimedOut = true;
                }
            }
            throw;
        }
    }

    public Task<ConnectionAttemptResult> ReconnectAsync(
        string deviceId,
        string expectedContainerId,
        CancellationToken cancellationToken = default)
        => RequestOneShotAsync(OneShot.Reconnect, deviceId, expectedContainerId, cancellationToken);

    public Task<ConnectionAttemptResult> DisconnectAsync(
        string deviceId,
        string expectedContainerId,
        CancellationToken cancellationToken = default)
        => RequestOneShotAsync(OneShot.Disconnect, deviceId, expectedContainerId, cancellationToken);

    /// <summary>The two documented one-shot Bluetooth-audio requests (KSPROPSETID_BtAudio).</summary>
    private sealed record OneShot(int PropertyId, string Noun)
    {
        public static readonly OneShot Reconnect = new(KsPropertyOneshotReconnect, "reconnect");
        public static readonly OneShot Disconnect = new(KsPropertyOneshotDisconnect, "disconnect");

        // Noun stays English: it only appears in log lines. User-facing text is one whole sentence per request kind.
        private bool IsReconnect => PropertyId == KsPropertyOneshotReconnect;

        /// <summary>"Windows can't reconnect/disconnect this device."</summary>
        public string CantText => IsReconnect ? Strings.WinConn_CantReconnect : Strings.WinConn_CantDisconnect;

        /// <summary>"Windows is still trying to reconnect/disconnect. Wait a moment, then try again."</summary>
        public string StillTryingText => IsReconnect ? Strings.WinConn_StillTryingReconnect : Strings.WinConn_StillTryingDisconnect;

        /// <summary>"Couldn't reconnect/disconnect. Try again."</summary>
        public string CouldntText => IsReconnect ? Strings.WinConn_CouldntReconnect : Strings.WinConn_CouldntDisconnect;

        /// <summary>"Windows couldn't reconnect/disconnect. Try again."</summary>
        public string WindowsCouldntText => IsReconnect ? Strings.WinConn_WindowsCouldntReconnect : Strings.WinConn_WindowsCouldntDisconnect;

        /// <summary>"Asking Windows to reconnect/disconnect…"</summary>
        public string AskingText => IsReconnect ? Strings.WinConn_AskingWindowsToReconnect : Strings.WinConn_AskingWindowsToDisconnect;
    }

    private async Task<ConnectionAttemptResult> RequestOneShotAsync(
        OneShot request,
        string deviceId,
        string expectedContainerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(deviceId)
            || !ConnectionIdentity.IsUsableContainerId(expectedContainerId))
            return new(ConnectionAttemptKind.Unsupported, Strings.WinConn_ChooseDeviceFirst);

        Task<ConnectionAttemptResult>? task;
        lock (_gate)
        {
            if (_disposed)
                return new(ConnectionAttemptKind.Failed, Strings.WinConn_Closed);
            if (_activeOperation is not null)
                return new(ConnectionAttemptKind.Busy, Strings.WinConn_WindowsBusy);
            if (!_lastSnapshot.IsComplete)
                return new(ConnectionAttemptKind.Unsupported, Strings.WinConn_StillCheckingTryAgain);

            var matchingIds = _lastSnapshot.Devices.Where(item =>
                string.Equals(item.Id, deviceId, StringComparison.Ordinal)).ToArray();
            var matchingContainer = _lastSnapshot.Devices.Where(item =>
                string.Equals(item.ContainerId, expectedContainerId, StringComparison.OrdinalIgnoreCase)).ToArray();
            // CanReconnect means the endpoint's audio driver supports the KSPROPSETID_BtAudio
            // one-shot requests; disconnect uses the same driver interface.
            if (matchingIds.Length != 1
                || matchingContainer.Length != 1
                || !string.Equals(matchingIds[0].ContainerId, expectedContainerId, StringComparison.OrdinalIgnoreCase)
                || !matchingIds[0].HasAvailableAudioEndpoint
                || !matchingIds[0].CanReconnect)
            {
                return new(ConnectionAttemptKind.Unsupported, request.CantText);
            }

            task = StartConnectLocked(
                request,
                deviceId,
                ConnectionIdentity.NormalizeContainerId(expectedContainerId)!,
                cancellationToken);
        }

        try
        {
            return await task.WaitAsync(_operationWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The worker is deliberately kept as the active operation. A KS call that does not
            // return cannot be safely canceled, so no second native request may overlap it.
            return new(ConnectionAttemptKind.TimedOut, request.StillTryingText);
        }
    }
    private Task<ConnectionSnapshot> StartReadLocked()
    {
        var completion = new TaskCompletionSource<ConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeRead = completion.Task;
        _activeReadTimedOut = false;
        _activeOperation = completion.Task;
        _ = RunReadAsync(completion);
        return completion.Task;
    }

    private async Task RunReadAsync(TaskCompletionSource<ConnectionSnapshot> completion)
    {
        ConnectionSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(ReadSnapshotCore).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LocalLog.Write($"Windows device metadata could not be read ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
            snapshot = ConnectionSnapshot.Incomplete(Strings.WinConn_CouldntReadDevices);
        }

        lock (_gate)
        {
            if (snapshot.IsComplete && !_disposed) _lastSnapshot = snapshot;
            if (ReferenceEquals(_activeRead, completion.Task))
            {
                // If every waiter timed out, keep this finished result for one future refresh.
                // It is consumed once by ReadSnapshotAsync; read completion never raises Changed.
                if (_activeReadTimedOut && !_disposed) _pendingReadResult = snapshot;
                completion.TrySetResult(snapshot);
                _activeRead = null;
                _activeReadTimedOut = false;
                _activeOperation = null;
            }
            else
            {
                completion.TrySetResult(snapshot);
            }
        }

    }

    private Task<ConnectionAttemptResult> StartConnectLocked(OneShot request, string deviceId, string containerId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ConnectionAttemptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeConnect = completion.Task;
        _activeOperation = completion.Task;
        _ = RunConnectAsync(request, deviceId, containerId, completion, cancellationToken);
        return completion.Task;
    }

    private async Task RunConnectAsync(
        OneShot request,
        string deviceId,
        string containerId,
        TaskCompletionSource<ConnectionAttemptResult> completion,
        CancellationToken cancellationToken)
    {
        ConnectionAttemptResult result;
        try
        {
            result = await Task.Run(
                () => RequestOneShot(request, deviceId, containerId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new(ConnectionAttemptKind.Failed, Strings.WinConn_CanceledBeforeRequest);
        }
        catch (Exception exception)
        {
            LocalLog.Write($"Windows audio {request.Noun} request failed ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
            result = new(ConnectionAttemptKind.Failed, request.CouldntText);
        }

        lock (_gate)
        {
            if (ReferenceEquals(_activeConnect, completion.Task))
            {
                _activeConnect = null;
                _activeOperation = null;
            }
        }
        completion.TrySetResult(result);
        RaiseChanged();
    }
    private ConnectionSnapshot ReadSnapshotCore()
    {
        var stage = "COM apartment initialization";
        try
        {
            using var apartment = ComApartment.Enter();
            stage = "paired Bluetooth device discovery";
            var logProgress = Interlocked.Exchange(ref _firstMetadataProgressLogged, 1) == 0;
            if (logProgress) LocalLog.Write("Windows Bluetooth metadata stage started: paired-device discovery.");
            var paired = ReadPairedBluetoothDevices();
            if (logProgress) LocalLog.Write($"Windows Bluetooth metadata stage complete: paired-device discovery returned {paired.Count} filtered identities.");
            var containerCounts = paired
                .GroupBy(device => device.ContainerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var uniqueContainers = containerCounts
                .Where(item => item.Value == 1)
                .Select(item => item.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            stage = "Windows render endpoint and reconnect-capability discovery";
            if (logProgress) LocalLog.Write("Windows Bluetooth metadata stage started: associated render endpoints and reconnect capabilities.");
            var endpoints = ReadAudioEndpoints(uniqueContainers);
            if (logProgress) LocalLog.Write($"Windows Bluetooth metadata stage complete: associated-endpoints={endpoints.Count}; reconnect-supported-endpoints={endpoints.Count(endpoint => endpoint.SupportsReconnect)}.");
            stage = "paired Bluetooth metadata projection";
            var devices = paired.Select(device =>
            {
                var ambiguousContainer = containerCounts[device.ContainerId] != 1;
                var matches = ambiguousContainer
                    ? Array.Empty<ConnectionAudioEndpoint>()
                    : endpoints.Where(endpoint =>
                        string.Equals(endpoint.ContainerId, device.ContainerId, StringComparison.OrdinalIgnoreCase)).ToArray();
                var hasEndpoint = matches.Length > 0;
                var connected = matches.Any(endpoint => endpoint.IsAvailable);
                var currentOutput = matches.Any(endpoint => endpoint.IsCurrentOutput);
                var supportsReconnect = matches.Any(endpoint => endpoint.SupportsReconnect);
                var detail = ambiguousContainer
                    ? Strings.WinConn_AmbiguousAudio
                    : connected
                        ? null
                        : !hasEndpoint
                            ? Strings.WinConn_PairedNotReady
                            : supportsReconnect
                                ? null
                                : matches.Select(endpoint => endpoint.Detail).Contains(ConnectionDriverState.MissingDriverDetail)
                                    ? ConnectionDriverState.MissingDriverDetail
                                    : matches.Select(endpoint => endpoint.Detail).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
                                    ?? Strings.WinConn_CantReconnect;
                // Ambiguous containers cannot be tied to one identity, so read nothing for them.
                var (modelId, windowsBattery, hasHandsfree) = ambiguousContainer
                    ? ((ushort?)null, (int?)null, (bool?)null)
                    : ReadAppleIdentity(device.ContainerId);
                return new ConnectionDevice(
                    device.Id,
                    device.Name,
                    device.ContainerId,
                    hasEndpoint,
                    connected,
                    currentOutput,
                    supportsReconnect,
                    detail,
                    modelId,
                    windowsBattery,
                    hasHandsfree);
            }).ToArray();

            LogMetadataCounts(paired.Count, endpoints.Count,
                endpoints.Count(endpoint => endpoint.SupportsReconnect));
            return new ConnectionSnapshot(true, devices, endpoints, null);
        }
        catch (Exception exception)
        {
            LocalLog.Write($"Windows Bluetooth metadata failed at {stage}: {LogText.Exception(exception)}");
            throw;
        }
    }

    /// <summary>
    /// Looks at the Windows device nodes that share this paired device's ContainerId: the Apple model
    /// id comes from a node id containing VID&amp;0001004C_PID&amp;XXXX, and Windows' Handsfree battery
    /// level from the optional {104EA319-...} 2 property. Both are read-only metadata. A missing
    /// battery is normal (there is no Handsfree node while Handsfree is disabled); any failure
    /// yields nulls, never an error. A missing Apple marker is retried on a later refresh after a
    /// bounded per-container cooldown, because Windows can return a temporarily empty node snapshot.
    /// HasHandsfree reports whether a Handsfree (HFP, 0x111E) service node exists; it is null when
    /// the nodes were not read, so the UI only explains a missing battery from a real read.
    /// </summary>
    private static (ushort? ModelId, int? BatteryPercent, bool? HasHandsfree) ReadAppleIdentity(string containerId)
    {
        if (!AppleIdentityRetries.TryBeginRead(containerId))
            return WithoutHandsfree(AppleIdentityRetries.TransientFallback(containerId));
        try
        {
            var filter = $"System.Devices.ContainerId:=\"{{{containerId}}}\"";
            var nodes = DeviceInformation.FindAllAsync(
                    filter,
                    new[] { HandsfreeBatteryKey },
                    DeviceInformationKind.Device)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            ushort? model = null;
            int? battery = null;
            var handsfree = false;
            foreach (var node in nodes)
            {
                model ??= AppleBluetoothIdentity.TryParseModelId(node.Id);
                handsfree |= node.Id.Contains(HandsfreeServiceMarker, StringComparison.OrdinalIgnoreCase);
                if (battery is null
                    && node.Properties.TryGetValue(HandsfreeBatteryKey, out var raw)
                    && raw is not null
                    && TryConvertBattery(raw, out var level))
                    battery = level;
            }
            if (model is ushort foundModel)
            {
                AppleIdentityRetries.RememberFound(containerId, foundModel);
                return (foundModel, battery, handsfree);
            }

            AppleIdentityRetries.RememberMissing(containerId);
            return WithoutHandsfree(AppleIdentityRetries.TransientFallback(containerId));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            AppleIdentityRetries.RememberMissing(containerId);
            return WithoutHandsfree(AppleIdentityRetries.TransientFallback(containerId));
        }
    }

    private static (ushort? ModelId, int? BatteryPercent, bool? HasHandsfree) WithoutHandsfree(
        (ushort? ModelId, int? BatteryPercent) fallback) => (fallback.ModelId, fallback.BatteryPercent, null);

    private static bool TryConvertBattery(object raw, out int percent)
    {
        percent = raw switch
        {
            byte value => value,
            sbyte value => value,
            ushort value => value,
            short value => value,
            uint value when value <= 100 => (int)value,
            int value => value,
            _ => -1,
        };
        if (percent is >= 0 and <= 100) return true;
        percent = 0;
        return false;
    }

    private static void LogMetadataCounts(int paired, int associatedEndpoints, int reconnectSupportedEndpoints)
    {
        var counts = (Paired: paired, AssociatedEndpoints: associatedEndpoints,
            ReconnectSupportedEndpoints: reconnectSupportedEndpoints);
        lock (MetadataLogGate)
        {
            if (_lastMetadataCounts is { } previous && previous.Equals(counts)) return;
            _lastMetadataCounts = counts;
            LocalLog.Write($"Paired Bluetooth metadata: paired={counts.Paired}; associated-endpoints={counts.AssociatedEndpoints}; reconnect-supported-endpoints={counts.ReconnectSupportedEndpoints}.");
        }
    }
    private static IReadOnlyList<PairedBluetoothDevice> ReadPairedBluetoothDevices()
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var properties = new[] { ItemNameKey, AepContainerIdKey, AepIsPairedKey, AepProtocolIdKey };
        var information = DeviceInformation.FindAllAsync(
                selector,
                properties,
                DeviceInformationKind.AssociationEndpoint)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        var paired = new List<PairedBluetoothDevice>();
        foreach (var device in information)
        {
            if (!device.Properties.TryGetValue(AepIsPairedKey, out var pairedValue)
                || pairedValue is not true
                || GetGuidProperty(device.Properties, AepProtocolIdKey) != ClassicProtocolId)
                continue;

            var containerId = GetContainerId(device.Properties);
            if (containerId is null || string.IsNullOrWhiteSpace(device.Id)) continue;
            paired.Add(new PairedBluetoothDevice(device.Id, device.Name ?? "", containerId));
        }
        return paired;
    }

    private static string? GetContainerId(IReadOnlyDictionary<string, object> properties)
    {
        if (!properties.TryGetValue(AepContainerIdKey, out var raw) || raw is null) return null;
        var text = raw is Guid id ? id.ToString("D") : raw.ToString();
        return ConnectionIdentity.NormalizeContainerId(text);
    }

    private static Guid? GetGuidProperty(IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var raw) || raw is null) return null;
        if (raw is Guid id) return id;
        return raw is string text && Guid.TryParse(text, out var parsed) ? parsed : null;
    }
    private static IReadOnlyList<ConnectionAudioEndpoint> ReadAudioEndpoints(HashSet<string> pairedContainers)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = CreateDeviceEnumerator();
            Check(enumerator.EnumAudioEndpoints(RenderFlow, AllDeviceStates, out collection));
            Check(collection.GetCount(out var count));
            var defaultPlaybackOutputId = ReadDefaultOutputId(enumerator);
            var endpoints = new List<ConnectionAudioEndpoint>();

            for (uint index = 0; index < count; index++)
            {
                IMMDevice? endpoint = null;
                try
                {
                    Check(collection.Item(index, out endpoint));
                    Check(endpoint.GetId(out var endpointId));
                    Check(endpoint.GetState(out var state));
                    var (name, containerId) = ReadEndpointProperties(endpoint);
                    if (containerId is null || !pairedContainers.Contains(containerId)) continue;

                    var currentOutput = string.Equals(defaultPlaybackOutputId, endpointId, StringComparison.OrdinalIgnoreCase);
                    var supportsReconnect = SupportsReconnect(enumerator, endpoint, out var detail);
                    if (!supportsReconnect && ConnectionDriverState.IsEndpointNotPresent(state))
                        detail = ConnectionDriverState.MissingDriverDetail;
                    endpoints.Add(new ConnectionAudioEndpoint(
                        endpointId,
                        name,
                        containerId,
                        (state & ActiveDeviceState) != 0,
                        currentOutput,
                        supportsReconnect)
                    {
                        Detail = detail,
                    });
                }
                finally { Release(endpoint); }
            }

            return endpoints.AsReadOnly();
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
    }

    private static string? ReadDefaultOutputId(IMMDeviceEnumerator enumerator)
    {
        static string? ReadRole(IMMDeviceEnumerator enumerator, int role)
        {
            IMMDevice? device = null;
            try
            {
                var result = enumerator.GetDefaultAudioEndpoint(RenderFlow, role, out device);
                if (result == NotFound) return null;
                Check(result);
                Check(device.GetId(out var id));
                return id;
            }
            finally { Release(device); }
        }

        var multimedia = ReadRole(enumerator, 1);
        var console = ReadRole(enumerator, 0);
        return ConnectionOutputProjection.SelectPlaybackOutputId(multimedia, console);
    }
    private static (string Name, string? ContainerId) ReadEndpointProperties(IMMDevice endpoint)
    {
        IPropertyStore? store = null;
        try
        {
            Check(endpoint.OpenPropertyStore(StgmRead, out store));
            var containerKey = new PropertyKey(ContainerPropertySet, 2);
            var containerValue = default(PropVariant);
            Check(store.GetValue(ref containerKey, out containerValue));
            string? containerId;
            try
            {
                containerId = containerValue.Type == 72 && containerValue.Pointer != IntPtr.Zero
                    ? ConnectionIdentity.NormalizeContainerId(Marshal.PtrToStructure<Guid>(containerValue.Pointer).ToString("D"))
                    : null;
            }
            finally { _ = PropVariantClear(ref containerValue); } // Best-effort PROPVARIANT cleanup.

            var nameKey = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
            var nameValue = default(PropVariant);
            string name;
            try
            {
                var result = store.GetValue(ref nameKey, out nameValue);
                name = result >= 0 && nameValue.Type == 31 && nameValue.Pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(nameValue.Pointer) ?? ""
                    : "";
            }
            finally { _ = PropVariantClear(ref nameValue); } // Best-effort PROPVARIANT cleanup.
            return (name, containerId);
        }
        finally { Release(store); }
    }

    private static bool SupportsReconnect(IMMDeviceEnumerator enumerator, IMMDevice endpoint, out string? detail)
    {
        IDeviceTopology? topology = null;
        try
        {
            var iid = DeviceTopologyInterfaceId;
            Check(endpoint.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var rawTopology));
            topology = CastComInterface<IDeviceTopology>(rawTopology);
            Check(topology.GetConnectorCount(out var connectorCount));
            var lastError = Strings.WinConn_CantReconnect;

            for (uint index = 0; index < connectorCount; index++)
            {
                IConnector? connector = null;
                IMMDevice? adapter = null;
                IKsControl? ks = null;
                IntPtr adapterId = IntPtr.Zero;
                try
                {
                    Check(topology.GetConnector(index, out connector));
                    var connectedResult = connector.GetDeviceIdConnectedTo(out adapterId);
                    if (connectedResult < 0 || adapterId == IntPtr.Zero) continue;
                    var id = Marshal.PtrToStringUni(adapterId);
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    Check(enumerator.GetDevice(id, out adapter));
                    var ksIid = KsControlInterfaceId;
                    Check(adapter.Activate(ref ksIid, ClsctxAll, IntPtr.Zero, out var rawKs));
                    ks = CastComInterface<IKsControl>(rawKs);
                    if (QueryReconnectSupport(ks, out var queryError))
                    {
                        detail = null;
                        return true;
                    }
                    lastError = queryError;
                }
                catch (COMException exception)
                {
                    lastError = ConnectionDriverState.IsMissingInterface(exception.HResult)
                        ? ConnectionDriverState.MissingDriverDetail
                        : Strings.WinConn_CantReconnect;
                }
                catch (InvalidCastException)
                {
                    lastError = Strings.WinConn_CantReconnect;
                }
                finally
                {
                    if (adapterId != IntPtr.Zero) Marshal.FreeCoTaskMem(adapterId);
                    Release(ks);
                    Release(adapter);
                    Release(connector);
                }
            }

            detail = lastError;
            return false;
        }
        catch (COMException exception)
        {
            detail = ConnectionDriverState.IsMissingInterface(exception.HResult)
                ? ConnectionDriverState.MissingDriverDetail
                : Strings.WinConn_CantReconnect;
            return false;
        }
        catch (InvalidCastException)
        {
            detail = Strings.WinConn_CantReconnect;
            return false;
        }
        finally { Release(topology); }
    }
    private static bool QueryReconnectSupport(IKsControl ks, out string detail)
        => QueryOneShotSupport(ks, OneShot.Reconnect, out detail);

    private static bool QueryOneShotSupport(IKsControl ks, OneShot request, out string detail)
    {
        var property = new KsProperty
        {
            Set = BluetoothAudioPropertySet,
            Id = request.PropertyId,
            Flags = KsPropertyBasicSupport,
        };
        var accessBuffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(accessBuffer, 0);
            var result = ks.KsProperty(
                ref property,
                Marshal.SizeOf<KsProperty>(),
                accessBuffer,
                sizeof(uint),
                out var bytesReturned);
            if (result < 0)
            {
                detail = request.CantText;
                return false;
            }

            var access = unchecked((uint)Marshal.ReadInt32(accessBuffer));
            if (bytesReturned < sizeof(uint) || (access & KsPropertyGet) == 0)
            {
                detail = request.CantText;
                return false;
            }

            detail = "";
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(accessBuffer);
        }
    }

    private static ConnectionAttemptResult RequestOneShot(
        OneShot request,
        string deviceId,
        string containerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var apartment = ComApartment.Enter();

        // Re-check the exact paired identity on the worker immediately before touching audio
        // topology. Names are display-only; neither a name nor a shared ContainerId is enough.
        var paired = ReadPairedBluetoothDevices();
        cancellationToken.ThrowIfCancellationRequested();
        var matchingIds = paired.Where(device =>
            string.Equals(device.Id, deviceId, StringComparison.Ordinal)).ToArray();
        if (matchingIds.Length != 1
            || !string.Equals(matchingIds[0].ContainerId, containerId, StringComparison.OrdinalIgnoreCase))
        {
            return new(ConnectionAttemptKind.Unsupported,
                Strings.WinConn_DeviceChanged);
        }

        if (paired.Any(device => !string.Equals(device.Id, deviceId, StringComparison.Ordinal)
            && string.Equals(device.ContainerId, containerId, StringComparison.OrdinalIgnoreCase)))
        {
            return new(ConnectionAttemptKind.Unsupported,
                Strings.WinConn_AmbiguousDevice);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = CreateDeviceEnumerator();
            Check(enumerator.EnumAudioEndpoints(RenderFlow, AllDeviceStates, out collection));
            Check(collection.GetCount(out var count));

            var foundEndpoint = false;
            var lastError = request.CantText;
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMMDevice? endpoint = null;
                IDeviceTopology? topology = null;
                try
                {
                    Check(collection.Item(index, out endpoint));
                    var (_, endpointContainerId) = ReadEndpointProperties(endpoint);
                    if (!string.Equals(endpointContainerId, containerId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foundEndpoint = true;
                    var topologyIid = DeviceTopologyInterfaceId;
                    Check(endpoint.Activate(ref topologyIid, ClsctxAll, IntPtr.Zero, out var rawTopology));
                    topology = CastComInterface<IDeviceTopology>(rawTopology);
                    Check(topology.GetConnectorCount(out var connectorCount));

                    for (uint connectorIndex = 0; connectorIndex < connectorCount; connectorIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        IConnector? connector = null;
                        IMMDevice? adapter = null;
                        IKsControl? ks = null;
                        IntPtr adapterId = IntPtr.Zero;
                        try
                        {
                            Check(topology.GetConnector(connectorIndex, out connector));
                            var connectedResult = connector.GetDeviceIdConnectedTo(out adapterId);
                            if (connectedResult < 0 || adapterId == IntPtr.Zero)
                            {
                                lastError = request.CantText;
                                continue;
                            }

                            var adapterDeviceId = Marshal.PtrToStringUni(adapterId);
                            if (string.IsNullOrWhiteSpace(adapterDeviceId))
                            {
                                lastError = request.CantText;
                                continue;
                            }

                            Check(enumerator.GetDevice(adapterDeviceId, out adapter));
                            var ksIid = KsControlInterfaceId;
                            Check(adapter.Activate(ref ksIid, ClsctxAll, IntPtr.Zero, out var rawKs));
                            ks = CastComInterface<IKsControl>(rawKs);
                            if (!QueryOneShotSupport(ks, request, out var supportDetail))
                            {
                                lastError = supportDetail;
                                continue;
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            // A supported path has been found. Issue exactly one actual request;
                            // the native return confirms only that the request was accepted.
                            var property = new KsProperty
                            {
                                Set = BluetoothAudioPropertySet,
                                Id = request.PropertyId,
                                Flags = KsPropertyGet,
                            };
                            var requestResult = ks.KsProperty(
                                ref property,
                                Marshal.SizeOf<KsProperty>(),
                                IntPtr.Zero,
                                0,
                                out _);
                            if (requestResult < 0)
                            {
                                var kind = requestResult == unchecked((int)0x80070490)
                                    || requestResult == unchecked((int)0x80070032)
                                    ? ConnectionAttemptKind.Unsupported
                                    : ConnectionAttemptKind.Failed;
                                return new(kind, request.WindowsCouldntText);
                            }

                            return new(ConnectionAttemptKind.Attempted, request.AskingText);
                        }
                        catch (COMException)
                        {
                            lastError = request.CantText;
                        }
                        catch (InvalidCastException)
                        {
                            lastError = request.CantText;
                        }
                        finally
                        {
                            if (adapterId != IntPtr.Zero) Marshal.FreeCoTaskMem(adapterId);
                            Release(ks);
                            Release(adapter);
                            Release(connector);
                        }
                    }
                }
                catch (COMException)
                {
                    lastError = Strings.WinConn_CantReconnect;
                }
                catch (InvalidCastException)
                {
                    lastError = Strings.WinConn_CantReconnect;
                }
                finally
                {
                    Release(topology);
                    Release(endpoint);
                }
            }

            if (!foundEndpoint)
            {
                return new(ConnectionAttemptKind.Unsupported, Strings.WinConn_PairedNotReady);
            }

            return new(ConnectionAttemptKind.Unsupported, lastError);
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
    }
    private ConnectionSnapshot IncompleteWithLastSnapshot(string detail)
        => new(false, _lastSnapshot.Devices, _lastSnapshot.AudioEndpoints, detail);

    private void RaiseChanged()
    {
        EventHandler? handlers;
        lock (_gate)
        {
            if (_disposed) return;
            handlers = Changed;
        }
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception) { /* Native work cannot be unwound by a subscriber. */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingReadResult = null;
            Changed = null;
        }
        // An in-flight native call remains tracked by its worker and is never overlapped. It
        // owns its COM references and releases them when Windows returns.
    }

    private static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        var classId = MmDeviceEnumeratorClassId;
        var interfaceId = MmDeviceEnumeratorInterfaceId;
        Check(CoCreateInstance(ref classId, IntPtr.Zero, ClsctxAll, ref interfaceId, out var enumerator));
        return enumerator;
    }
    private static T CastComInterface<T>(IntPtr value) where T : class => ComInterop.Wrap<T>(value);
    private static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    private static void Release(object? value)
    {
        try { ComInterop.Release(value); }
        catch (InvalidComObjectException) { }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDeviceEnumerator>))] out IMMDeviceEnumerator instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    private sealed class ComApartment : IDisposable
    {
        private readonly bool _shouldUninitialize;

        private ComApartment(bool shouldUninitialize) => _shouldUninitialize = shouldUninitialize;

        public static ComApartment Enter()
        {
            var result = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            if (result == RpcEChangedMode) return new ComApartment(false);
            Check(result);
            return new ComApartment(true);
        }

        public void Dispose()
        {
            if (_shouldUninitialize) CoUninitialize();
        }
    }

    private sealed record PairedBluetoothDevice(string Id, string Name, string ContainerId);

    internal struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KsProperty
    {
        public Guid Set;
        public int Id;
        public int Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDeviceCollection>))] out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [GeneratedComInterface, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    internal partial interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
    }

    [GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(uint access, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IPropertyStore>))] out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [GeneratedComInterface, Guid(DeviceTopologyInterfaceIdText)]
    internal partial interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IConnector>))] out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId(out IntPtr deviceId);
        [PreserveSig] int GetSignalPath(IntPtr from, IntPtr to, int rejectMixedPaths, out IntPtr parts);
    }

    [GeneratedComInterface, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
    internal partial interface IConnector
    {
        [PreserveSig] int GetType(out int connectorType);
        [PreserveSig] int GetDataFlow(out int dataFlow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo([MarshalUsing(typeof(UniqueComInterfaceMarshaller<IConnector>))] out IConnector connector);
        [PreserveSig] int GetConnectorIdConnectedTo(out IntPtr connectorId);
        [PreserveSig] int GetDeviceIdConnectedTo(out IntPtr deviceId);
    }

    [GeneratedComInterface, Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
    internal partial interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, int propertyLength, IntPtr propertyData, int dataLength, out int bytesReturned);
    }
}
