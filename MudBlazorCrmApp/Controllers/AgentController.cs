using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MudBlazorCrmApp.Services.Agent;

namespace MudBlazorCrmApp.Controllers;

[Authorize]
[Route("api/[controller]")]
[ApiController]
public class AgentController(
    AgentService agentService,
    IOptions<AgentOptions> optionsAccessor) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AgentStatusModel> Status()
    {
        var opts = optionsAccessor.Value;
        opts.Normalize();
        return Ok(new AgentStatusModel
        {
            UseAgent = opts.Enabled,
            MaxTaskSteps = opts.MaxTaskSteps,
            MaxPriorTurns = opts.MaxPriorTurns,
            MaxTaskModelCalls = opts.MaxTaskModelCalls,
            MaxTaskCostUsd = opts.MaxTaskCostUsd,
            ShowCost = opts.ShowCost,
            Vision = opts.Vision
        });
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

        var userKey = User.GetAgentUserKey();
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

        var userKey = User.GetAgentUserKey();
        var response = await agentService.LookAsync(request, userKey, cancellationToken);
        return Ok(response);
    }
}
