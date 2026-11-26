using System.Globalization;
using System.Text.Json;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Builds structured mini-chart specs and [[chart|Title|label:value,...]] directives for AgentPanel visualization.
/// </summary>
public static class AgentChartService
{
    public sealed record ChartPoint(string Label, decimal Value);

    public static string BuildChartDirective(string title, IReadOnlyList<ChartPoint> points)
    {
        if (points.Count == 0) return string.Empty;
        var cleanTitle = title.Replace("|", "-").Replace("]", "").Trim();
        var serialized = string.Join(",", points.Take(12).Select(p =>
        {
            var lbl = p.Label.Replace("|", " ").Replace(",", " ").Replace(":", " ").Replace("]", "").Trim();
            return $"{lbl}:{p.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
        }));
        return $"[[chart|{cleanTitle}|{serialized}]]";
    }

    public static string BuildChartResultJson(string title, JsonElement pointsArray)
    {
        var list = new List<ChartPoint>();
        if (pointsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pointsArray.EnumerateArray())
            {
                var label = item.TryGetProperty("label", out var lEl) && lEl.ValueKind == JsonValueKind.String ? lEl.GetString() ?? "" : "";
                decimal val = 0m;
                if (item.TryGetProperty("value", out var vEl) && vEl.TryGetDecimal(out var d)) val = d;
                if (!string.IsNullOrWhiteSpace(label)) list.Add(new(label, val));
            }
        }
        var directive = BuildChartDirective(title, list);
        return string.IsNullOrEmpty(directive)
            ? JsonSerializer.Serialize(new { ok = false, error = "At least one labeled numeric data point is required." })
            : JsonSerializer.Serialize(new { ok = true, directive, count = list.Count });
    }
}
