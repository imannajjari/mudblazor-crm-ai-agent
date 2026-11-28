namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Maps OpenXML cell references (for example <c>A1</c> or <c>AB7</c>) to zero-based column indexes.
/// Sparse worksheets omit empty cells, so the reference is required to keep values in their real columns.
/// </summary>
internal static class AgentCellReference
{
    private const int MaxReasonableColumn = 10_000;

    public static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;

        var column = 0;
        var hasLetter = false;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') column = column * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') column = column * 26 + (ch - 'a' + 1);
            else break;

            hasLetter = true;
            if (column > MaxReasonableColumn) return -1;
        }
        return hasLetter ? column - 1 : -1;
    }
}
