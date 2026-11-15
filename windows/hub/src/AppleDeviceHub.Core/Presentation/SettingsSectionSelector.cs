namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// Picks which Settings section the left rail should highlight while the page scrolls.
/// Pure arithmetic so it can be tested without a window.
/// </summary>
public static class SettingsSectionSelector
{
    /// <summary>The reading line is this fraction of the viewport height below its top.</summary>
    public const double ReadingLineFraction = 1.0 / 3.0;

    /// <summary>
    /// Returns the index of the section to highlight, or -1 when none is visible.
    /// <paramref name="sectionTops"/> are in on-page order: each section's top edge measured from the
    /// top of the viewport (negative once scrolled past), or null for a hidden section.
    /// The current section is the last one whose top has reached the reading line. At the very top the
    /// first section wins; at the very end the last one wins, because short final sections can never
    /// scroll up to the line.
    /// </summary>
    public static int Choose(IReadOnlyList<double?> sectionTops, double viewportHeight, bool atTop, bool atEnd)
    {
        int first = -1, last = -1, current = -1;
        double line = Math.Max(0, viewportHeight) * ReadingLineFraction;
        for (int i = 0; i < sectionTops.Count; i++)
        {
            if (sectionTops[i] is not { } top) continue;
            if (first < 0) first = i;
            last = i;
            if (top <= line) current = i;
        }
        if (first < 0) return -1;
        if (atTop) return first;
        if (atEnd) return last;
        return current >= 0 ? current : first;
    }
}
