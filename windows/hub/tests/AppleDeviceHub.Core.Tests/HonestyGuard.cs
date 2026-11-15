using System.Text.RegularExpressions;

namespace AppleDeviceHub.Core.Tests;

/// <summary>
/// Test-side rule for labels that describe an UNREPORTED value (what is shown and what Narrator
/// reads). Such a label must not claim a value: no digit, no "%", and no standalone "on"/"off".
/// Known values ("Off", "0%") are legitimate elsewhere; this guard is only applied to unknown labels.
/// </summary>
internal static class HonestyGuard
{
    private static readonly Regex ValueClaim = new(@"\d|%|\b(on|off)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="label"/> states or implies a value.</summary>
    public static bool ClaimsAValue(string label) => ValueClaim.IsMatch(label);

    public static void AssertClaimsNoValue(params string[] labels)
    {
        foreach (var label in labels)
            Assert.False(ClaimsAValue(label), $"Label for an unreported value claims a value: \"{label}\"");
    }
}

public class HonestyGuardTests
{
    // Labels that would be dishonest for a value the device has not reported.
    [Theory]
    [InlineData("Off")]
    [InlineData("off")]
    [InlineData("On")]
    [InlineData("0")]
    [InlineData("0%")]
    [InlineData("82%")]
    [InlineData("Conversation Awareness, off")]
    [InlineData("Adaptive strength 0")]
    [InlineData("Left earbud, 0 percent")]
    public void Rejects_labels_that_claim_a_value(string label) => Assert.True(HonestyGuard.ClaimsAValue(label));

    // The labels the app actually uses for unreported values.
    [Theory]
    [InlineData("Unknown")]
    [InlineData("—")]
    [InlineData("Conversation Awareness, not reported yet")]
    [InlineData("Adaptive strength, not reported yet")]
    [InlineData("Left earbud, battery unknown, demo data")]
    [InlineData("Case, battery unknown")]
    public void Accepts_labels_that_say_unknown(string label) => Assert.False(HonestyGuard.ClaimsAValue(label));
}
