namespace AllMyTasks.Core.Notes;

/// <summary>Shared helpers for building inline lists in the HTML and Markdown converters.</summary>
internal static class InlineTools
{
    /// <summary>Adds text, merging it into the previous inline when the style is the same.</summary>
    public static void AddText(List<NoteInline> sink, string text, bool bold, bool italic, bool strike, bool code)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (sink.Count > 0 && sink[^1] is TextInline last && last.Bold == bold && last.Italic == italic && last.Strike == strike && last.Code == code)
        {
            sink[^1] = last with { Text = last.Text + text };
        }
        else
        {
            sink.Add(new TextInline(text, bold, italic, strike, code));
        }
    }

    /// <summary>Removes leading and trailing blanks and line breaks, and blanks after a line break.</summary>
    public static List<NoteInline> Tidy(List<NoteInline> source)
    {
        var list = new List<NoteInline>(source);
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i - 1] is LineBreakInline && list[i] is TextInline after)
            {
                list[i] = after with { Text = after.Text.TrimStart() };
            }
        }

        while (list.Count > 0)
        {
            if (list[0] is LineBreakInline || list[0] is TextInline { Text: var t } && string.IsNullOrWhiteSpace(t))
            {
                list.RemoveAt(0);
            }
            else if (list[0] is TextInline first)
            {
                list[0] = first with { Text = first.Text.TrimStart() };
                break;
            }
            else
            {
                break;
            }
        }

        while (list.Count > 0)
        {
            if (list[^1] is LineBreakInline || list[^1] is TextInline { Text: var t } && string.IsNullOrWhiteSpace(t))
            {
                list.RemoveAt(list.Count - 1);
            }
            else if (list[^1] is TextInline last)
            {
                list[^1] = last with { Text = last.Text.TrimEnd() };
                break;
            }
            else
            {
                break;
            }
        }

        return list;
    }
}
