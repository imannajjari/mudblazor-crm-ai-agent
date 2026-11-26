namespace MudBlazorCrmApp.Controllers;

public sealed record AttachmentUploadRequest(string ChatKey, string FileName, string Content);
