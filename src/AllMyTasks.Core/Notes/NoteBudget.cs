namespace AllMyTasks.Core.Notes;

/// <summary>Limits that keep a hostile note from stalling the UI.</summary>
internal sealed class NoteBudget
{
    /// <summary>Nesting deeper than this (lists in quotes in lists, nested elements) is flattened or dropped.</summary>
    public const int MaxDepth = 32;

    /// <summary>Most blocks one note may produce.</summary>
    public const int MaxBlocks = 2_000;

    private int _blocks;

    public bool TryTakeBlock()
    {
        if (_blocks >= MaxBlocks)
        {
            return false;
        }

        _blocks++;
        return true;
    }
}
