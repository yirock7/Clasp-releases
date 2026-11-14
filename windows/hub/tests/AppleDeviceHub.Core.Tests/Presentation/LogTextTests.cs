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
}
