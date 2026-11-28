using System.Linq.Expressions;
using MudBlazorCrmApp.Models;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Retention rules for the agent knowledge store: when a fact is retired after unhelpful
/// marks, and when untouched or unused facts are deleted outright. Kept as pure functions
/// so the thresholds and the delete predicate are unit-testable; the throttled execution
/// lives in <see cref="AgentKnowledgeService"/>.
/// </summary>
public static class AgentKnowledgePurger
{
    /// <summary>A fact is retired once it was marked "did not help" this many times and outvoted its confirmations.</summary>
    public const int RetireAfterNotHelped = 2;

    /// <summary>Facts idle and untouched past this many days are candidates for deletion.</summary>
    public const int RetainDays = 90;

    /// <summary>A fact used at least this many times survives the idle window if it was used recently.</summary>
    public const int MinUseCountToKeep = 3;

    public static DateTime Cutoff(DateTime now) => now.AddDays(-RetainDays);

    /// <summary>
    /// Delete predicate: last meaningful change is older than the cutoff AND the fact was
    /// either never used recently or never earned real use. Used directly in an EF query.
    /// </summary>
    public static Expression<Func<AgentKnowledge, bool>> OlderThan(DateTime cutoff) =>
        k => (k.ModifiedDate ?? k.CreatedDate) < cutoff
             && ((k.LastUsedDate ?? k.CreatedDate) < cutoff || k.UseCount < MinUseCountToKeep);
}
