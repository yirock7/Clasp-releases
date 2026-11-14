using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GattDiscoveryProbe.Analysis;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    ProbeArgumentResult parsed = ProbeArguments.Parse(args);
    switch (parsed.Command)
    {
        case ProbeCommandKind.Scope:
            PrintScope();
            return (int)ProbeExitCode.Success;

        case ProbeCommandKind.ListPaired:
            return await ListPairedAsync();

        case ProbeCommandKind.Refused:
            Console.Error.WriteLine($"Refused: {parsed.Error}");
            Console.Error.WriteLine($"Run with no arguments for scope, or use {ProbeArguments.ListPairedFlag} to list paired LE identities.");
            return (int)ProbeExitCode.Refused;

        case ProbeCommandKind.Live when parsed.Options is not null:
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                Console.Error.WriteLine("Refused: Windows 10 build 19041 or later is required.");
                return (int)ProbeExitCode.Refused;
            }
            return await RunLiveAsync(parsed.Options);

        default:
            Console.Error.WriteLine("Refused: the command line could not be interpreted.");
            return (int)ProbeExitCode.Refused;
    }
}

static void PrintScope()
{
    Console.WriteLine($"""
        GattDiscoveryProbe: paired-device-bound Bluetooth LE GATT metadata discovery.

        With no arguments this prints scope only and exits 0. It does not enumerate devices,
        connect, or create an output folder.

        --list-paired lists only Windows' paired Bluetooth LE device records as a per-run name
        hash and Windows device id. It does not resolve a BluetoothLEDevice or connect.

        A live run requires the exact flag {ProbeArguments.AuthorizeFlag} and a paired id:
          GattDiscoveryProbe.exe {ProbeArguments.AuthorizeFlag} --device-id <id> [--out <dir>]

        A live run queries GATT service metadata once from Cached and once from Uncached, then
        queries characteristics for each returned service in the corresponding mode. It records
        UUIDs, attribute handles, properties, protection level, status, and protocol error.
        Device name and id are written only as per-run keyed hashes.

        Results go under {OutputFolder.DefaultRoot}\<UTC yyyyMMdd-HHmmss> by default. --out may
        choose another folder, but it must be outside every Git repository and new or empty.

        Every asynchronous Windows call has a 10 second caller wait bound. After a timeout or
        Ctrl+C, the probe stopped waiting; Windows activity may continue. Late results are disposed
        and late failures are recorded. On exit the probe waits up to 5 seconds, then reports any
        operation still outstanding. The tool does not retry or change pairing, and records metadata only.

        Service categories: USEFUL_STANDARD includes Battery (180F), Device Information (180A),
        Heart Rate (180D), and the functional SIG services listed in the README; PLUMBING_ONLY means
        every returned service is GAP (1800) or GATT (1801); VENDOR_DISCOVERY means a non-SIG-base
        128-bit UUID and is not proof of an Apple control capability. Mixed useful/vendor findings
        list both; vendor takes exit-code precedence.

        Exit codes: 0 scope/list; 1 unexpected error; 2 refused; 10 VENDOR_DISCOVERY; 11
        USEFUL_STANDARD; 12 access denied; 13 unreachable/timeout/incomplete; 14 no paired LE
        identity; 15 PLUMBING_ONLY; 16 other SIG service UUIDs not in the useful list.
        """);
}

