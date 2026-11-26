using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MudBlazorCrmApp.Data;
using MudBlazorCrmApp.Shared.Models;

namespace MudBlazorCrmApp.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
[EnableRateLimiting("Fixed")]
public class SearchController(ApplicationDbContext _ctx) : ControllerBase
{
    private readonly ApplicationDbContext ctx = _ctx;

    [HttpGet("")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SearchResultDto>> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(new SearchResultDto());

        var query = q.Trim().ToLower();
        if (query.Length > 100) query = query[..100];

        var customers = await ctx.Customer
            .AsNoTracking()
            .Where(c => c.Name != null && c.Name.ToLower().Contains(query))
            .Take(5)
            .Select(c => new SearchResultItem { Id = c.Id, Name = c.Name, EntityType = "Customer", Detail = c.Industry })
            .ToListAsync(ct);

        var contacts = await ctx.Contact
            .AsNoTracking()
            .Where(c => (c.Name != null && c.Name.ToLower().Contains(query)) || (c.Email != null && c.Email.ToLower().Contains(query)))
            .Take(5)
            .Select(c => new SearchResultItem { Id = c.Id, Name = c.Name, EntityType = "Contact", Detail = c.Email })
            .ToListAsync(ct);

        var opportunities = await ctx.Opportunity
            .AsNoTracking()
            .Where(o => o.Name != null && o.Name.ToLower().Contains(query))
            .Take(5)
            .Select(o => new SearchResultItem { Id = o.Id, Name = o.Name, EntityType = "Opportunity", Detail = o.Stage })
            .ToListAsync(ct);

        var leads = await ctx.Lead
            .AsNoTracking()
            .Where(l => l.Contact != null && l.Contact.Name != null && l.Contact.Name.ToLower().Contains(query))
            .Take(5)
            .Select(l => new SearchResultItem { Id = l.Id, Name = l.Contact != null ? l.Contact.Name : "Unknown", EntityType = "Lead", Detail = l.Status })
            .ToListAsync(ct);

        return Ok(new SearchResultDto { Customers = customers, Contacts = contacts, Opportunities = opportunities, Leads = leads });
    }
}
