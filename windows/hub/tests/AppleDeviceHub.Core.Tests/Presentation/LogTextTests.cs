using AppleDeviceHub.Core.Presentation;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Presentation;

public sealed class LogTextTests
{
    [Fact]
    public void Line_breaks_and_control_characters_are_escaped_so_one_record_stays_one_line()
    {
        var text = LogText.Sanitize("a\r\n2026-01-01 forged line\tb\u0007c\u2028d");
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\u0007', text);
        Assert.Equal("a\\r\\n2026-01-01 forged line\\tb\\u0007c\\u2028d", text);
    }

    [Fact]
    public void A_record_is_capped_at_about_two_kilobytes()
    {
        var text = LogText.Sanitize(new string('x', 100_000));
        Assert.True(text.Length <= LogText.MaxRecordChars + 20);
        Assert.EndsWith("[truncated]", text);
    }

    [Fact]
    public void Short_plain_text_is_unchanged()
        => Assert.Equal("Flyout open: waited 48 ms", LogText.Sanitize("Flyout open: waited 48 ms"));

    [Fact]
    public void Null_and_empty_give_empty()
    {
        Assert.Equal("", LogText.Sanitize(null));
        Assert.Equal("", LogText.Sanitize(""));
    }

    [Fact]
    public void Opaque_refs_are_stable_short_and_hide_the_value()
    {
        var a = LogText.OpaqueRef("{0.0.0.00000000}.{abc}");
        Assert.Equal(a, LogText.OpaqueRef("{0.0.0.00000000}.{abc}"));
        Assert.NotEqual(a, LogText.OpaqueRef("other"));
        Assert.Equal(12, a.Length);
        Assert.DoesNotContain("abc", a);
        Assert.Equal("ref:none", LogText.OpaqueRef(null));
    }

    private static Exception Thrown(Exception exception)
    {
        try { throw exception; }
        catch (Exception caught) { return caught; }
    }

    [Fact]
    public void Exception_text_has_type_hresult_and_stack_but_never_the_message()
    {
        var thrown = Thrown(new InvalidOperationException("Headphones (Jane's AirPods Pro) AA:BB:CC:DD:EE:FF", new IOException("C:/Users/jane/secret.txt")));
        var text = LogText.Exception(thrown);
        Assert.Contains("System.InvalidOperationException (0x", text);
        Assert.Contains("System.IO.IOException (0x", text);
        Assert.Contains(nameof(Thrown), text);
        Assert.DoesNotContain("Jane", text);
        Assert.DoesNotContain("AA:BB", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public void Exception_text_covers_aggregate_children_without_their_messages()
    {
        var aggregate = new AggregateException("outer name", new ArgumentException("child name"));
        var text = LogText.Exception(aggregate);
        Assert.Contains("System.ArgumentException", text);
        Assert.DoesNotContain("name", text);
    }

    [Fact]
    public void Exception_kind_is_one_short_message_free_phrase()
    {
        var text = LogText.ExceptionKind(new System.ComponentModel.Win32Exception("secret device"));
        Assert.StartsWith("System.ComponentModel.Win32Exception (0x", text);
        Assert.DoesNotContain("secret", text);
        Assert.Equal("no exception", LogText.Exception(null));
    }
}
