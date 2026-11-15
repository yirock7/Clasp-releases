using AppleDeviceHub.Core.Presentation;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Presentation;

public sealed class SettingsSectionSelectorTests
{
    private static int Choose(double?[] tops, bool atTop = false, bool atEnd = false, double viewport = 600)
        => SettingsSectionSelector.Choose(tops, viewport, atTop, atEnd);

    [Fact]
    public void NoVisibleSectionsChoosesNothing()
        => Assert.Equal(-1, Choose(new double?[] { null, null }));

    [Fact]
    public void AtTopChoosesTheFirstSection()
        => Assert.Equal(0, Choose(new double?[] { 0, 400, 900 }, atTop: true));

    [Fact]
    public void AtTopSkipsHiddenSections()
        => Assert.Equal(1, Choose(new double?[] { null, 0, 400 }, atTop: true));

    [Fact]
    public void ChoosesLastSectionWhoseTopReachedTheReadingLine()
    {
        // line is 200 for a 600px viewport
        Assert.Equal(1, Choose(new double?[] { -500, 150, 450 }));
        Assert.Equal(0, Choose(new double?[] { -100, 250, 450 }));
        Assert.Equal(2, Choose(new double?[] { -900, -300, 200 }));
    }

    [Fact]
    public void BelowTheLineTheSectionStillAboveItStaysSelected()
        => Assert.Equal(0, Choose(new double?[] { -50, 201, 500 }));

    [Fact]
    public void FirstVisibleIsChosenWhenNothingHasReachedTheLine()
        => Assert.Equal(0, Choose(new double?[] { 300, 700 }));

    [Fact]
    public void AtEndChoosesTheLastVisibleSectionEvenWhenItCannotReachTheLine()
        => Assert.Equal(2, Choose(new double?[] { -1200, 100, 450 }, atEnd: true));

    [Fact]
    public void AtEndSkipsTrailingHiddenSections()
        => Assert.Equal(1, Choose(new double?[] { -900, 450, null }, atEnd: true));

    [Fact]
    public void HiddenSectionsAreNeverChosen()
        => Assert.Equal(0, Choose(new double?[] { -500, null, 450 }));

    [Fact]
    public void AtTopWinsWhenTheWholePageFitsAndIsAlsoAtTheEnd()
        => Assert.Equal(0, Choose(new double?[] { 0, 200, 400 }, atTop: true, atEnd: true));

    [Fact]
    public void TopsAreEvaluatedInPageOrderNotByValue()
        // A later entry that happens to be above the line (shared header, odd layout) is still "last reached".
        => Assert.Equal(2, Choose(new double?[] { -300, -100, 50, 400 }));
}
