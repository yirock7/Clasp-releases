using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Commands;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.App.Companion;

// P4-B: clasp:// links, the second-instance hand-off, global shortcuts and the command sink
// that does what the flyout buttons do.
internal sealed partial class CompanionHost
{
    private CommandDispatcher? _commandDispatcher;
    private CommandRouter? _router;
    private Hotkeys? _hotkeys;

    private void InitializeCommands() => _commandDispatcher = new CommandDispatcher(new CommandSink(this));

    // ----- Where commands come from -----

    /// <summary>
    /// A command that arrived from outside the app: a clasp:// link, the pipe from a second launch
    /// or a command line. Only runs while "Allow clasp:// links" is on; the setting is checked here
    /// and not only when the pipe was opened. Safe from any thread.
    /// </summary>
    public void RunLinkCommand(ClaspCommand command, string origin)
    {
        if (_disposed) return;
        if (!_settings.AllowClaspLinks)
        {
            LocalLog.Write($"Command '{ClaspCommandText.CanonicalName(command)}' from {origin} ignored: clasp:// links are turned off.");
            return;
        }
        Post(() => _ = RunCommandAsync(command, origin, external: true));
    }

    private bool _confirmationPending;

    /// <summary>
    /// Runs a command. <paramref name="external"/> is true for anything that arrived from outside Clasp
    /// (links, the pipe, the command line): sound changes and Fix connection then need the user's
    /// answer to a prompt first (see <see cref="CommandConfirmation"/>). Clasp's own hotkeys pass false.
    /// </summary>
    private async Task RunCommandAsync(ClaspCommand command, string origin, bool external = false)
    {
        if (_commandDispatcher is null) return;
        try
        {
            if (external && CommandConfirmation.RequiresConfirmation(command))
            {
                if (_confirmationPending)
                {
                    LocalLog.Write($"Command '{ClaspCommandText.CanonicalName(command)}' from {origin} ignored: another question is open.");
                    return;
                }
                _confirmationPending = true;
                bool allowed;
                try { allowed = await LinkConfirmWindow.AskAsync(CommandConfirmation.PromptText(command, ConfirmationOutputName(command)), _theme); }
                finally { _confirmationPending = false; }
                if (_disposed) return;
                if (!allowed)
                {
                    LocalLog.Write($"Command '{ClaspCommandText.CanonicalName(command)}' from {origin}: not allowed by the user.");
                    return;
                }
            }
            // The origin only lives for this dispatch: Connect reads it synchronously when the click event
            // fires. External commands only get here after the user said Allow, so ExternalApproved never
            // comes from an unapproved request.
            _commandOrigin = external ? ConnectionOrigin.ExternalApproved : ConnectionOrigin.Hotkey;
            CommandDispatchResult result;
            try { result = await _commandDispatcher.DispatchAsync(command); }
            finally { _commandOrigin = ConnectionOrigin.None; }
            LocalLog.Write($"Command '{ClaspCommandText.CanonicalName(command)}' from {origin}: {result.Status}. {result.Message}");
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Command '{ClaspCommandText.CanonicalName(command)}' from {origin} failed ({ex.GetType().Name}).");
        }
    }

    private string? ConfirmationOutputName(ClaspCommand command)
    {
        if (_audioVm is null) return null;
        var id = command switch
        {
            ClaspCommand.OutputPrevious => _audioVm.PreviousOutputEndpointId,
            ClaspCommand.OutputAirPods => _observation is { HasUsableEndpoint: true } observation ? observation.AudioEndpointId : null,
            _ => null,
        };
        return id is null ? null : _audioVm.Outputs.FirstOrDefault(output => output.Id == id)?.Label;
    }

    // ----- clasp:// links: registered per user, only while the setting is on -----

    private void ApplyLinksSetting()
    {
        if (_disposed) return;
        if (_settings.AllowClaspLinks)
        {
            var exe = Environment.ProcessPath;
            var registered = !string.IsNullOrEmpty(exe) && (ProtocolRegistration.IsRegistered(exe) || ProtocolRegistration.Register(exe));
            Status.LinksText = registered
                ? "Links are on. Open clasp://connect to try one."
                : "Couldn't turn on links. clasp:// links won't open Clasp.";
            LocalLog.Write(registered ? "clasp: links turned on." : "clasp: links could not be registered.");
            StartRouter();
        }
        else
        {
            StopRouter();
            // Removes only an entry that is ours; nothing is written when none exists.
            if (ProtocolRegistration.IsRegistered())
            {
                var removed = ProtocolRegistration.Unregister();
                LocalLog.Write(removed ? "clasp: links turned off; entry removed." : "clasp: links turned off; entry could not be removed.");
            }
            Status.LinksText = "";
        }
    }

