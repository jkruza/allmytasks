namespace AllMyTasks.Core.Views;

/// <summary>Decides how many entries a month-grid cell can show for the height it really has.</summary>
public static class MonthCellFit
{
    /// <summary>
    /// How many of <paramref name="total"/> entries to draw. When all of them fit, all are shown and no "+N more" is
    /// needed; otherwise room is kept for the "+N more" button. Never negative, never more than <paramref name="total"/>.
    /// </summary>
    /// <param name="total">Entries on the day.</param>
    /// <param name="availableHeight">Height left in the cell for entries and the "+N more" button.</param>
    /// <param name="itemHeight">Height of one entry including its margin.</param>
    /// <param name="moreHeight">Height of the "+N more" button.</param>
    public static int VisibleCount(int total, double availableHeight, double itemHeight, double moreHeight)
    {
        if (total <= 0 || itemHeight <= 0)
        {
            return Math.Max(0, total);
        }

        if (total * itemHeight <= availableHeight)
        {
            return total;
        }

        var n = (int)Math.Floor((availableHeight - moreHeight) / itemHeight);
        return Math.Clamp(n, 0, total);
    }
}
