using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Decisions;

/// <summary>
/// The WRITE side of the cross-grain "Needs decision" queue (Decision substrate D3b): answer ANY pending decision by
/// its queue id, routing by grain to the right durable resume mechanism WITHOUT the caller knowing which grain it is.
/// Agent-grain (a tool-ledger <c>decision.request</c> row) → <see cref="IToolCallLedgerService.TryAnswerDecisionAsync"/>
/// + wake the blocked mid-run call; node-grain (a <c>flow.decision</c> <c>WorkflowRunWait</c>) →
/// <see cref="IWorkflowResumeService.ResumeWaitAsync"/>, resuming the run from the exact node. BOTH routes are
/// resolve-once (the same status-guarded CAS the card / deadline paths use), so a queue answer racing a card click
/// leaves exactly one winner. Team-scoped: a decision outside the team is a clean not-found (no cross-team existence leak).
/// </summary>
public interface IDecisionAnswerService
{
    Task<AnswerDecisionResult> AnswerAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, Guid teamId, Guid actorUserId, CancellationToken cancellationToken);
}

public sealed class DecisionAnswerService : IDecisionAnswerService, IScopedDependency
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CodeSpaceDbContext _db;
    private readonly IToolCallLedgerService _ledger;
    private readonly IToolApprovalWaiterRegistry _waiters;
    private readonly IWorkflowResumeService _resume;
    private readonly IActorIdentityRequirementGate _identityGate;

    public DecisionAnswerService(CodeSpaceDbContext db, IToolCallLedgerService ledger, IToolApprovalWaiterRegistry waiters, IWorkflowResumeService resume, IActorIdentityRequirementGate identityGate)
    {
        _db = db;
        _ledger = ledger;
        _waiters = waiters;
        _resume = resume;
        _identityGate = identityGate;
    }

    public async Task<AnswerDecisionResult> AnswerAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        // Read each grain REGARDLESS of status (team-scoped) so an already-resolved decision is "already resolved", and
        // only a truly-absent / cross-team id is "not found" (no cross-team existence leak — neither distinguishes which).
        var agent = await ReadAgentAsync(decisionId, teamId, cancellationToken).ConfigureAwait(false);

        if (agent is not null)
        {
            if (agent.Status != ToolCallLedgerStatus.AwaitingApproval) return AnswerDecisionResult.Of(DecisionAnswerOutcome.AlreadyResolved);

            return await AnswerAgentAsync(decisionId, agent.EnvelopeJson, selectedOptions, freeText, teamId, actorUserId, cancellationToken).ConfigureAwait(false);
        }

        var node = await ReadNodeAsync(decisionId, teamId, cancellationToken).ConfigureAwait(false);

        if (node is not null)
        {
            if (node.Status != WorkflowWaitStatuses.Pending) return AnswerDecisionResult.Of(DecisionAnswerOutcome.AlreadyResolved);

            return await AnswerNodeAsync(decisionId, node.RunId, node.NodeId, node.EnvelopeJson, selectedOptions, freeText, actorUserId, cancellationToken).ConfigureAwait(false);
        }

        return AnswerDecisionResult.Of(DecisionAnswerOutcome.NotFound);
    }

    // ── Agent grain: TryAnswerDecisionAsync (the same CAS the card uses) + wake the blocked in-process call ──
    private async Task<AnswerDecisionResult> AnswerAgentAsync(Guid ledgerId, string? envelopeJson, IReadOnlyList<string> selectedOptions, string? freeText, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (!Validate(envelopeJson, selectedOptions, freeText, out var error)) return AnswerDecisionResult.Of(DecisionAnswerOutcome.Invalid, error);

        var json = BuildAnswerJson(ledgerId, selectedOptions, freeText, actorUserId);

        var won = await _ledger.TryAnswerDecisionAsync(ledgerId, teamId, json, cancellationToken).ConfigureAwait(false);

        if (!won) return AnswerDecisionResult.Of(DecisionAnswerOutcome.AlreadyResolved);   // a concurrent answer / the deadline won the CAS

        _waiters.TrySignal(ledgerId, ToolApprovalOutcome.Approved);   // wake the blocked mid-run call (in-process fast-path; the durable row is the authority)

        return AnswerDecisionResult.Of(DecisionAnswerOutcome.Answered);
    }

    // ── Node grain: resume the parked run from the exact flow.decision node ──
    private async Task<AnswerDecisionResult> AnswerNodeAsync(Guid waitId, Guid runId, string nodeId, string? envelopeJson, IReadOnlyList<string> selectedOptions, string? freeText, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (!Validate(envelopeJson, selectedOptions, freeText, out var error)) return AnswerDecisionResult.Of(DecisionAnswerOutcome.Invalid, error);

        // Same act-as-user pre-flight the chat-Action resume path applies (ResumeByActionTokenAsync): if resolving this
        // decision makes a DOWNSTREAM node act AS the answerer on a provider they haven't linked (or lack repo access
        // for), refuse UP FRONT (throws ActorIdentityRequiredException → 428 / ActorRepoPermissionDeniedException → 403,
        // mapped by the global filter) instead of the run failing later in the background. Runs BEFORE the resume, so the
        // run stays parked for the retry. Agent-grain decisions have no downstream workflow node, so this is node-only.
        await _identityGate.EnsureResponderCanActAsUserAsync(runId, nodeId, actorUserId, cancellationToken).ConfigureAwait(false);

        var json = BuildAnswerJson(waitId, selectedOptions, freeText, actorUserId);

        var resolved = await _resume.ResumeWaitAsync(runId, waitId, json, cancellationToken).ConfigureAwait(false);

        return AnswerDecisionResult.Of(resolved ? DecisionAnswerOutcome.Answered : DecisionAnswerOutcome.AlreadyResolved);
    }

    private async Task<AgentDecision?> ReadAgentAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.Id == decisionId && l.TeamId == teamId && l.ToolKind == DecisionToolKinds.DecisionRequest)
            .Select(l => new AgentDecision(l.Status, l.DecisionEnvelopeJson))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<NodeDecision?> ReadNodeAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.Id == decisionId && w.WaitKind == WorkflowWaitKinds.Decision
                && _db.WorkflowRun.Any(r => r.Id == w.RunId && r.TeamId == teamId))
            .Select(w => new NodeDecision(w.Status, w.RunId, w.NodeId, w.PayloadJson))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private static string BuildAnswerJson(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, Guid actorUserId) =>
        JsonSerializer.Serialize(new DecisionAnswer
        {
            DecisionId = decisionId,
            AnsweredBy = DecisionAnsweredByKinds.Human,
            SelectedOptions = selectedOptions,
            FreeText = string.IsNullOrWhiteSpace(freeText) ? null : freeText,
            AnsweredByUserId = actorUserId,
        }, Json);

    /// <summary>
    /// Defense-in-depth validation against the stashed envelope (the resume mechanisms are tolerant, but a clearly-wrong
    /// answer should be rejected up front): an option-bearing decision needs at least one chosen option, all of which
    /// must be real option ids; an option-less (free-text) decision needs non-empty free text. A missing / unreadable
    /// envelope passes (don't block a legitimate answer on a projection quirk). Internal for direct unit testing.
    /// </summary>
    internal static bool Validate(string? envelopeJson, IReadOnlyList<string> selectedOptions, string? freeText, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(envelopeJson)) return true;

        DecisionRequest? env;
        try { env = JsonSerializer.Deserialize<DecisionRequest>(envelopeJson, Json); }
        catch (JsonException) { return true; }

        if (env is null) return true;

        if (env.Options.Count > 0)
        {
            if (selectedOptions.Count == 0) { error = "this decision requires choosing at least one option."; return false; }

            var ids = env.Options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            var unknown = selectedOptions.FirstOrDefault(s => !ids.Contains(s));

            if (unknown is not null) { error = $"'{unknown}' is not one of this decision's options."; return false; }

            return true;
        }

        if (string.IsNullOrWhiteSpace(freeText)) { error = "this decision requires a free-text answer."; return false; }

        return true;
    }

    private sealed record AgentDecision(ToolCallLedgerStatus Status, string? EnvelopeJson);

    private sealed record NodeDecision(string Status, Guid RunId, string NodeId, string? EnvelopeJson);
}