    private void StartRouter()
    {
        if (_router is not null) return;
        try
        {
            // The callback runs on a thread-pool thread: RunLinkCommand marshals to the UI thread.
            _router = CommandRouter.StartServer(command =>
            {
                RunLinkCommand(command, "second launch");
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Command pipe could not start ({ex.GetType().Name}).");
        }
    }

    private void StopRouter()
    {
        var router = _router;
        _router = null;
        router?.Dispose();
    }

    // ----- Global shortcuts: unassigned unless the user sets them; independent of the links setting -----

    private void ApplyHotkeys()
    {
        if (_disposed) return;
        var bindings = new Dictionary<ClaspCommand, HotkeyBinding?>();
        foreach (var command in Enum.GetValues<ClaspCommand>())
            bindings[command] = HotkeyBinding.ParseOrNull(_settings.GetHotkey(command));

        // Nothing assigned and nothing registered yet: do not even create the hidden window.
        if (_hotkeys is null && bindings.Values.All(binding => binding is null))
        {
            Status.HotkeyProblems = new Dictionary<ClaspCommand, string>();
            return;
        }

        try
        {
            if (_hotkeys is null)
            {
                _hotkeys = new Hotkeys();
                _hotkeys.Pressed += (_, command) => _ = RunCommandAsync(command, "shortcut");
            }
            var problems = new Dictionary<ClaspCommand, string>();
            foreach (var result in _hotkeys.Apply(bindings))
                if (!result.Registered && result.Problem is { } problem) problems[result.Command] = problem;
            Status.HotkeyProblems = problems;
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Shortcuts unavailable ({ex.GetType().Name}).");
            Status.HotkeyProblems = Enum.GetValues<ClaspCommand>()
                .Where(command => bindings[command] is not null)
                .ToDictionary(command => command, _ => "Windows wouldn't allow these shortcuts.");
        }
    }

    private void DisposeCommands()
    {
        var hotkeys = _hotkeys;
        _hotkeys = null;
        hotkeys?.Dispose();
        StopRouter();
    }

    // ----- What each command does: the same code the flyout buttons use -----

    private sealed class CommandSink(CompanionHost host) : ICommandSink
    {
        public Task<CommandExecutionResult> ExecuteAsync(ClaspCommand command, CancellationToken cancellationToken = default)
            => host.ExecuteCommandAsync(command);
    }

    private async Task<CommandExecutionResult> ExecuteCommandAsync(ClaspCommand command)
    {
        switch (command)
        {
            case ClaspCommand.ShowFlyout:
                if (_flyout is null || _tray is null) return CommandExecutionResult.Failed("Clasp is still starting.");
                _flyout.Open(_tray.GetIconRect(), NativeMethods.CursorPosition(), AppleDeviceHub.Core.Presentation.FlyoutOpenReason.Programmatic);
                return CommandExecutionResult.Ok("Opened Clasp.");

            case ClaspCommand.ShowSettings:
                _showSettings();
                return CommandExecutionResult.Ok("Opened settings.");

            case ClaspCommand.Connect:
                return await ConnectCommandAsync();

            case ClaspCommand.Disconnect:
                // Clasp's connection controller has no disconnect: it can ask Windows to reconnect
                // and nothing else, and Clasp does not make Bluetooth calls it was not built for.
                // (Any disconnect Clasp does start must first call HandoffDetector.NoteUserDisconnect.)
                return CommandExecutionResult.Failed("Clasp cannot disconnect AirPods yet. Use Windows Bluetooth settings, or put the AirPods in their case.");

            case ClaspCommand.OutputAirPods:
                return UseOutputCommand(
                    _observation is { HasUsableEndpoint: true } observation ? observation.AudioEndpointId : null,
                    "The AirPods are not available as a sound output right now.");

            case ClaspCommand.OutputPrevious:
                return UseOutputCommand(
                    _audioVm?.PreviousOutputEndpointId,
                    "Clasp does not know your previous speakers yet. Turn on \"Restore previous output\" or choose a return output in Audio output settings.");

            case ClaspCommand.FixConnection:
            {
                var message = await RunRepairAsync();
                return Status.RepairSucceeded ? CommandExecutionResult.Ok(message) : CommandExecutionResult.Failed(message);
            }

            case ClaspCommand.PlayPause:
                return await PlayPauseCommandAsync();

            default:
                return CommandExecutionResult.Failed("Unknown command.");
        }
    }

    private async Task<CommandExecutionResult> ConnectCommandAsync()
    {
        if (_connection is null) return CommandExecutionResult.Failed("Paired-device controls are not available in this session.");
        if (!_connection.HasSelectedDevice) return CommandExecutionResult.Failed("Choose your AirPods in Clasp settings first.");
        if (_connection.IsConnected) return CommandExecutionResult.Ok("The AirPods are already connected.");
        if (!_connection.CanConnect) return CommandExecutionResult.Failed("Clasp cannot connect right now. " + _connection.StatusText);
        // The attempt owns this origin from here until it ends; _commandOrigin is only set during a dispatch.
        await _connection.ConnectAsync(_commandOrigin == ConnectionOrigin.None ? ConnectionOrigin.Hotkey : _commandOrigin);
        return _connection.IsConnected
            ? CommandExecutionResult.Ok("Connected.")
            : CommandExecutionResult.Failed(string.IsNullOrWhiteSpace(_connection.DetailText) ? _connection.StatusText : _connection.DetailText);
    }

    private CommandExecutionResult UseOutputCommand(string? endpointId, string unavailableMessage)
    {
        if (_audioVm is null || string.IsNullOrWhiteSpace(endpointId)) return CommandExecutionResult.Failed(unavailableMessage);
        var before = _audioVm.LastManualOutputChoiceAt;
        _audioVm.UseOutput(endpointId); // the same method the Settings buttons call
        if (_audioVm.LastManualOutputChoiceAt != before)
        {
            _ = _connection?.RefreshAsync();
            return CommandExecutionResult.Ok("Sound output changed.");
        }
        return CommandExecutionResult.Failed(string.IsNullOrWhiteSpace(_audioVm.LastActionText) ? unavailableMessage : _audioVm.LastActionText);
    }

    private async Task<CommandExecutionResult> PlayPauseCommandAsync()
    {
        var playback = await GetPlaybackAsync();
        if (playback is null) return CommandExecutionResult.Failed("Windows media controls are not available.");
        var result = playback.Current.Playback == MediaPlayback.Playing
            ? await playback.PauseAsync()
            : await playback.PlayAsync();
        return result.WasSent ? CommandExecutionResult.Ok(result.Message) : CommandExecutionResult.Failed(result.Message);
    }
}
