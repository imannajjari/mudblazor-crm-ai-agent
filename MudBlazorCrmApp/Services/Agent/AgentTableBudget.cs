namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Tracks per-file cell and character allocations so sparse XLSX workbooks cannot exhaust server memory.
/// </summary>
public sealed class AgentTableBudget
{
    private const long MaxCells = 1_000_000;
    private const long MaxChars = 5_000_000;
    private const long MaxAllocatedCells = 2_000_000;

    private long _allocated;
    private long _cells;
    private long _chars;

    public string? TryChargeAllocation(long cells)
    {
        _allocated += cells;
        return _allocated > MaxAllocatedCells ? "The table is too large to read." : null;
    }

    public string? TryChargeCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        _cells++;
        _chars += value.Length;
        return _cells > MaxCells || _chars > MaxChars ? "The table is too large to read." : null;
    }
}
