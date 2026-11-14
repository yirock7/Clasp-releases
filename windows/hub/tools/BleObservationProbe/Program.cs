// Manual, passive-only BLE observation probe (packets EF-C2, EF-PROBE). See TEST-PROCEDURE.md.
// Without --i-authorize-a-passive-scan it only prints its scope and exits 0: no watcher is
// created and no folder is written. With it, it listens passively (no scan requests) for Apple
// manufacturer data for at most 120 s and saves sightings plus a summary to a per-run folder
// outside the repository. It never pairs, connects, opens GATT or writes to any device.
// Decoded layout = UPSTREAM ASSUMPTION (LibrePods 4589176, linux/ble/blemanager.cpp, GPL-3.0).
//
// Exit codes: 0 = finished (timer) or stopped by user; 1 = unexpected error;
//             2 = refused (bad arguments or unusable results folder; nothing started, nothing written);
//             3 = Bluetooth unavailable (off, missing, disabled, access denied, or lost mid-run).
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AppleDeviceHub.App.Ble;
using AppleDeviceHub.Core.Ble;
using BleObservationProbe.Analysis;

const int ExitOk = 0, ExitError = 1, ExitRefused = 2, ExitRadioUnavailable = 3;

if (!ProbeArguments.IsAuthorized(args))
{
    PrintScope();
    if (ProbeArguments.IsScopeRequest(args)) return ExitOk;
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Refused: arguments were given without the exact flag {ProbeArguments.AuthorizeFlag} (spelled exactly so, lower case).");
    Console.Error.WriteLine("Nothing was started and nothing was written.");
    return ExitRefused;
}

var (options, argError) = ProbeArguments.Parse(args);
if (options is null)
{
    Console.Error.WriteLine($"Refused: {argError}");
    Console.Error.WriteLine("Nothing was started and nothing was written. Run without arguments to see the scope.");
    return ExitRefused;
}
if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
{
    Console.Error.WriteLine("Refused: Windows 10 2004 (19041) or later is required.");
    return ExitRefused;
}

var startedUtc = DateTimeOffset.UtcNow;
var repoRoots = OutputFolder.FindRepositoryRoots(AppContext.BaseDirectory, Environment.CurrentDirectory);
var (outDir, folderError) = OutputFolder.Resolve(options.OutDir, startedUtc, repoRoots);
if (outDir is null)
{
    Console.Error.WriteLine($"Refused: {folderError}");
    Console.Error.WriteLine("Nothing was started and nothing was written.");
    return ExitRefused;
}

StreamWriter jsonl;
try
{
    Directory.CreateDirectory(outDir);
    jsonl = new StreamWriter(Path.Combine(outDir, "sightings.jsonl"), append: false, new UTF8Encoding(false)) { NewLine = "\n" };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
{
    Console.Error.WriteLine($"Refused: the results folder '{outDir}' could not be created ({ex.Message}). Nothing was started.");
    return ExitRefused;
}

var (probeVersion, gitCommit) = ReadVersion();
Console.WriteLine($"Results folder: {outDir}");

var sync = new Lock();
var builder = new RunSummaryBuilder(bucketSeconds: 5);
var tagger = AddressTagger.CreateRandom(); // per-run key, never stored
var policy = new BroadcastAttributionPolicy();
var statusLog = new List<string>();
var clock = Stopwatch.StartNew();
long sequence = 0, writeErrors = 0;
bool closed = false, stopping = false;
string? userStopHow = null;
string? radioLostReason = null;

// Not disposed on purpose: a late signal must never hit a disposed token source.
var userStop = new CancellationTokenSource();
var radioLost = new CancellationTokenSource();
using var finished = new ManualResetEventSlim(false);

void RequestUserStop(string how)
{
    lock (sync) userStopHow ??= how;
    // Cancel off the signal thread so cleanup never runs inside the console control handler.
    ThreadPool.QueueUserWorkItem(_ => userStop.Cancel());
}

var signals = new List<PosixSignalRegistration>
{
    PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; RequestUserStop("Ctrl+C"); }),
    PosixSignalRegistration.Create(PosixSignal.SIGQUIT, ctx => { ctx.Cancel = true; RequestUserStop("Ctrl+Break"); }),
    // Console closed / logoff / shutdown: Windows ends the process after the handler returns,
    // so wait (bounded) for the cleanup below to stop the watcher and write the summary.
    PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; RequestUserStop("console window closed"); finished.Wait(TimeSpan.FromSeconds(4)); }),
    PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; RequestUserStop("logoff or shutdown"); finished.Wait(TimeSpan.FromSeconds(4)); }),
};

RunInfo Info(string ended, string? detail, int? exitCode, DateTimeOffset? stopped)
{
    lock (sync)
    {
        return new RunInfo
        {
            ProbeVersion = probeVersion,
            GitCommit = gitCommit,
            Arguments = args,
            Seconds = options.Seconds,
            OutputFolder = outDir,
            StartedUtc = startedUtc,
            StoppedUtc = stopped,
            Ended = ended,
            EndDetail = detail,
            ExitCode = exitCode,
            StatusLog = statusLog.ToList(),
            WriteErrors = writeErrors,
        };
    }
}

