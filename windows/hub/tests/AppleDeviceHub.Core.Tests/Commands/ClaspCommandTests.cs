using AppleDeviceHub.Core.Commands;

namespace AppleDeviceHub.Core.Tests.Commands;

public class ClaspCommandParserTests
{
    [Theory]
    [InlineData("clasp://connect", ClaspCommand.Connect)]
    [InlineData("clasp://disconnect", ClaspCommand.Disconnect)]
    [InlineData("clasp://output/airpods", ClaspCommand.OutputAirPods)]
    [InlineData("clasp://output/previous", ClaspCommand.OutputPrevious)]
    [InlineData("clasp://fix", ClaspCommand.FixConnection)]
    [InlineData("clasp://show", ClaspCommand.ShowFlyout)]
    [InlineData("clasp://settings", ClaspCommand.ShowSettings)]
    [InlineData("clasp://playpause", ClaspCommand.PlayPause)]
    [InlineData("CLASP://Output/AirPods", ClaspCommand.OutputAirPods)]
    [InlineData("clasp://connect/", ClaspCommand.Connect)]
    [InlineData("clasp://output/airpods/", ClaspCommand.OutputAirPods)]
    [InlineData("clasp://fix?source=streamdeck&x=1", ClaspCommand.FixConnection)]
    [InlineData("clasp://fix/?a=b#frag", ClaspCommand.FixConnection)]
    [InlineData("  clasp://connect  ", ClaspCommand.Connect)]
    public void Accepts_known_links(string uri, ClaspCommand expected)
    {
        var result = ClaspCommandParser.ParseUri(uri);
        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("clasp:connect")]
    [InlineData("clasp:/connect")]
    [InlineData("http://connect")]
    [InlineData("clasp://")]
    [InlineData("clasp://unknown")]
    [InlineData("clasp://output")]
    [InlineData("clasp://output/other")]
    [InlineData("clasp://output/airpods/extra")]
    [InlineData("clasp://connect/extra")]
    [InlineData("clasp://evil.com/connect")]
    [InlineData("clasp://user@connect")]
    [InlineData("clasp://conn%65ct")]
    [InlineData("clasp://connect\" --transport=hardware")]
    [InlineData("clasp://connect --transport=hardware")]
    [InlineData("clasp://connect&x=1")]
    [InlineData("clasp://con\r\nnect")]
    [InlineData("clasp://con\\nect")]
    public void Rejects_everything_else_with_a_reason(string? uri)
    {
        var result = ClaspCommandParser.ParseUri(uri);
        Assert.False(result.Succeeded);
        Assert.Null(result.Command);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void Rejects_overlong_input_even_when_it_starts_correctly()
    {
        var uri = "clasp://connect?" + new string('a', ClaspCommandParser.MaxInputLength);
        Assert.False(ClaspCommandParser.ParseUri(uri).Succeeded);
        Assert.False(ClaspCommandParser.ParseName(new string('a', ClaspCommandParser.MaxInputLength + 1)).Succeeded);
    }

    [Fact]
    public void Every_command_round_trips_through_its_name_and_its_link()
    {
        foreach (var command in Enum.GetValues<ClaspCommand>())
        {
            Assert.Equal(command, ClaspCommandParser.ParseName(ClaspCommandText.CanonicalName(command)).Command);
            Assert.Equal(command, ClaspCommandParser.ParseUri(ClaspCommandText.Link(command)).Command);
            Assert.False(string.IsNullOrWhiteSpace(ClaspCommandText.DisplayName(command)));
        }
    }

    [Theory]
    [InlineData("--command=fix", ClaspCommand.FixConnection)]
    [InlineData("--COMMAND=Output-AirPods", ClaspCommand.OutputAirPods)]
    [InlineData("--uri=clasp://playpause", ClaspCommand.PlayPause)]
    public void Reads_command_and_uri_arguments(string arg, ClaspCommand expected)
    {
        var result = ClaspCommandParser.ParseArgs(["--transport=demo", arg]);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Command);
        Assert.True(ClaspCommandParser.IsCommandLaunch([arg]));
    }

    [Fact]
    public void Arguments_without_a_command_are_not_a_command_launch()
    {
        Assert.Null(ClaspCommandParser.ParseArgs(["--transport=demo", "--show=flyout"]));
        Assert.False(ClaspCommandParser.IsCommandLaunch(["--show=settings"]));
    }

