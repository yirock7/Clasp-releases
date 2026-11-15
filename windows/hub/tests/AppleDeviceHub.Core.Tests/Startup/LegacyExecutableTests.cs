using AppleDeviceHub.Core.Startup;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Startup;

public sealed class LegacyExecutableTests
{
    private const string NewExe = @"C:\Users\jane\AppData\Local\Programs\Clasp\Clasp.exe";

    [Fact]
    public void A_sign_in_value_for_the_old_program_in_this_folder_is_pointed_at_the_new_one_with_its_arguments_kept()
    {
        var rewritten = LegacyExecutable.Rewrite(@"""C:\Users\jane\AppData\Local\Programs\Clasp\AppleDeviceHub.exe"" --transport=ble --startup", NewExe);
        Assert.Equal(@"""C:\Users\jane\AppData\Local\Programs\Clasp\Clasp.exe"" --transport=ble --startup", rewritten);
    }

    [Fact]
    public void The_clasp_link_command_is_rewritten_too()
    {
        var rewritten = LegacyExecutable.Rewrite(@"""c:\users\jane\appdata\local\programs\clasp\appledevicehub.EXE"" --uri=""%1""", NewExe);
        Assert.Equal(@"""" + NewExe + @""" --uri=""%1""", rewritten);
    }

    [Theory]
    [InlineData(@"""C:\Users\jane\AppData\Local\Programs\Clasp\Clasp.exe"" --transport=ble --startup")] // already current
    [InlineData(@"""D:\Elsewhere\AppleDeviceHub.exe"" --transport=ble --startup")] // another copy
    [InlineData(@"""C:\Users\jane\AppData\Local\Programs\Clasp\Other.exe"" --startup")] // another program
    [InlineData(@"C:\Users\jane\AppData\Local\Programs\Clasp\AppleDeviceHub.exe --startup")] // not quoted: not ours
    [InlineData(@"""C:\Users\jane\AppData\Local\Programs\Clasp\AppleDeviceHub.exe --startup")] // unterminated quote
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_left_alone(string? command)
        => Assert.Null(LegacyExecutable.Rewrite(command, NewExe));

    [Fact]
    public void Running_as_the_old_program_never_rewrites()
        => Assert.Null(LegacyExecutable.Rewrite(@"""C:\x\AppleDeviceHub.exe"" --startup", @"C:\x\AppleDeviceHub.exe"));

    [Fact]
    public void An_unusable_current_path_never_rewrites()
    {
        Assert.Null(LegacyExecutable.Rewrite(@"""C:\x\AppleDeviceHub.exe"" --startup", null));
        Assert.Null(LegacyExecutable.Rewrite(@"""C:\x\AppleDeviceHub.exe"" --startup", @"C:\x\bad""name\Clasp.exe"));
    }
}