void WriteSummary(RunInfo info)
{
    string json, text;
    lock (sync)
    {
        json = RunSummaryBuilder.ToJsonText(builder.BuildJson(info));
        text = builder.BuildText(info);
    }
    var utf8 = new UTF8Encoding(false);
    File.WriteAllText(Path.Combine(outDir, "summary.json"), json + "\n", utf8);
    File.WriteAllText(Path.Combine(outDir, "summary.txt"), text, utf8);
}

// Placeholder summary first: if the process is killed hard, the folder still says so.
try
{
    WriteSummary(Info("in-progress", "If you can read this, the probe did not finish cleanly.", null, null));
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    jsonl.Dispose();
    Console.Error.WriteLine($"Refused: the results folder '{outDir}' is not writable ({ex.Message}). Nothing was started.");
    return ExitRefused;
}

string ended = "error";
string? endDetail = null;
int exitCode = ExitError;
WinRtAdvertisementSource? source = null;
try
{
    source = new WinRtAdvertisementSource();
    source.StatusChanged += (_, s) =>
    {
        string line = $"{clock.Elapsed.TotalSeconds:0.0}s {s.State}{(s.Reason is null ? "" : ": " + s.Reason)}";
        bool lost;
        lock (sync)
        {
            statusLog.Add(line);
            lost = !stopping && s.State != AdvertisementSourceState.Running;
            if (lost) radioLostReason ??= s.Reason ?? "Windows stopped the listener without giving a reason.";
            Console.WriteLine($"[status] {line}");
        }
        if (lost) radioLost.Cancel();
    };
    source.Received += (_, s) =>
    {
        var data = s.ManufacturerData.ToArray();
        lock (sync)
        {
            if (closed) return;
            var now = DateTimeOffset.UtcNow;
            var obs = SightingObservation.Create(++sequence, now, clock.Elapsed.TotalSeconds, tagger.Tag(s.Address),
                s.Rssi, s.CompanyId, data, namePresent: !string.IsNullOrEmpty(s.LocalName));
            if (obs.Proximity is not null)
                obs = obs with { Attribution = policy.Evaluate(s, obs.Proximity, userChoseToObserve: true, now).Verdict.ToString() };
            builder.Add(obs);
            try
            {
                jsonl.WriteLine(obs.ToJson().ToJsonString());
                jsonl.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                writeErrors++;
            }
            if (obs.Proximity is ProximityMessage m) Console.WriteLine(ConsoleLine(obs, m));
        }
    };

    Console.WriteLine($"Passive listening for {options.Seconds} s (Ctrl+C stops early). Nothing is paired, connected or written to any device.");
    AdvertisementSourceStatus status;
    try
    {
        status = await source.StartAsync(userStop.Token);
    }
    catch (OperationCanceledException) when (userStop.IsCancellationRequested)
    {
        status = AdvertisementSourceStatus.Stopped;
    }

    if (status.State == AdvertisementSourceState.Running)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(userStop.Token, radioLost.Token);
            await Task.Delay(TimeSpan.FromSeconds(options.Seconds), linked.Token);
            (ended, exitCode) = ("timer", ExitOk);
        }
        catch (OperationCanceledException) when (radioLost.IsCancellationRequested && !userStop.IsCancellationRequested)
        {
            lock (sync) endDetail = radioLostReason;
            (ended, exitCode) = ("radio-unavailable", ExitRadioUnavailable);
        }
        catch (OperationCanceledException) when (userStop.IsCancellationRequested)
        {
            (ended, exitCode) = ("stopped-by-user", ExitOk);
        }
    }
    else if (userStop.IsCancellationRequested)
    {
        // Ctrl+C during startup: StartAsync throws or returns Stopped. Either way, a user stop.
        (ended, exitCode) = ("stopped-by-user", ExitOk);
    }
    else
    {
        endDetail = status.Reason ?? $"The listener did not start (state {status.State}).";
        (ended, exitCode) = ("radio-unavailable", ExitRadioUnavailable);
    }
}
catch (Exception ex)
{
    (ended, exitCode, endDetail) = ("error", ExitError, $"{ex.GetType().Name}: {ex.Message}");
}
finally
{
    lock (sync) stopping = true;
    if (source is not null)
    {
        try { await source.StopAsync(); }
        catch (Exception ex) { Console.Error.WriteLine($"StopAsync failed ({ex.GetType().Name}); disposing anyway."); }
        source.Dispose();
    }
    lock (sync)
    {
        closed = true;
        try { jsonl.Flush(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { writeErrors++; }
        // A failing final flush on Dispose must not lose the summary written below (EF-IR review F8).
        try { jsonl.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { writeErrors++; }
    }
}

if (ended == "stopped-by-user")
{
    string how;
    lock (sync) how = userStopHow ?? "Ctrl+C";
    endDetail = $"stopped by user ({how})";
}

try
{
    WriteSummary(Info(ended, endDetail, exitCode, DateTimeOffset.UtcNow));
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"The summary could not be written ({ex.Message}).");
    if (exitCode == ExitOk) exitCode = ExitError;
}

Console.WriteLine();
switch (ended)
{
    case "timer": Console.WriteLine($"Finished after {options.Seconds} s."); break;
    case "stopped-by-user": Console.WriteLine($"Stopped by user. {endDetail}"); break;
    case "radio-unavailable": Console.WriteLine($"Bluetooth unavailable: {endDetail}"); break;
    default: Console.WriteLine($"Error: {endDetail}"); break;
}
lock (sync)
{
    Console.WriteLine($"Apple sightings: {builder.Total}; proximity decoded: {builder.ProximityCount}; distinct address tags: {builder.DistinctTags}.");
}
Console.WriteLine("Reminder: every sighting is UNCONFIRMED (any nearby Apple device can appear). It does not prove that any control works.");
Console.WriteLine($"Results folder: {outDir}");
Console.WriteLine($"Exit code: {exitCode}");

finished.Set();
foreach (var r in signals) r.Dispose();
return exitCode;

static string ConsoleLine(SightingObservation o, ProximityMessage m)
{
    string model = m.ModelId is ushort id ? $"0x{id:X4} {AppleModelCatalog.DisplayNameOf(id)}" : "?";
    string pods = m.PodBatteryByte is byte p ? $"0x{p:X2}" : "?";
    return $"{o.SecondsSinceStart,6:0.0}s {o.AddressTag} rssi={o.Rssi?.ToString(CultureInfo.InvariantCulture) ?? "?"} model={model} " +
           $"status={(m.Status is byte s ? $"0x{s:X2}" : "?")} pods={pods} L={P(m.LeftBatteryPercent)} R={P(m.RightBatteryPercent)} " +
           $"case={P(m.CaseBatteryPercent)} inEar(L/R)={B(m.LeftInEar)}/{B(m.RightInEar)} " +
           $"inCase(this/one/both)={B(m.ThisPodInCase)}/{B(m.OnePodInCase)}/{B(m.BothPodsInCase)} " +
           $"lid(counter={m.LidCounter?.ToString(CultureInfo.InvariantCulture) ?? "?"},closed={B(m.LidClosed)}) " +
           $"attribution={o.Attribution} identity=unconfirmed";
}

static string B(bool? v) => v switch { true => "1", false => "0", null => "?" };

static string P(int? v) => v is int x ? x.ToString(CultureInfo.InvariantCulture) + "%" : "?";

static (string Version, string? Commit) ReadVersion()
{
    // The .NET SDK appends "+<commit sha>" to the informational version when built inside git.
    string info = typeof(ProbeArguments).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    int plus = info.IndexOf('+', StringComparison.Ordinal);
    return plus < 0 ? (info, null) : (info[..plus], info[(plus + 1)..]);
}

static void PrintScope()
{
    Console.WriteLine($"""
        BleObservationProbe: passive Bluetooth LE observation probe (packets EF-C2, EF-PROBE).

        Without {ProbeArguments.AuthorizeFlag} this program only prints this text and exits 0.
        It has not created a Bluetooth watcher and has not created or written any folder or file.

        What a live run listens to
          - PASSIVE listening only (no scan requests) for Bluetooth LE advertisements carrying Apple
            manufacturer data (company 0x004C), for at most {ProbeArguments.MaxSeconds} seconds.
          - Proximity-pairing messages (type 0x07) are decoded with the UPSTREAM-ASSUMED layout from
            LibrePods 4589176 (linux/ble/blemanager.cpp, GPL-3.0). Nothing is verified for Pro 3.
          - Every other Apple message type is counted by type, not decoded.

        What it records, and where
          - A new folder per run: {OutputFolder.DefaultRoot}\<UTC yyyyMMdd-HHmmss>\
            (or --out <dir>; must be new or empty and outside the repository).
          - sightings.jsonl: one line per Apple sighting (UTC time, seconds since start, address tag,
            RSSI, company id, raw manufacturer-data hex, decoded fields or a parse-failure reason).
          - summary.json and summary.txt: run parameters, how the run ended, per-address-tag counts,
            per-model counts, distinct tags over time, status and battery histograms, other message types.
          - BLE addresses are stored only as a per-run keyed hash (addr#xxxxxxxx); device names are
            never stored (only whether a name was present). The raw payload hex IS stored: it contains
            an encrypted tail, so do not publish the results folder.

        What it never does
          - Never pairs, connects, opens GATT services, or writes anything to any device.
          - Never changes Bluetooth, pairing or audio settings.

        Identity is always UNCONFIRMED: any nearby Apple device can appear, and no broadcast can be tied
        to the owner's AirPods without the AAP session keys. Broadcast observation is NOT proof that any
        control (listening mode, Conversation Awareness, strength) works.

        Usage
          BleObservationProbe.exe {ProbeArguments.AuthorizeFlag} --seconds N [--out <dir>]
            (1 <= N <= {ProbeArguments.MaxSeconds}; Ctrl+C stops early and still saves the results)

        Exit codes: 0 finished or stopped by user; 1 unexpected error; 2 refused (bad arguments or
        results folder; nothing started); 3 Bluetooth unavailable (off, missing, access denied, lost).
        See TEST-PROCEDURE.md before running a live scan.
        """);
}
