using System.Globalization;
using System.Runtime.CompilerServices;

namespace AppleDeviceHub.Core.Tests;

/// <summary>
/// Pins the whole test process to en-US so tests that assert English UI text (which now comes from
/// Strings.resx) and culture-formatted numbers pass on any machine, whatever its Windows display language.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
