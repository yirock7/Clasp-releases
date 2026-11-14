using AppleDeviceHub.Core.Commands;

namespace AppleDeviceHub.Core.Tests.Commands;

public class CommandConfirmationTests
{
    [Theory]
    [InlineData(ClaspCommand.OutputAirPods, true)]
    [InlineData(ClaspCommand.OutputPrevious, true)]
    [InlineData(ClaspCommand.FixConnection, true)]
    [InlineData(ClaspCommand.PlayPause, true)]
    [InlineData(ClaspCommand.ShowFlyout, false)]
    [InlineData(ClaspCommand.ShowSettings, false)]
    [InlineData(ClaspCommand.Connect, true)]
    [InlineData(ClaspCommand.Disconnect, false)]
    public void Decides_which_external_commands_need_confirmation(ClaspCommand command, bool expected)
        => Assert.Equal(expected, CommandConfirmation.RequiresConfirmation(command));

    [Fact]
    public void Every_confirmable_command_has_prompt_text()
    {
        foreach (var command in Enum.GetValues<ClaspCommand>())
            if (CommandConfirmation.RequiresConfirmation(command))
                Assert.EndsWith("Allow?", CommandConfirmation.PromptText(command, null));
    }

    [Fact]
    public void Prompt_names_the_output()
        => Assert.Equal("A link asked Clasp to switch your sound to Headphones. Allow?",
            CommandConfirmation.PromptText(ClaspCommand.OutputPrevious, " Headphones "));

    [Fact]
    public void Prompt_falls_back_to_generic_wording()
        => Assert.Equal("A link asked Clasp to switch your sound to your AirPods. Allow?",
            CommandConfirmation.PromptText(ClaspCommand.OutputAirPods, ""));

    [Fact]
    public void Connect_prompt_says_the_sound_will_move()
        => Assert.Equal("A link asked Clasp to connect your AirPods and switch your sound to them. Allow?",
            CommandConfirmation.PromptText(ClaspCommand.Connect, "ignored name"));

    [Fact]
    public void PlayPause_prompt_is_plain()
        => Assert.Equal("A link asked Clasp to play or pause your media. Allow?",
            CommandConfirmation.PromptText(ClaspCommand.PlayPause, null));

    [Fact]
    public void Unconfirmed_commands_have_no_prompt()
        => Assert.Throws<ArgumentOutOfRangeException>(() => CommandConfirmation.PromptText(ClaspCommand.ShowSettings, null));
}
