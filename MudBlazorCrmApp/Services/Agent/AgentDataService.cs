using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

public sealed class AgentDataService(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    IOptions<AgentOptions> optionsAccessor) : IAgentDataReader
{
    private const int MaxResponseChars = 6000;
    private const int DefaultTop = 10;
    private const int MaxTop = 25;
    private const int MaxParamLength = 200;
    private static readonly TimeSpan LoopbackTimeout = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> AllowedForwardedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Accept-Language", "X-Agent-Language"
    };

    private static readonly Dictionary<string, DataSourceDef> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard_kpis"] = new("dashboard_kpis", "/api/dashboard/kpis", false, null, "Key CRM KPIs: newLeads, totalRevenue, conversionRate, pipelineValue, dealsClosed, avgDealSize, totalCustomers, openSupportCases."),
        ["customers"] = new("customers", "Customer", true, "Name", "Customers / companies list with count, Name, Type, Industry, Notes."),
        ["contacts"] = new("contacts", "Contact", true, "Name", "Contacts list with count, Name, Email, Phone, Title, Status."),
        ["leads"] = new("leads", "Lead", true, "Company", "Leads list with count, Company, Title, Industry, Source, Status, PotentialValue."),
        ["opportunities"] = new("opportunities", "Opportunity", true, "Name", "Deals / opportunities with count, Name, Stage, Value, Probability, EstimatedCloseDate."),
        ["sales"] = new("sales", "Sale", true, "InvoiceNumber", "Completed sales records with count, InvoiceNumber, PaymentStatus, SaleDate, TotalAmount."),
        ["support_cases"] = new("support_cases", "SupportCase", true, "Title", "Support cases / tickets with count, Title, Status, Priority."),
        ["activities"] = new("activities", "Activity", true, "Subject", "Activities (calls, emails, meetings, notes) with count, Subject, Type, Status."),
        ["todo_tasks"] = new("todo_tasks", "TodoTask", true, "Name", "Tasks list with count, Name, Description, Status, DueDate."),
        ["addresses"] = new("addresses", "Address", true, "Street", "Addresses list with count, Street, City, State, ZipCode, Country."),
        ["products"] = new("products", "Product", true, "Name", "Products catalog with count, Name, Price, StockQuantity, Description."),
        ["product_categories"] = new("product_categories", "ProductCategory", true, "Name", "Product categories with count, Name."),
        ["services"] = new("services", "Service", true, "Name", "Services catalog with count, Name, Recurring, Description."),
        ["service_categories"] = new("service_categories", "ServiceCategory", true, "Name", "Service categories with count, Name."),
        ["vendors"] = new("vendors", "Vendor", true, "Name", "Vendors / suppliers list with count, Name, ContactName, Phone, Email."),
        ["rewards"] = new("rewards", "Reward", true, null, "Contact rewards ledger with count, Rewardpoints, CreditsDollars, ConversionRate, ExpirationDate."),
        ["tags"] = new("tags", "Tag", true, "Name", "Tags list with count, Name, Color."),
        ["communications"] = new("communications", "Communication", true, "Subject", "Communications log with count, Subject, Type, Direction, Status, Outcome, FollowUpRequired.")
    };

    public static string GetCatalogDescription() =>
        string.Join("\n", Sources.Values.Select(s => $"- `{s.Name}`: {s.Description}"));

    public static string GetSourceEnumJson() =>
        JsonSerializer.Serialize(Sources.Keys.ToArray());

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

        if (!TryReadValidatedParam(args, "search", out var search, out var paramErr) ||
            !TryReadValidatedParam(args, "status", out var status, out paramErr))
        {
            return JsonSerializer.Serialize(new { ok = false, error = paramErr });
        }

        var top = args.TryGetProperty("top", out var topEl) && topEl.TryGetInt32(out var t) ? Math.Clamp(t, 1, MaxTop) : DefaultTop;
        var skip = args.TryGetProperty("skip", out var skipEl) && skipEl.TryGetInt32(out var sk) ? Math.Clamp(sk, 0, 100_000) : 0;

        var httpCtx = httpContextAccessor.HttpContext;
        if (httpCtx == null)
            return JsonSerializer.Serialize(new { ok = false, error = "No HTTP context for loopback read." });

        var baseUri = ResolveBaseUri(httpCtx);
        var relativeUri = BuildRelativeUri(def, top, skip, search, status);

        var opts = optionsAccessor.Value;
        opts.Normalize();
        var maxBytes = (long)opts.MaxLoopbackResponseMB * 1024L * 1024L;

        var client = httpClientFactory.CreateClient("Loopback");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUri), relativeUri));
        ForwardSafeHeaders(httpCtx, req);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(LoopbackTimeout);

        try
        {
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return JsonSerializer.Serialize(new { ok = false, error = "Unauthorized to read this data source. Please log in first." });
            if (!res.IsSuccessStatusCode)
                return JsonSerializer.Serialize(new { ok = false, error = $"Data endpoint returned HTTP {(int)res.StatusCode}." });

            if (res.Content.Headers.ContentLength > maxBytes)
                return JsonSerializer.Serialize(new { ok = false, error = "Data response exceeded size limit." });

            var raw = await ReadLimitedUtf8Async(res.Content, maxBytes, linkedCts.Token);
            if (raw == null)
                return JsonSerializer.Serialize(new { ok = false, error = "Data response exceeded size limit." });

            return ProjectResult(def, raw, skip);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return JsonSerializer.Serialize(new { ok = false, error = "Loopback data request timed out." });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = $"The data could not be read ({ex.GetType().Name})." });
        }
    }

    private string ResolveBaseUri(HttpContext httpCtx) =>
        ResolveLoopbackBase(optionsAccessor.Value.LoopbackBaseUrl, httpCtx.Request.Scheme, httpCtx.Connection.LocalPort);

    // Pure (testable) base resolution for the loopback self-call. The connection's own local port is
    // the only port guaranteed to belong to the listener that served this request: scanning the server
    // address list for the "first same-scheme bind" would self-call a DIFFERENT listener on
    // multi-bind launches (e.g. two https ports -> wrong cert/app). The scheme is the one this request
    // arrived on; loopback requests are exempted from HTTPS redirection in Program.cs, so this cannot
    // bounce with a 307 (the Loopback client never follows redirects).
    public static string ResolveLoopbackBase(string? configured, string scheme, int localPort)
    {
        if (TrySanitizeConfiguredBase(configured, out var configuredBase))
            return configuredBase;

        var reqPort = localPort > 0 ? localPort : 5025;
        var reqScheme = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        return $"{reqScheme}://127.0.0.1:{reqPort}";
    }

    // A configured override must be a plain http(s) loopback URL: userinfo would smuggle credentials
    // into the self-call, and a non-http(s) or non-loopback target is out of contract.
    public static bool TrySanitizeConfiguredBase(string? configured, out string configuredBase)
    {
        configuredBase = "";
        if (string.IsNullOrWhiteSpace(configured)) return false;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.UserInfo.Length > 0 || !uri.IsLoopback) return false;
        configuredBase = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static string BuildRelativeUri(DataSourceDef def, int top, int skip, string? search, string? status)
    {
        if (!def.IsOData) return def.EndpointOrEntity;

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
            var field = def.Name == "opportunities" ? "Stage" : def.Name == "sales" ? "PaymentStatus" : "Status";
            filters.Add($"{field} eq '{safeStatus}'");
        }
        if (filters.Count > 0) qs["$filter"] = string.Join(" and ", filters);
        return $"/odata/{def.EndpointOrEntity}?{qs}";
    }

    private static void ForwardSafeHeaders(HttpContext httpCtx, HttpRequestMessage req)
    {
        req.Headers.Accept.TryParseAdd("application/json");
        foreach (var header in httpCtx.Request.Headers)
        {
            if (!AllowedForwardedHeaders.Contains(header.Key)) continue;
            req.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    private static bool TryReadValidatedParam(JsonElement args, string prop, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!args.TryGetProperty(prop, out var el) || el.ValueKind == JsonValueKind.Null) return true;
        if (el.ValueKind != JsonValueKind.String) { error = $"Parameter '{prop}' must be a string."; return false; }

        var raw = el.GetString()?.Trim();
        if (string.IsNullOrEmpty(raw)) return true;
        if (raw.Length > MaxParamLength || raw.Any(char.IsControl))
        {
            error = $"Parameter '{prop}' is invalid or too long.";
            return false;
        }
        value = raw;
        return true;
    }

    private static async Task<string?> ReadLimitedUtf8Async(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            total += read;
            if (total > maxBytes) return null;
            ms.Write(buf, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ProjectResult(DataSourceDef def, string rawJson, int skip)
    {
        if (!def.IsOData)
        {
            try
            {
                var node = JsonNode.Parse(rawJson);
                var wrapped = JsonSerializer.Serialize(new { ok = true, source = def.Name, data = node });
                return wrapped.Length > MaxResponseChars
                    ? JsonSerializer.Serialize(new { ok = false, error = "The result is too large." })
                    : wrapped;
            }
            catch (JsonException)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "Invalid JSON from data endpoint." });
            }
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawJson); }
        catch (JsonException) { return JsonSerializer.Serialize(new { ok = false, error = "Invalid JSON from OData endpoint." }); }
        using var _ = doc;
        var root = doc.RootElement;

        int? totalCount = root.TryGetProperty("@odata.count", out var countEl) && countEl.TryGetInt32(out var c) ? c : null;
        var rows = new JsonArray();
        if (root.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueEl.EnumerateArray()) rows.Add(JsonNode.Parse(item.GetRawText()));
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
        while (json.Length > MaxResponseChars && rows.Count > 0)
        {
            rows.RemoveAt(rows.Count - 1);
            wrapper["rowsReturned"] = rows.Count;
            wrapper["moreRows"] = true;
            wrapper["countIsCapped"] = true;
            json = wrapper.ToJsonString();
        }
        return json.Length > MaxResponseChars
            ? JsonSerializer.Serialize(new { ok = false, error = "The result is too large." })
            : json;
    }

    private sealed record DataSourceDef(string Name, string EndpointOrEntity, bool IsOData, string? SearchField, string Description);
}
