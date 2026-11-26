namespace MudBlazorCrmApp.Services.Agent;

public sealed record AttachmentRecord(
    string Id,
    string UserKey,
    string ChatKey,
    string FileName,
    string Kind,
    long SizeBytes,
    long MemoryBytes,
    string Hash,
    string Summary,
    DateTime ExpiresUtc,
    List<AttachmentSheet> Sheets,
    string? RawText,
    byte[]? ImageBytes,
    string? MediaType,
    byte[]? RawBytes = null);
