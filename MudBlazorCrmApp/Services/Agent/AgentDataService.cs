using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace MudBlazorCrmApp.Services.Agent;

public sealed class AgentDataService(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor)
{
    private const int MaxResponseChars = 6000;
    private const int DefaultTop = 10;
    private const int MaxTop = 25;

    private static readonly Dictionary<string, DataSourceDef> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard_kpis"] = new("dashboard_kpis", "/api/dashboard/kpis", false, null, "Key CRM KPIs: newLeads, totalRevenue, conversionRate, pipelineValue, dealsClosed, avgDealSize, totalCustomers, openSupportCases."),
        ["customers"] = new("customers", "Customer", true, "Name", "Customers / companies list with count, Name, Type, Industry, Notes."),
        ["contacts"] = new("contacts", "Contact", true, "Name", "Contacts list with count, Name, Email, Phone, Title, Status."),
        ["leads"] = new("leads", "Lead", true, "Name", "Leads list with count, Name, Company, Email, Source, Status, Value."),
        ["opportunities"] = new("opportunities", "Opportunity", true, "Name", "Deals / opportunities with count, Name, Stage, Value, Probability, EstimatedCloseDate."),
        ["sales"] = new("sales", "Sale", true, null, "Completed sales records with count, SaleDate, TotalAmount."),
        ["support_cases"] = new("support_cases", "SupportCase", true, "Title", "Support cases / tickets with count, Title, Status, Priority."),
        ["activities"] = new("activities", "Activity", true, "Subject", "Activities (calls, emails, meetings, notes) with count, Subject, Type, Status."),
        ["todo_tasks"] = new("todo_tasks", "TodoTask", true, "Title", "Tasks list with count, Title, Status, Priority, DueDate.")
    };

    public static string GetCatalogDescription()
    {
        return string.Join("\n", Sources.Values.Select(s => $"- `{s.Name}`: {s.Description}"));
    }

    public async Task<string> ReadAsync(JsonElement args, CancellationToken ct)
    {
        var sourceName = args.TryGetProperty("source", out var srcEl) ? srcEl.GetString()?.Trim() ?? "" : "";
        if (!Sources.TryGetValue(sourceName, out var def))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Unknown data source '{sourceName}'.",
                availableSources = Sources.Keys.ToArray()
            });
        }

        var top = args.TryGetProperty("top", out var topEl) && topEl.TryGetInt32(out var t)
            ? Math.Clamp(t, 1, MaxTop)
            : DefaultTop;
        var skip = args.TryGetProperty("skip", out var skipEl) && skipEl.TryGetInt32(out var sk)
            ? Math.Max(0, sk)
            : 0;
        var search = args.TryGetProperty("search", out var sEl) ? sEl.GetString()?.Trim() : null;
        var status = args.TryGetProperty("status", out var stEl) ? stEl.GetString()?.Trim() : null;

        var httpCtx = httpContextAccessor.HttpContext;
        if (httpCtx == null)
            return JsonSerializer.Serialize(new { ok = false, error = "No HTTP context for loopback read." });

        var baseUri = $"{httpCtx.Request.Scheme}://{httpCtx.Request.Host}";
        string relativeUri;
        if (!def.IsOData)
        {
            relativeUri = def.EndpointOrEntity;
        }
        else
        {
            var qs = HttpUtility.ParseQueryString(string.Empty);
            qs["$count"] = "true";
            qs["$top"] = top.ToString();
            if (skip > 0) qs["$skip"] = skip.ToString();

            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(search) && !string.IsNullOrWhiteSpace(def.SearchField))
            {
                var safe = search.Replace("'", "''");
                filters.Add($"contains(tolower({def.SearchField}), '{safe.ToLowerInvariant()}')");
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                var safeStatus = status.Replace("'", "''");
                var field = def.Name == "opportunities" ? "Stage" : "Status";
                filters.Add($"{field} eq '{safeStatus}'");
            }
            if (filters.Count > 0)
            {
                qs["$filter"] = string.Join(" and ", filters);
            }

            relativeUri = $"/odata/{def.EndpointOrEntity}?{qs}";
        }

        var client = httpClientFactory.CreateClient("Loopback");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUri), relativeUri));

        if (httpCtx.Request.Headers.TryGetValue("Authorization", out var authValues))
        {
            var authHeader = authValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(authHeader) && AuthenticationHeaderValue.TryParse(authHeader, out var parsed))
            {
                req.Headers.Authorization = parsed;
            }
        }

        try
        {
            using var res = await client.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Unauthorized to read this data source. Please log in first."
                });
            }

            if (!res.IsSuccessStatusCode)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"Data endpoint returned HTTP {(int)res.StatusCode}."
                });
            }

            var raw = await res.Content.ReadAsStringAsync(ct);
            return ProjectResult(def, raw, skip, top);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    private static string ProjectResult(DataSourceDef def, string rawJson, int skip, int top)
    {
        if (!def.IsOData)
        {
            var trimmed = rawJson.Length > MaxResponseChars ? rawJson[..MaxResponseChars] : rawJson;
            return JsonSerializer.Serialize(new { ok = true, source = def.Name, data = JsonNode.Parse(trimmed) });
        }

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        int? totalCount = null;
        if (root.TryGetProperty("@odata.count", out var countEl) && countEl.TryGetInt32(out var c))
            totalCount = c;

        var rows = new JsonArray();
        if (root.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueEl.EnumerateArray())
            {
                rows.Add(JsonNode.Parse(item.GetRawText()));
            }
        }

        var moreRows = totalCount.HasValue && (skip + rows.Count) < totalCount.Value;
        var wrapper = new JsonObject
        {
            ["ok"] = true,
            ["source"] = def.Name,
            ["count"] = totalCount,
            ["skip"] = skip,
            ["rowsReturned"] = rows.Count,
            ["moreRows"] = moreRows,
            ["rows"] = rows
        };

        var json = wrapper.ToJsonString();
        while (json.Length > MaxResponseChars && rows.Count > 1)
        {
            rows.RemoveAt(rows.Count - 1);
            wrapper["rowsReturned"] = rows.Count;
            wrapper["moreRows"] = true;
            wrapper["countIsCapped"] = true;
            json = wrapper.ToJsonString();
        }

        return json;
    }

    private sealed record DataSourceDef(string Name, string EndpointOrEntity, bool IsOData, string? SearchField, string Description);
}