static async Task<int> ListPairedAsync()
{
    using var hasher = DeviceIdentityHasher.CreateRandom();
    using var userCancellation = new CancellationTokenSource();
    var lifecycle = new BoundedOperationTracker(WriteLateCompletion);
    BoundedOperationDrain lifecycleDrain = new([], []);
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        try { userCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
        var call = await RunBoundedAsync(
            "DeviceInformation.FindAllAsync",
            () => DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)),
            userCancellation.Token,
            lifecycle);

        if (call.OperationStatus != "Completed" || call.Value is null)
        {
            Console.Error.WriteLine(call.TimedOut || call.Cancelled
                ? "Probe stopped waiting; Windows activity may continue while the paired-device list finishes."
                : $"Paired-device listing failed ({call.ExceptionType ?? call.OperationStatus}).");
            if (call.AccessDenied) return (int)ProbeExitCode.AccessDenied;
            return call.TimedOut || call.Cancelled ? (int)ProbeExitCode.UnreachableOrTimeout : (int)ProbeExitCode.UnexpectedError;
        }

        DeviceInformationCollection devices = call.Value;
        foreach (DeviceInformation deviceInfo in devices)
        {
            if (userCancellation.IsCancellationRequested) break;
            // The display name is never printed; the id is printed only because it is the explicit
            // operator-selected input accepted by the live mode. Listing writes no file.
            Console.WriteLine($"nameHash={hasher.HashName(deviceInfo.Name)} id={deviceInfo.Id}");
        }
        Console.WriteLine($"Paired Bluetooth LE device count: {devices.Count}");
        return userCancellation.IsCancellationRequested
            ? (int)ProbeExitCode.UnreachableOrTimeout
            : (int)ProbeExitCode.Success;
    }
    finally
    {
        lifecycleDrain = await lifecycle.WaitForCompletionGraceAsync(TimeSpan.FromSeconds(5));
        PrintLifecycleDrain(lifecycleDrain);
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> RunLiveAsync(ProbeOptions options)
{
    DateTimeOffset started = DateTimeOffset.UtcNow;
    IReadOnlyList<string> repositoryRoots = OutputFolder.FindRepositoryRoots(AppContext.BaseDirectory, Environment.CurrentDirectory);
    var (outDir, folderError) = OutputFolder.Resolve(options.OutDir, started, repositoryRoots);
    if (outDir is null)
    {
        Console.Error.WriteLine($"Refused: {folderError}");
        return (int)ProbeExitCode.Refused;
    }

    try
    {
        Directory.CreateDirectory(outDir);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        Console.Error.WriteLine($"Refused: the result folder could not be created ({ex.GetType().Name}).");
        return (int)ProbeExitCode.Refused;
    }

    using var hasher = DeviceIdentityHasher.CreateRandom();
    using var userCancellation = new CancellationTokenSource();
    var lifecycle = new BoundedOperationTracker(WriteLateCompletion);
    BoundedOperationDrain lifecycleDrain = new([], []);
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        try { userCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    };
    Console.CancelKeyPress += cancelHandler;

    var result = new ProbeRunResult
    {
        StartedUtc = started,
        WindowsVersion = Environment.OSVersion.Version.ToString(),
        WindowsBuild = Environment.OSVersion.Version.Build,
        ToolVersion = GetToolVersion(),
        AssemblySha256 = SourceHashCatalog.ComputeAssemblySha256(),
        SourceSha256s = SourceHashCatalog.Compute().ToList(),
        DeviceIdHash = hasher.HashDeviceId(options.DeviceId),
    };

    BluetoothLEDevice? device = null;
    try
    {
        Console.WriteLine($"Results folder: {outDir}");
        string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var enumerationCall = new ProbeCall { Operation = "DeviceInformation.FindAllAsync", CacheMode = null };
        result.Calls.Add(enumerationCall);
        var paired = await RunBoundedAsync(
            "DeviceInformation.FindAllAsync",
            () => DeviceInformation.FindAllAsync(selector),
            userCancellation.Token,
            lifecycle);
        ApplyOperationResult(enumerationCall, paired);

        if (paired.OperationStatus == "Completed" && paired.Value is not null)
        {
            result.IdentityEnumerationCompleted = true;
            DeviceInformation? selected = paired.Value.FirstOrDefault(info =>
                string.Equals(info.Id, options.DeviceId, StringComparison.Ordinal));

            if (selected is null)
            {
                result.Detail = "The requested id was not present in Windows' paired Bluetooth LE list.";
            }
            else
            {
                result.DeviceNameHash = hasher.HashName(selected.Name);
                result.DeviceIdHash = hasher.HashDeviceId(selected.Id);

                if (!userCancellation.IsCancellationRequested)
                {
                    var resolveCall = new ProbeCall { Operation = "BluetoothLEDevice.FromIdAsync", CacheMode = null };
                    result.Calls.Add(resolveCall);
                    var resolved = await RunBoundedAsync(
                        "BluetoothLEDevice.FromIdAsync",
                        () => BluetoothLEDevice.FromIdAsync(selected.Id),
                        userCancellation.Token,
                        lifecycle);
                    ApplyOperationResult(resolveCall, resolved);
                    device = resolved.Value;

                    if (resolved.OperationStatus == "Completed" && device is not null)
                    {
                        result.PairedLeIdentityResolved = true;

                        // Each mode is issued exactly once, in the requested order. Each service
                        // returned by that mode gets one matching-mode characteristic query.
                        await CollectModeSafelyAsync(device, BluetoothCacheMode.Cached, result, userCancellation.Token, lifecycle);
                        if (!userCancellation.IsCancellationRequested)
                            await CollectModeSafelyAsync(device, BluetoothCacheMode.Uncached, result, userCancellation.Token, lifecycle);
                    }
                    else if (resolved.TimedOut)
                    {
                        result.Detail = "Windows did not resolve the paired LE identity before the timeout.";
                    }
                    else if (resolved.Value is null && resolved.OperationStatus == "Completed")
                    {
                        result.Detail = "Windows returned no LE device for the selected paired id.";
                    }
                    else
                    {
                        result.Detail = "Windows could not resolve the selected paired LE identity.";
                    }
                }
            }
        }
        else if (paired.TimedOut)
        {
            result.Detail = "Windows' paired LE list did not return before the timeout.";
        }
        else
        {
            result.Detail = "Windows' paired LE list could not be read.";
        }

        if (userCancellation.IsCancellationRequested)
            result.Detail ??= "Probe stopped waiting; Windows activity may continue.";
    }
    catch (Exception ex)
    {
        result.Calls.Add(new ProbeCall
        {
            Operation = "probe-runtime",
            OperationStatus = "Failed",
            ExceptionType = ex.GetType().Name,
        });
        result.Detail = $"Unexpected error ({ex.GetType().Name}).";
    }
    finally
    {
        lifecycleDrain = await lifecycle.WaitForCompletionGraceAsync(TimeSpan.FromSeconds(5));
        result.LateCompletions = lifecycleDrain.LateCompletions.ToList();
        result.OutstandingOperationsAtExit = lifecycleDrain.OutstandingOperations.ToList();
        PrintLifecycleDrain(lifecycleDrain);
        if (result.Calls.Any(call => call.TimedOut || call.CallerCancelledWait) && !(result.Detail?.Contains("Probe stopped waiting; Windows activity may continue.", StringComparison.Ordinal) ?? false))
            result.Detail = AppendDetail(result.Detail, "Probe stopped waiting; Windows activity may continue.");
        if (result.LateCompletions.Count > 0)
            result.Detail = AppendDetail(result.Detail, $"Observed {result.LateCompletions.Count} late completion(s) before exit.");
        if (result.OutstandingOperationsAtExit.Count > 0)
            result.Detail = AppendDetail(result.Detail, $"Still outstanding at exit: {string.Join(", ", result.OutstandingOperationsAtExit)}.");
        if (device is not null)
        {
            try { device.Dispose(); }
            catch (Exception ex)
            {
                result.Calls.Add(new ProbeCall
                {
                    Operation = "BluetoothLEDevice.Dispose",
                    OperationStatus = "Failed",
                    ExceptionType = ex.GetType().Name,
                });
            }
        }
        Console.CancelKeyPress -= cancelHandler;
    }

    ProbeAssessment assessment = OutcomeClassifier.Assess(result);
    result.ServiceCategories = assessment.ServiceCategories.Select(category => category.ToString()).ToList();
    result.UnclassifiedSigServiceUuids = assessment.UnclassifiedSigServiceUuids.ToList();
    ProbeExitCode outcome = assessment.ExitCode;
    result.ExitCode = (int)outcome;
    result.Outcome = OutcomeText(outcome);
    result.FinishedUtc = DateTimeOffset.UtcNow;

    try
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(result, jsonOptions);
        File.WriteAllText(Path.Combine(outDir, "result.json"), json + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), BuildSummary(result, outDir), new UTF8Encoding(false));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        Console.Error.WriteLine($"The result files could not be written ({ex.GetType().Name}).");
        return (int)ProbeExitCode.UnexpectedError;
    }

    Console.WriteLine($"Outcome: {result.Outcome}");
    Console.WriteLine($"Exit code: {result.ExitCode}");
    Console.WriteLine($"Results folder: {outDir}");
    if (result.Detail is not null)
        Console.WriteLine(result.Detail);
    return result.ExitCode;
}

