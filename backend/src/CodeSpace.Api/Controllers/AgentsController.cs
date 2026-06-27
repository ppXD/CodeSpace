using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the team-scoped Agents library (Agent personas). List / get / create / update /
/// delete. Team scope comes from <c>X-Team-Id</c>; the MediatR pipeline behaviour vets membership
/// before the handler runs. Records bind directly (Rule 17) — route ids merge in via <c>with</c>.
/// </summary>
[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AgentsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListAgentDefinitionsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Every harness registered in the engine (deployment-level, team-agnostic) — feeds the agent node's harness picker + per-harness model suggestions.</summary>
    [HttpGet("harnesses")]
    public async Task<IActionResult> ListHarnesses(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListHarnessesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Live status of one agent run (team-scoped) — the run-detail's status header + poll-while-active signal.</summary>
    [HttpGet("runs/{agentRunId:guid}")]
    public async Task<IActionResult> GetRun([FromRoute] Guid agentRunId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetAgentRunQuery { AgentRunId = agentRunId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The agent run's live log, only steps after <paramref name="after"/> (0 = whole log) — the incremental cursor the timeline streams with.</summary>
    [HttpGet("runs/{agentRunId:guid}/events")]
    public async Task<IActionResult> ListRunEvents([FromRoute] Guid agentRunId, [FromQuery] long after, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListAgentRunEventsQuery { AgentRunId = agentRunId, AfterSequence = after }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The run's governed (side-effecting) tool-call audit — what tool, when, the outcome, and the approval trail (team-scoped). Read-only tools are absent (they skip the ledger).</summary>
    [HttpGet("runs/{agentRunId:guid}/tool-calls")]
    public async Task<IActionResult> ListToolCalls([FromRoute] Guid agentRunId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListToolCallsQuery { AgentRunId = agentRunId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The team's agent-run scorecard — per-harness + overall success rate and latency (P50/P95) over its terminal runs. Optional since/harness filters narrow the window. Team-scoped (the team is the X-Team-Id header, never the query string).</summary>
    [HttpGet("scorecard")]
    public async Task<IActionResult> GetScorecard([FromQuery] GetAgentScorecardQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The team's supervisor-run scorecard — the cross-run roll-up (avg decisions/replan rounds, overall ground-truth spawn success, outcome distribution) + recent per-run scores over the durable supervisor lane. Optional since filter windows the trend. Team-scoped (the team is the X-Team-Id header, never the query string); empty when no supervisor runs exist.</summary>
    [HttpGet("supervisor-scorecard")]
    public async Task<IActionResult> GetSupervisorScorecard([FromQuery] GetSupervisorScorecardQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The team's token + estimated-USD spend roll-up over its agent runs, with an optional since window — the auditable bill over the previously-dead TokenUsage. Team-scoped (the team is the X-Team-Id header, never the query string); runs with no captured usage or an unpriced model are surfaced as unknown-cost, not silently undercounted.</summary>
    [HttpGet("cost")]
    public async Task<IActionResult> GetCost([FromQuery] GetTeamCostRollupQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("{agentDefinitionId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetAgentDefinitionQuery { AgentDefinitionId = agentDefinitionId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAgentDefinitionCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { agentDefinitionId = id }, new { id });
    }

    [HttpPut("{agentDefinitionId:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid agentDefinitionId, [FromBody] UpdateAgentDefinitionCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { AgentDefinitionId = agentDefinitionId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("{agentDefinitionId:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteAgentDefinitionCommand { AgentDefinitionId = agentDefinitionId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Dry-run: discover + parse the agents in a bound repository's pack (no persistence) so the operator can inspect + select before importing.</summary>
    [HttpPost("import-preview")]
    public async Task<IActionResult> ImportPreview([FromBody] PreviewAgentPackQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Dry-run: clone a pack from a git URL (host-allowlist-guarded) and discover its agents AND skills (no persistence) so the operator can inspect + select before importing.</summary>
    [HttpPost("import-preview-url")]
    public async Task<IActionResult> ImportPreviewFromUrl([FromBody] PreviewPackFromUrlQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Commit the selected agents from a previewed pack — returns a per-path outcome (imported / skipped / failed).</summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportAgentPackCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
