using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Unicode-safe display rendering of tool-call lines in the agent log.
/// DISPLAY-ONLY: it rewrites what the transcript shows; the raw entry text used for
/// persistence and for the LLM transcript is never touched.
/// </summary>
public static partial class AgentLogViewBuilder
{
    /// <summary>
    /// Re-renders a tool-call line so Persian arguments show as text instead of \uXXXX escapes
    /// (models/LiteLLM return escaped JSON in tool arguments). Falls back to the input when the
    /// argument block is not valid JSON.
    /// </summary>
    public static string DecodeToolText(string entryText)
    {
        if (string.IsNullOrEmpty(entryText) || !entryText.Contains("\\u", StringComparison.Ordinal))
            return entryText;

        // Language-neutral and deterministic: memoize, the same line re-renders on every state change.
        if (DecodeCache.TryGetValue(entryText, out var cached)) return cached;

        var decoded = DecodeEscaped(entryText);
        if (DecodeCache.Count > 4000) DecodeCache.Clear();
        DecodeCache[entryText] = decoded;
        return decoded;
    }

    private static readonly ConcurrentDictionary<string, string> DecodeCache = new(StringComparer.Ordinal);

    private static string DecodeEscaped(string entryText)
    {
        var open = entryText.IndexOf('(', StringComparison.Ordinal);
        var close = entryText.LastIndexOf(')');
        if (open <= 0 || close <= open) return entryText;
        try
        {
            var args = JsonNode.Parse(entryText[(open + 1)..close]);
            CleanJsonStrings(args);
            var rebuilt = string.Concat(entryText[..open], "(", args!.ToJsonString(DisplayJson), entryText[close..]);
            // ToJsonString re-escapes emoji/non-ASCII back to \uXXXX text; this is a display line,
            // not JSON a parser will read, so unescape whatever is left for a readable transcript.
            return rebuilt.Contains("\\u", StringComparison.Ordinal) ? UnescapeUnicode(rebuilt) : rebuilt;
        }
        catch (JsonException) { return entryText; }
    }

    /// <summary>
    /// Decodes leftover \uXXXX text inside JSON string values. Some models double-escape
    /// emoji/non-ASCII inside tool arguments (\\uXXXX survives one JSON parse as literal text).
    /// Numbers and other value types are left untouched.
    /// </summary>
    private static void CleanJsonStrings(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var prop in obj)
            {
                if (prop.Value is JsonValue val && val.GetValueKind() == JsonValueKind.String)
                {
                    var s = val.GetValue<string>();
                    if (s is not null && s.Contains("\\u", StringComparison.Ordinal)) obj[prop.Key] = UnescapeUnicode(s);
                }
                else CleanJsonStrings(prop.Value);
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonValue val && val.GetValueKind() == JsonValueKind.String)
                {
                    var s = val.GetValue<string>();
                    if (s is not null && s.Contains("\\u", StringComparison.Ordinal)) arr[i] = UnescapeUnicode(s);
                }
                else CleanJsonStrings(arr[i]);
            }
        }
    }

    private static readonly Regex UnicodeEscape =
        new(@"\\u([0-9a-fA-F]{4})(\\u([0-9a-fA-F]{4}))?", RegexOptions.Compiled);

    private static string UnescapeUnicode(string s) => UnicodeEscape.Replace(s, m =>
    {
        var c1 = (char)Convert.ToInt32(m.Groups[1].Value, 16);
        if (m.Groups[3].Success)
        {
            var c2 = (char)Convert.ToInt32(m.Groups[3].Value, 16);
            if (char.IsHighSurrogate(c1) && char.IsLowSurrogate(c2))
                return string.Concat(c1, c2);
        }
        return c1.ToString();
    });

    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
