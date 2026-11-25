using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MudBlazorCrmApp.Services.Agent;

namespace MudBlazorCrmApp.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AgentController(
    AgentService agentService,
    IOptions<AgentOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AgentStatusModel> Status()
    {
        return Ok(new AgentStatusModel { UseAgent = optionsAccessor.Value.Enabled });
    }

    [HttpPost("step")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentStepResponseModel>> StepAsync(
        [FromBody] AgentStepRequestModel request,
        CancellationToken cancellationToken)
    {
        if (!optionsAccessor.Value.Enabled)
            return NotFound();

        var userKey = GetUserKey();
        var response = await agentService.StepAsync(request, userKey, cancellationToken);
        return Ok(response);
    }

    [HttpPost("look")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentLookResponseModel>> LookAsync(
        [FromBody] AgentLookRequestModel request,
        CancellationToken cancellationToken)
    {
        if (!optionsAccessor.Value.Enabled)
            return NotFound();

        var userKey = GetUserKey();
        var response = await agentService.LookAsync(request, userKey, cancellationToken);
        return Ok(response);
    }

    private string GetUserKey()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? User.Identity?.Name
               ?? HttpContext.Connection.RemoteIpAddress?.ToString()
               ?? "anonymous";
    }
}
