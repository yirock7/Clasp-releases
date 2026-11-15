using System.Text;

namespace AppleDeviceHub.Core.Tests;

/// <summary>
/// Packet 2.2 review P1-1: a re-save double-encoded UTF-8 in user-visible strings (an ellipsis
/// became U+00E2 U+20AC U+00A6, a filled circle became U+00E2 U+2014 U+008F). Fail if any hub
/// source file contains these UTF-8-read-as-Windows-1252 sequences again. Escapes only: this file
/// must not contain the characters it looks for.
/// </summary>
public class SourceEncodingTests
{
    private static readonly string[] Mojibake =
    {
        "\u00e2\u20ac",   // start of a double-encoded U+2010..U+206F (dashes, quotes, ellipsis)
        "\u00e2\u2014",   // start of a double-encoded U+25C0..U+25FF (shapes such as the filled circle)
        "\u00c3\u00a9",   // U+00E9 (e acute) read as Windows-1252
    };

    [Fact]
    public void Hub_sources_contain_no_double_encoded_text()
    {
        var root = FindHubRoot();
        var offenders = new List<string>();
        foreach (var dir in new[] { "src", "tests" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.*", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            if (!(file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".xaml", StringComparison.Ordinal))) continue;
            var text = File.ReadAllText(file, Encoding.UTF8);
            if (Mojibake.Any(text.Contains)) offenders.Add(Path.GetRelativePath(root, file));
        }
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("\u2026")] // ellipsis, as in the tray menu's Settings item
    [InlineData("\u25cf")] // filled circle, the confirmed-mode dot
    public void The_check_recognises_the_double_encoded_form(string original)
    {
        // Negative control: encode as UTF-8, then read those bytes as Windows-1252, exactly the
        // corruption found in packet 2.2. The check must catch it.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var corrupted = Encoding.GetEncoding(1252).GetString(Encoding.UTF8.GetBytes(original));
        Assert.Contains(Mojibake, corrupted.Contains);
    }

    private static string FindHubRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AppleDeviceHub.sln"))) return dir.FullName;
        throw new InvalidOperationException("windows/hub not found above the test output folder.");
    }
}