    [Theory]
    [InlineData("--command=")]
    [InlineData("--command=reboot")]
    [InlineData("--uri=clasp://nope")]
    [InlineData("--uri=http://connect")]
    public void A_present_but_invalid_command_argument_is_a_failure_not_a_normal_launch(string arg)
    {
        var result = ClaspCommandParser.ParseArgs([arg]);
        Assert.NotNull(result);
        Assert.False(result!.Succeeded);
        Assert.True(ClaspCommandParser.IsCommandLaunch([arg]));
    }
}

public class CommandDispatcherTests
{
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeSink : ICommandSink
    {
        public List<ClaspCommand> Executed { get; } = [];
        public TaskCompletionSource<CommandExecutionResult>? Gate { get; set; }
        public Exception? Throw { get; set; }

        public async Task<CommandExecutionResult> ExecuteAsync(ClaspCommand command, CancellationToken cancellationToken = default)
        {
            Executed.Add(command);
            if (Throw is not null) throw Throw;
            return Gate is null ? CommandExecutionResult.Ok() : await Gate.Task;
        }
    }

    [Fact]
    public async Task Runs_a_command_and_reports_the_sink_result()
    {
        var sink = new FakeSink();
        var dispatcher = new CommandDispatcher(sink, new FakeTime());
        var result = await dispatcher.DispatchAsync(ClaspCommand.Connect);
        Assert.Equal(CommandDispatchStatus.Executed, result.Status);
        Assert.True(result.Execution!.Succeeded);
        Assert.Equal([ClaspCommand.Connect], sink.Executed);
    }

    [Fact]
    public async Task Identical_command_inside_one_second_is_ignored_then_allowed_after()
    {
        var time = new FakeTime();
        var sink = new FakeSink();
        var dispatcher = new CommandDispatcher(sink, time);
        await dispatcher.DispatchAsync(ClaspCommand.ShowFlyout);

        time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.Equal(CommandDispatchStatus.IgnoredDuplicate, (await dispatcher.DispatchAsync(ClaspCommand.ShowFlyout)).Status);

        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(CommandDispatchStatus.Executed, (await dispatcher.DispatchAsync(ClaspCommand.ShowFlyout)).Status);
        Assert.Equal(2, sink.Executed.Count);
    }

    [Fact]
    public async Task A_different_non_exclusive_command_is_not_a_duplicate()
    {
        var sink = new FakeSink();
        var dispatcher = new CommandDispatcher(sink, new FakeTime());
        await dispatcher.DispatchAsync(ClaspCommand.ShowFlyout);
        Assert.Equal(CommandDispatchStatus.Executed, (await dispatcher.DispatchAsync(ClaspCommand.PlayPause)).Status);
    }

    [Theory]
    [InlineData(ClaspCommand.Connect, ClaspCommand.FixConnection)]
    [InlineData(ClaspCommand.FixConnection, ClaspCommand.OutputAirPods)]
    [InlineData(ClaspCommand.OutputAirPods, ClaspCommand.OutputPrevious)]
    [InlineData(ClaspCommand.OutputPrevious, ClaspCommand.Connect)]
    [InlineData(ClaspCommand.Connect, ClaspCommand.Disconnect)]
    public async Task Connection_and_output_commands_never_overlap(ClaspCommand first, ClaspCommand second)
    {
        var sink = new FakeSink { Gate = new TaskCompletionSource<CommandExecutionResult>() };
        var dispatcher = new CommandDispatcher(sink, new FakeTime());

        var running = dispatcher.DispatchAsync(first);
        var refused = await dispatcher.DispatchAsync(second);
        Assert.Equal(CommandDispatchStatus.IgnoredBusy, refused.Status);
        Assert.Equal([first], sink.Executed);

        sink.Gate.SetResult(CommandExecutionResult.Ok());
        Assert.Equal(CommandDispatchStatus.Executed, (await running).Status);
    }

    [Fact]
    public async Task Show_and_playpause_still_run_while_a_connection_command_is_busy()
    {
        var gate = new TaskCompletionSource<CommandExecutionResult>();
        var sink = new FakeSink { Gate = gate };
        var dispatcher = new CommandDispatcher(sink, new FakeTime());
        var running = dispatcher.DispatchAsync(ClaspCommand.Connect);

        sink.Gate = null;
        Assert.Equal(CommandDispatchStatus.Executed, (await dispatcher.DispatchAsync(ClaspCommand.ShowSettings)).Status);
        Assert.Equal(CommandDispatchStatus.Executed, (await dispatcher.DispatchAsync(ClaspCommand.PlayPause)).Status);

        Assert.False(running.IsCompleted);
        gate.SetResult(CommandExecutionResult.Ok());
        await running;
    }

