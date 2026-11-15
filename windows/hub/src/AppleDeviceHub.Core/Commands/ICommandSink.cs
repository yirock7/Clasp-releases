namespace AppleDeviceHub.Core.Commands;

/// <summary>What happened when a command ran.</summary>
public sealed record CommandExecutionResult(bool Succeeded, string Message)
{
    public static CommandExecutionResult Ok(string message = "Done.") => new(true, message);
    public static CommandExecutionResult Failed(string message) => new(false, message);
}

/// <summary>Implemented by the app: performs a command using the same code the flyout buttons use.</summary>
public interface ICommandSink
{
    Task<CommandExecutionResult> ExecuteAsync(ClaspCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Why the dispatcher did or did not run a command.</summary>
public enum CommandDispatchStatus
{
    /// <summary>The sink ran it (it may still report failure).</summary>
    Executed = 0,
    /// <summary>The same command was accepted less than the duplicate window ago.</summary>
    IgnoredDuplicate = 1,
    /// <summary>Another connection or output command is still running.</summary>
    IgnoredBusy = 2,
    /// <summary>The sink threw; the exception is not propagated.</summary>
    Faulted = 3,
}

public sealed record CommandDispatchResult(CommandDispatchStatus Status, CommandExecutionResult? Execution, string Message);

/// <summary>
/// Front door for every outside trigger. Two safety rules: an identical command repeated within
/// <see cref="DuplicateWindow"/> is ignored (a double-fired hotkey or link), and the commands
/// that change connection or audio routing (Connect, Disconnect, the two Output commands and
/// FixConnection) never overlap: while one runs, the others are refused, not queued, so a burst
/// of triggers cannot fight over the Bluetooth link or the default audio device.
/// Thread-safe. Time comes from the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class CommandDispatcher
{
    /// <summary>Identical commands inside this window are dropped.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(1);

    private readonly ICommandSink _sink;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<ClaspCommand, DateTimeOffset> _lastAccepted = new();
    private ClaspCommand? _exclusiveRunning;

    public CommandDispatcher(ICommandSink sink, TimeProvider? time = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True for commands that must not run at the same time as each other.</summary>
    public static bool IsExclusive(ClaspCommand command) => command is
        ClaspCommand.Connect or ClaspCommand.Disconnect or
        ClaspCommand.OutputAirPods or ClaspCommand.OutputPrevious or
        ClaspCommand.FixConnection;

    public async Task<CommandDispatchResult> DispatchAsync(ClaspCommand command, CancellationToken cancellationToken = default)
    {
        var exclusive = IsExclusive(command);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastAccepted.TryGetValue(command, out var last) && now - last < DuplicateWindow)
                return new CommandDispatchResult(CommandDispatchStatus.IgnoredDuplicate, null, "Same command repeated too quickly; ignored.");
            if (exclusive && _exclusiveRunning is { } running)
                return new CommandDispatchResult(CommandDispatchStatus.IgnoredBusy, null, $"Busy with '{ClaspCommandText.CanonicalName(running)}'; ignored.");
            _lastAccepted[command] = now;
            if (exclusive) _exclusiveRunning = command;
        }

        try
        {
            var result = await _sink.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            return new CommandDispatchResult(CommandDispatchStatus.Executed, result, result.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CommandDispatchResult(CommandDispatchStatus.Faulted, null, $"Command failed ({ex.GetType().Name}).");
        }
        finally
        {
            if (exclusive)
                lock (_gate) _exclusiveRunning = null;
        }
    }
}
