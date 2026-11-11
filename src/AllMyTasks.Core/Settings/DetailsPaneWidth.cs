namespace AllMyTasks.Core.Settings;

/// <summary>Width rules for the details pane the user can resize. Pure so the limits are unit-tested.</summary>
public static class DetailsPaneWidth
{
    /// <summary>Width of the pane until the user drags the splitter.</summary>
    public const double Default = 340;

    /// <summary>The pane never gets narrower than this, so its text stays readable.</summary>
    public const double Minimum = 260;

    /// <summary>The pane never gets wider than this, however wide the window is.</summary>
    public const double Maximum = 640;

    /// <summary>Width the main view always keeps when the pane is open.</summary>
    public const double MinimumContent = 360;

    /// <summary>
    /// Keeps <paramref name="width"/> within the limits. <paramref name="windowWidth"/> is the width available to
    /// both panes; pass 0 or less when it is not known yet. A non-finite width gives the default.
    /// </summary>
    public static double Clamp(double width, double windowWidth = 0)
    {
        if (!double.IsFinite(width))
        {
            width = Default;
        }

        var max = Maximum;
        if (windowWidth > 0)
        {
            // A narrow window wins over the maximum, but never goes below the pane minimum.
            max = Math.Min(max, windowWidth - MinimumContent);
        }

        max = Math.Max(max, Minimum);
        return Math.Clamp(width, Minimum, max);
    }
}