static async Task CollectModeSafelyAsync(
    BluetoothLEDevice device,
    BluetoothCacheMode mode,
    ProbeRunResult run,
    CancellationToken cancellationToken,
    BoundedOperationTracker lifecycle)
{
    try
    {
        await CollectModeAsync(device, mode, run, cancellationToken, lifecycle);
    }
    catch (Exception ex)
    {
        run.Calls.Add(new ProbeCall
        {
            Operation = "service-metadata-processing",
            CacheMode = mode.ToString(),
            OperationStatus = "Failed",
            ExceptionType = ex.GetType().Name,
        });
    }
}

static async Task CollectModeAsync(
    BluetoothLEDevice device,
    BluetoothCacheMode mode,
    ProbeRunResult run,
    CancellationToken cancellationToken,
    BoundedOperationTracker lifecycle)
{
    if (cancellationToken.IsCancellationRequested) return;

    string cacheMode = mode.ToString();
    var servicesCall = new ProbeCall { Operation = "GetGattServicesAsync", CacheMode = cacheMode };
    run.Calls.Add(servicesCall);
    var fetched = await RunBoundedAsync(
        $"GetGattServicesAsync({cacheMode})",
        () => device.GetGattServicesAsync(mode),
        cancellationToken,
        lifecycle,
        DisposeLateServicesResult);
    ApplyOperationResult(servicesCall, fetched);

    GattDeviceServicesResult? serviceResult = fetched.Value;
    if (fetched.OperationStatus != "Completed" || serviceResult is null) return;

    try
    {
        servicesCall.CommunicationStatus = serviceResult.Status.ToString();
        servicesCall.ProtocolError = serviceResult.ProtocolError;
        if (serviceResult.Status != GattCommunicationStatus.Success)
            return;

        foreach (GattDeviceService service in serviceResult.Services)
        {
            var metadata = new ServiceMetadata
            {
                CacheMode = cacheMode,
                Uuid = service.Uuid.ToString("D").ToUpperInvariant(),
                AttributeHandle = service.AttributeHandle,
            };
            run.Services.Add(metadata);

            if (cancellationToken.IsCancellationRequested) break;

            var characteristicsCall = new ProbeCall
            {
                Operation = "GetCharacteristicsAsync",
                CacheMode = cacheMode,
                ServiceUuid = metadata.Uuid,
            };
            run.Calls.Add(characteristicsCall);
            var characteristics = await RunBoundedAsync(
                $"GetCharacteristicsAsync({cacheMode}, {metadata.Uuid})",
                () => service.GetCharacteristicsAsync(mode),
                cancellationToken,
                lifecycle,
                DisposeLateCharacteristicsResult);
            ApplyOperationResult(characteristicsCall, characteristics);

            GattCharacteristicsResult? characteristicsResult = characteristics.Value;
            if (characteristics.OperationStatus != "Completed" || characteristicsResult is null) continue;

            try
            {
                characteristicsCall.CommunicationStatus = characteristicsResult.Status.ToString();
                characteristicsCall.ProtocolError = characteristicsResult.ProtocolError;
                if (characteristicsResult.Status != GattCommunicationStatus.Success) continue;

                foreach (GattCharacteristic characteristic in characteristicsResult.Characteristics)
                {
                    GattCharacteristicProperties properties = characteristic.CharacteristicProperties;
                    metadata.Characteristics.Add(new CharacteristicMetadata(
                        characteristic.Uuid.ToString("D").ToUpperInvariant(),
                        characteristic.AttributeHandle,
                        properties.ToString(),
                        (uint)properties,
                        characteristic.ProtectionLevel.ToString()));
                }
            }
            finally
            {
                _ = DisposeLateCharacteristicsResult(characteristicsResult);
            }
        }
    }
    finally
    {
        _ = DisposeLateServicesResult(serviceResult);
    }
}

