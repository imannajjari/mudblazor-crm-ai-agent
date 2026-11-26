using Microsoft.Extensions.Caching.Memory;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Tracks help sections already delivered within a chat session to avoid duplicate guide dumps.
/// </summary>
public sealed class HelpSessionStore(IMemoryCache cache)
{
    public bool TryMarkSent(string userKey, string chatKey, string sectionTitle)
    {
        if (string.IsNullOrWhiteSpace(chatKey) || string.IsNullOrWhiteSpace(sectionTitle))
            return true;

        var set = cache.GetOrCreate($"help_seen:{userKey}:{chatKey}", entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        })!;

        lock (set)
        {
            return set.Add(sectionTitle.Trim());
        }
    }
}
