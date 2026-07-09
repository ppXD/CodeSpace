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

    /// <summary>Per-agent run stats — one row per persona with runs: its recent-outcome sparkline, windowed success rate, latency, spend, and last-active stamp. The evidence the Agents roster shows on each agent row. Optional since window narrows the horizon. Team-scoped (the team is the X-Team-Id header, never the query string).</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats([FromQuery] GetAgentStatsQuery query, CancellationToken cancellationToken)
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

    /// <summary>The team's unattended-delivery scorecard — the path-to-intelligence north-star ("task in → merged/published artifact out with zero human touches") over every terminal run, single-agent or supervisor-orchestrated alike. Optional since filter windows the trend. Team-scoped (the team is the X-Team-Id header, never the query string); empty when no terminal runs exist.</summary>
    [HttpGet("unattended-delivery-scorecard")]
    public async Task<IActionResult> GetUnattendedDeliveryScorecard([FromQuery] GetUnattendedDeliveryScorecardQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The team's premature-stop-rate report (P4) — the stability north-star: of the task runs it started (single-agent, plan-map, or supervisor alike), what fraction died prematurely rather than reaching a genuine conclusion. DELIBERATELY includes runs that haven't finished yet (never silently excluded); a run stuck for too long is surfaced as a loud, separate figure. Optional since filter windows the trend. Team-scoped (the team is the X-Team-Id header, never the query string).</summary>
    [HttpGet("premature-stop-rate")]
    public async Task<IActionResult> GetPrematureStopRate([FromQuery] GetPrematureStopRateQuery query, CancellationToken cancellationToken)
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

    /// <summary>Instantiate a new working bench persona by copying a Library store snapshot (the New-agent "from Library" path). Returns the new persona id.</summary>
    [HttpPost("from-store")]
    public async Task<IActionResult> InstantiateFromStore([FromBody] InstantiateAgentFromStoreCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { agentDefinitionId = id }, new { id });
    }

    /// <summary>Author a new agent directly INTO the Library (a store entry under the team's Custom pack), not onto the bench. Returns the new id.</summary>
    [HttpPost("library")]
    public async Task<IActionResult> AuthorIntoLibrary([FromBody] AuthorStoreAgentCommand command, CancellationToken cancellationToken)
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

    /// <summary>Replace the skills bound to a persona (full-replace). The route id is authoritative (Rule 17).</summary>
    [HttpPut("{agentDefinitionId:guid}/skills")]
    public async Task<IActionResult> SetSkills([FromRoute] Guid agentDefinitionId, [FromBody] SetAgentSkillsCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { AgentDefinitionId = agentDefinitionId }, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Commit the selected agents AND skills from a previewed URL pack — clones the URL (allowlist-guarded), persists each under a resolved pack, and returns a per-path outcome (imported / updated / skipped / failed). Idempotent: a re-run upserts rather than duplicating.</summary>
    [HttpPost("import-url")]
    public async Task<IActionResult> ImportFromUrl([FromBody] ImportPackFromUrlCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