static async Task<AsyncCallResult<T>> RunBoundedAsync<T>(
    string operationName,
    Func<IAsyncOperation<T>> start,
    CancellationToken userCancellationToken,
    BoundedOperationTracker lifecycle,
    Func<T, string?>? lateDisposer = null)
{
    var stopwatch = Stopwatch.StartNew();
    if (userCancellationToken.IsCancellationRequested)
        return new AsyncCallResult<T>(default, "StoppedWaiting", false, true, false, null, 0);

    try
    {
        IAsyncOperation<T> operation = start();
        var tracked = lifecycle.Start(operationName, AwaitWinRtAsync(operation), lateDisposer);
        BoundedOperationResult<T> waited = await tracked.WaitAsync(TimeSpan.FromSeconds(10), userCancellationToken);
        stopwatch.Stop();

        if (waited.OperationStatus == "Completed")
            return new AsyncCallResult<T>(waited.Value, "Completed", false, false, false, null, stopwatch.ElapsedMilliseconds);

        if (waited.OperationStatus == "StoppedWaiting")
        {
            return new AsyncCallResult<T>(
                default,
                waited.OperationStatus,
                waited.TimedOut,
                waited.Cancelled,
                false,
                null,
                stopwatch.ElapsedMilliseconds);
        }

        Exception failure = waited.Exception ?? new InvalidOperationException("The operation failed without an exception detail.");
        return new AsyncCallResult<T>(
            default,
            "Failed",
            false,
            false,
            IsAccessDeniedException(failure),
            failure.GetType().Name,
            stopwatch.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        stopwatch.Stop();
        return new AsyncCallResult<T>(
            default,
            "Failed",
            false,
            false,
            IsAccessDeniedException(ex),
            ex.GetType().Name,
            stopwatch.ElapsedMilliseconds);
    }
}
static bool IsAccessDeniedException(Exception exception)
    => exception is UnauthorizedAccessException
        || exception.HResult == unchecked((int)0x80070005);

static async Task<T> AwaitWinRtAsync<T>(IAsyncOperation<T> operation)
    => await operation;

// GattDeviceServicesResult itself is not IDisposable; each GattDeviceService in it is.
static string? DisposeLateServicesResult(GattDeviceServicesResult result)
    => BoundedOperationTracker.DisposeDisposableGraph(result.Services);

// Neither GattCharacteristicsResult nor GattCharacteristic is IDisposable: nothing to release.
static string? DisposeLateCharacteristicsResult(GattCharacteristicsResult result)
    => null;

static string? DisposeSafely(IDisposable disposable)
{
    try
    {
        disposable.Dispose();
        return null;
    }
    catch (Exception ex)
    {
        return ex.GetType().Name;
    }
}

static string? CombineCleanupErrors(params string?[] errors)
{
    string[] present = errors.Where(error => !string.IsNullOrWhiteSpace(error)).Select(error => error!).ToArray();
    return present.Length == 0 ? null : string.Join("; ", present);
}

static void WriteLateCompletion(LateCompletionRecord late)
{
    string error = late.ExceptionType is null ? string.Empty : $" error={late.ExceptionType}";
    string cleanup = late.CleanupError is null ? string.Empty : $" cleanupError={late.CleanupError}";
    Console.WriteLine($"Late completion recorded: {late.Operation} status={late.OperationStatus}; completedUtc={late.CompletedUtc:O}{error}; resultDisposed={late.LateResultDisposed}{cleanup}");
}

static void PrintLifecycleDrain(BoundedOperationDrain drain)
{
    foreach (string operation in drain.OutstandingOperations)
        Console.WriteLine($"Still outstanding after the 5-second grace period: {operation}");
}

static string? AppendDetail(string? current, string next)
    => string.IsNullOrWhiteSpace(current) ? next : $"{current} {next}";

static void ApplyOperationResult<T>(ProbeCall call, AsyncCallResult<T> result)
{
    call.OperationStatus = result.OperationStatus;
    call.TimedOut = result.TimedOut;
    call.CallerCancelledWait = result.Cancelled;
    call.AccessDenied = result.AccessDenied;
    call.ExceptionType = result.ExceptionType;
    call.ElapsedMilliseconds = result.ElapsedMilliseconds;
}

static string BuildSummary(ProbeRunResult result, string outDir)
{
    var lines = new List<string>
    {
        "GattDiscoveryProbe result",
        $"Started UTC: {result.StartedUtc:O}",
        $"Finished UTC: {result.FinishedUtc:O}",
        $"Windows version: {result.WindowsVersion}",
        $"Windows build: {result.WindowsBuild}",
        $"Tool version: {result.ToolVersion}",
        $"Assembly SHA-256: {result.AssemblySha256 ?? "unavailable"}",
        $"Outcome: {result.Outcome}",
        $"Service categories: {(result.ServiceCategories.Count == 0 ? "none" : string.Join(", ", result.ServiceCategories))}",
        $"Unclassified SIG service UUIDs: {(result.UnclassifiedSigServiceUuids.Count == 0 ? "none" : string.Join(", ", result.UnclassifiedSigServiceUuids))}",
        $"Exit code: {result.ExitCode}",
        $"Output folder: {outDir}",
        $"Device name hash: {result.DeviceNameHash ?? "unavailable"}",
        $"Device id hash: {result.DeviceIdHash ?? "unavailable"}",
        $"Paired LE identity resolved: {result.PairedLeIdentityResolved}",
        "",
        "Source SHA-256s:",
    };

    foreach (SourceHash source in result.SourceSha256s)
        lines.Add($"  {source.File}: {source.Sha256}");

    lines.Add("");
    lines.Add("Asynchronous calls:");
    foreach (ProbeCall call in result.Calls)
    {
        string mode = call.CacheMode ?? "n/a";
        string service = call.ServiceUuid is null ? string.Empty : $" service={call.ServiceUuid}";
        string communication = call.CommunicationStatus ?? "n/a";
        string protocol = call.ProtocolError?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        string elapsed = call.ElapsedMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        lines.Add($"  {call.Operation} cache={mode}{service}: operation={call.OperationStatus}; communication={communication}; protocolError={protocol}; timeout={call.TimedOut}; callerCancelledWait={call.CallerCancelledWait}; elapsedMs={elapsed}; error={call.ExceptionType ?? "none"}");
    }

    lines.Add("");
    lines.Add("Late completions:");
    if (result.LateCompletions.Count == 0)
        lines.Add("  (none observed before exit)");
    else
    {
        foreach (LateCompletionRecord late in result.LateCompletions)
            lines.Add($"  {late.Operation}: status={late.OperationStatus}; completedUtc={late.CompletedUtc:O}; exception={late.ExceptionType ?? "none"}; resultDisposed={late.LateResultDisposed}; cleanupError={late.CleanupError ?? "none"}");
    }

    lines.Add($"Outstanding operations at exit: {(result.OutstandingOperationsAtExit.Count == 0 ? "none" : string.Join(", ", result.OutstandingOperationsAtExit))}");
    lines.Add("");
    lines.Add("Services and characteristics:");
    if (result.Services.Count == 0)
    {
        lines.Add("  (none returned)");
    }
    else
    {
        foreach (ServiceMetadata service in result.Services)
        {
            lines.Add($"  [{service.CacheMode}] service {service.Uuid} handle=0x{service.AttributeHandle:X4}");
            foreach (CharacteristicMetadata characteristic in service.Characteristics)
            {
                lines.Add($"    characteristic {characteristic.Uuid} handle=0x{characteristic.AttributeHandle:X4}; properties={characteristic.Properties} (0x{characteristic.PropertiesValue:X}); protection={characteristic.ProtectionLevel}");
            }
        }
    }

    if (result.Detail is not null)
    {
        lines.Add("");
        lines.Add($"Detail: {result.Detail}");
    }

    return string.Join(Environment.NewLine, lines) + Environment.NewLine;
}

static string GetToolVersion()
{
    string version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    int plus = version.IndexOf('+', StringComparison.Ordinal);
    return plus < 0 ? version : version[..plus];
}

static string OutcomeText(ProbeExitCode outcome) => outcome switch
{
    ProbeExitCode.Success => "scope or paired-device list",
    ProbeExitCode.UnexpectedError => "unexpected error",
    ProbeExitCode.Refused => "refused",
    ProbeExitCode.VendorDiscovery => "non-SIG 128-bit service discovery; interpretation required, not proof of an Apple control capability",
    ProbeExitCode.UsefulStandard => "useful functional Bluetooth SIG service exposed",
    ProbeExitCode.PlumbingOnly => "only GAP and/or GATT plumbing services exposed",
    ProbeExitCode.OtherSigServices => "SIG service exposed outside the listed useful-service set",
    ProbeExitCode.AccessDenied => "access denied",
    ProbeExitCode.UnreachableOrTimeout => "unreachable, timeout, interrupted, or incomplete discovery",
    ProbeExitCode.NoPairedLeIdentity => "no paired LE identity",
    _ => "unexpected error",
};

readonly record struct AsyncCallResult<T>(
    T? Value,
    string OperationStatus,
    bool TimedOut,
    bool Cancelled,
    bool AccessDenied,
    string? ExceptionType,
    long ElapsedMilliseconds);
