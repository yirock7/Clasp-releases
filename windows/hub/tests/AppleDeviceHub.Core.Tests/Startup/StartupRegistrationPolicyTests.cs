using AppleDeviceHub.Core.Startup;

namespace AppleDeviceHub.Core.Tests;

public class StartupRegistrationPolicyTests
{
    [Fact]
    public void Registry_names_match_the_installer_entry()
    {
        Assert.Equal("Clasp", StartupRegistrationPolicy.ValueName);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistrationPolicy.RunKeyPath);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", StartupRegistrationPolicy.ApprovalKeyPath);
    }

    [Fact]
    public void BuildCommand_quotes_the_path_and_adds_transport_and_startup_arguments()
    {
        var command = StartupRegistrationPolicy.BuildCommand(@"C:\Program Files\Clasp\AppleDeviceHub.exe");

        Assert.Equal(@"""C:\Program Files\Clasp\AppleDeviceHub.exe"" --transport=ble --startup", command);
    }

    [Fact]
    public void BuildCommand_trims_surrounding_spaces_from_the_path()
    {
        var command = StartupRegistrationPolicy.BuildCommand(@"  C:\Apps\AppleDeviceHub.exe  ");

        Assert.Equal(@"""C:\Apps\AppleDeviceHub.exe"" --transport=ble --startup", command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCommand_rejects_a_blank_path(string path)
    {
        Assert.Throws<ArgumentException>(() => StartupRegistrationPolicy.BuildCommand(path));
    }

    [Fact]
    public void BuildCommand_rejects_a_path_that_already_contains_quotes()
    {
        Assert.Throws<ArgumentException>(() => StartupRegistrationPolicy.BuildCommand("\"C:\\Apps\\Clasp.exe\""));
    }

    [Fact]
    public void Approval_is_assumed_when_no_approval_value_exists()
    {
        Assert.True(StartupRegistrationPolicy.IsApprovedByUser(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(0x02, true)]
    [InlineData(0x06, true)]
    [InlineData(0x03, false)]
    [InlineData(0x07, false)]
    public void Approval_byte_decides_whether_task_manager_disabled_the_entry(byte first, bool approved)
    {
        byte[] value = [first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(approved, StartupRegistrationPolicy.IsApprovedByUser(value));
    }

    [Fact]
    public void Evaluate_is_off_when_no_run_entry_exists_even_if_approval_says_disabled()
    {
        byte[] disabled = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(StartupStatus.Off, StartupRegistrationPolicy.Evaluate(false, disabled));
        Assert.Equal(StartupStatus.Off, StartupRegistrationPolicy.Evaluate(false, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Evaluate_is_on_when_entry_exists_and_is_approved()
    {
        byte[] approved = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(StartupStatus.On, StartupRegistrationPolicy.Evaluate(true, approved));
        Assert.Equal(StartupStatus.On, StartupRegistrationPolicy.Evaluate(true, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Evaluate_reports_task_manager_disable_instead_of_treating_the_entry_as_on()
    {
        byte[] disabled = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(StartupStatus.OffInTaskManager, StartupRegistrationPolicy.Evaluate(true, disabled));
    }

    [Fact]
    public void Describe_names_task_manager_when_the_user_turned_it_off()
    {
        var message = StartupMessages.Describe(StartupStatus.OffInTaskManager);

        Assert.Contains("Task Manager", message, StringComparison.Ordinal);
        Assert.Equal("Clasp starts when you sign in.", StartupMessages.Describe(StartupStatus.On));
        Assert.Equal("Clasp won't start when you sign in.", StartupMessages.Describe(StartupStatus.Off));
    }

    [Fact]
    public void Succeeded_result_reports_task_manager_block_without_claiming_it_runs()
    {
        var result = StartupMessages.Succeeded(StartupStatus.OffInTaskManager);

        Assert.True(result.Succeeded);
        Assert.Equal(StartupStatus.OffInTaskManager, result.Status);
        Assert.Contains("Task Manager", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_result_says_the_change_did_not_happen()
    {
        var result = StartupMessages.Failed(StartupStatus.Off);

        Assert.False(result.Succeeded);
        Assert.Equal(StartupStatus.Off, result.Status);
        Assert.StartsWith("Couldn't change", result.Message, StringComparison.Ordinal);
    }
}
