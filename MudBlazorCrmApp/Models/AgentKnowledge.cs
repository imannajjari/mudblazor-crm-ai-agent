using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace MudBlazorCrmApp.Models;

[Index(nameof(FactHash), IsUnique = true)]
public class AgentKnowledge
{
    public long Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string FactHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Topic { get; set; } = string.Empty;

    [Required]
    [MaxLength(400)]
    public string Fact { get; set; } = string.Empty;

    [MaxLength(10)]
    public string? LanguageId { get; set; }

    [Required]
    [MaxLength(128)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedDate { get; set; }
    public int UseCount { get; set; }
    public int ConfirmCount { get; set; } = 1;
    public int NotHelpedCount { get; set; }
    public bool IsRetired { get; set; }

    [MaxLength(128)]
    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