    [Fact]
    public async Task The_exclusive_lock_is_released_after_failure_and_after_an_exception()
    {
        var time = new FakeTime();
        var sink = new FakeSink { Throw = new InvalidOperationException("boom") };
        var dispatcher = new CommandDispatcher(sink, time);

        var faulted = await dispatcher.DispatchAsync(ClaspCommand.Connect);
        Assert.Equal(CommandDispatchStatus.Faulted, faulted.Status);
        Assert.DoesNotContain("boom", faulted.Message);

        sink.Throw = null;
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(CommandDispatchStatus.Executed, (await dispatcher.DispatchAsync(ClaspCommand.FixConnection)).Status);
    }

    [Fact]
    public void Only_connection_and_output_commands_are_exclusive()
    {
        var exclusive = Enum.GetValues<ClaspCommand>().Where(CommandDispatcher.IsExclusive).ToHashSet();
        Assert.Equal(
            new HashSet<ClaspCommand> { ClaspCommand.Connect, ClaspCommand.Disconnect, ClaspCommand.OutputAirPods, ClaspCommand.OutputPrevious, ClaspCommand.FixConnection },
            exclusive);
    }
}

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("Ctrl+Alt+A", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'A', "Ctrl+Alt+A")]
    [InlineData("alt+ctrl+a", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'A', "Ctrl+Alt+A")]
    [InlineData(" Ctrl + Shift + 7 ", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, '7', "Ctrl+Shift+7")]
    [InlineData("Win+Ctrl+Space", HotkeyModifiers.Win | HotkeyModifiers.Ctrl, 0x20, "Ctrl+Win+Space")]
    [InlineData("Ctrl+F12", HotkeyModifiers.Ctrl, 0x7B, "Ctrl+F12")]
    [InlineData("Alt+Num5", HotkeyModifiers.Alt, 0x65, "Alt+Num5")]
    [InlineData("Ctrl+Alt+MediaPlayPause", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0xB3, "Ctrl+Alt+MediaPlayPause")]
    public void Parses_and_formats_canonically(string text, HotkeyModifiers modifiers, int key, string canonical)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding, out var reason), reason);
        Assert.Equal(modifiers, binding!.Modifiers);
        Assert.Equal((uint)key, binding.VirtualKey);
        Assert.Equal(canonical, binding.ToString());
        Assert.True(binding.Validate().IsValid);
        Assert.Equal(binding, HotkeyBinding.ParseOrNull(canonical));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("+A")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Ctrl+A+B")]
    [InlineData("A+Ctrl")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+%")]
    public void Rejects_unparseable_text(string? text)
    {
        Assert.False(HotkeyBinding.TryParse(text, out var binding, out var reason));
        Assert.Null(binding);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Null(HotkeyBinding.ParseOrNull(text));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("F5")]
    [InlineData("Shift+A")]
    [InlineData("Win+A")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Escape")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Ctrl+Shift+Escape")]
    public void Validation_rejects_unsafe_or_system_combinations(string text)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding, out _));
        var validation = binding!.Validate();
        Assert.False(validation.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(validation.Reason));
        Assert.Null(HotkeyBinding.ParseOrNull(text));
    }

    [Theory]
    [InlineData("Ctrl+Alt+P")]
    [InlineData("Alt+Shift+F9")]
    [InlineData("Ctrl+Win+A")]
    [InlineData("Ctrl+Shift+Delete")]
    public void Validation_accepts_ordinary_combinations(string text)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding, out _));
        Assert.True(binding!.Validate().IsValid);
    }

    [Fact]
    public void Unsupported_virtual_keys_do_not_validate()
    {
        Assert.False(new HotkeyBinding(HotkeyModifiers.Ctrl, 0xFF).Validate().IsValid);
    }

    [Fact]
    public void Modifier_values_match_the_win32_flags()
    {
        Assert.Equal(1u, (uint)HotkeyModifiers.Alt);
        Assert.Equal(2u, (uint)HotkeyModifiers.Ctrl);
        Assert.Equal(4u, (uint)HotkeyModifiers.Shift);
        Assert.Equal(8u, (uint)HotkeyModifiers.Win);
    }
}
