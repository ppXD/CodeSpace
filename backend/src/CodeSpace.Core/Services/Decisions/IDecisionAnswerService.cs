using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
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

    /// <summary>
    /// Answer a decision AS THE SUPERVISOR ARBITER (Decision substrate D4) — the auto-answer the arbiter writes when it
    /// decides a low/med-risk decision itself rather than escalating to a human. Stamps <c>AnsweredBy=supervisor</c> + a
    /// REQUIRED rationale (AC3 — an auto-answer is never silent) + no user id, and SKIPS the act-as-user identity gate (a
    /// supervisor is not a human acting-as-user). DEFENSE-IN-DEPTH: re-runs the fail-closed floor on the stashed envelope
    /// and returns <see cref="DecisionAnswerOutcome.RequiresHuman"/> if the decision is human-only — the supervisor can
    /// NEVER auto-resolve what the floor reserves for a person, even if the arbiter's own check is wrong.
    /// </summary>
    Task<AnswerDecisionResult> AnswerAsSupervisorAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, string rationale, Guid teamId, CancellationToken cancellationToken);
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

    public Task<AnswerDecisionResult> AnswerAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, Guid teamId, Guid actorUserId, CancellationToken cancellationToken) =>
        AnswerCoreAsync(decisionId, selectedOptions, freeText, DecisionAuthor.Human(actorUserId), teamId, cancellationToken);

    public Task<AnswerDecisionResult> AnswerAsSupervisorAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, string rationale, Guid teamId, CancellationToken cancellationToken) =>
        // AC3 — a non-human answer is NEVER silent: a blank rationale is rejected up front (the C# string type is only a
        // compile-time hint; "" / "   " / null! would otherwise persist a silent auto-answer).
        string.IsNullOrWhiteSpace(rationale)
            ? Task.FromResult(AnswerDecisionResult.Of(DecisionAnswerOutcome.Invalid, "a supervisor answer requires a non-empty rationale (AC3 — an auto-answer is never silent)."))
            : AnswerCoreAsync(decisionId, selectedOptions, freeText, DecisionAuthor.Supervisor(rationale), teamId, cancellationToken);

    private async Task<AnswerDecisionResult> AnswerCoreAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, DecisionAuthor author, Guid teamId, CancellationToken cancellationToken)
    {
        // Read each grain REGARDLESS of status (team-scoped) so an already-resolved decision is "already resolved", and
        // only a truly-absent / cross-team id is "not found" (no cross-team existence leak — neither distinguishes which).
        var agent = await ReadAgentAsync(decisionId, teamId, cancellationToken).ConfigureAwait(false);

        if (agent is not null)
        {
            if (agent.Status != ToolCallLedgerStatus.AwaitingApproval) return AnswerDecisionResult.Of(DecisionAnswerOutcome.AlreadyResolved);

            return await AnswerAgentAsync(decisionId, agent.EnvelopeJson, selectedOptions, freeText, author, teamId, cancellationToken).ConfigureAwait(false);
        }

        var node = await ReadNodeAsync(decisionId, teamId, cancellationToken).ConfigureAwait(false);

        if (node is not null)
        {
            if (node.Status != WorkflowWaitStatuses.Pending) return AnswerDecisionResult.Of(DecisionAnswerOutcome.AlreadyResolved);

            return await AnswerNodeAsync(decisionId, node.RunId, node.NodeId, node.EnvelopeJson, selectedOptions, freeText, author, cancellationToken).ConfigureAwait(false);
        }

        return AnswerDecisionResult.Of(DecisionAnswerOutcome.NotFound);
    }

    // ── Agent grain: TryAnswerDecisionAsync (the same CAS the card uses) + wake the blocked in-process call ──
    private async Task<AnswerDecisionResult> AnswerAgentAsync(Guid ledgerId, string? envelopeJson, IReadOnlyList<string> selectedOptions, string? freeText, DecisionAuthor author, Guid teamId, CancellationToken cancellationToken)
    {
        if (Floored(author, envelopeJson)) return AnswerDecisionResult.Of(DecisionAnswerOutcome.RequiresHuman);

        if (!Validate(envelopeJson, selectedOptions, freeText, out var error)) return AnswerDecisionResult.Of(DecisionAnswerOutcome.Invalid, error);

        var json = BuildAnswerJson(ledgerId, selectedOptions, freeText, author);

        var won = await _ledger.TryAnswerDecisionAsync(ledgerId, teamId, json, cancellationToken).ConfigureAwait(false);

        if (!won) return AnswerDecisionResult.Of(DecisionAnswerOutcome.AlreadyResolved);   // a concurrent answer / the deadline won the CAS

        _waiters.TrySignal(ledgerId, ToolApprovalOutcome.Approved);   // wake the blocked mid-run call (in-process fast-path; the durable row is the authority)

        return AnswerDecisionResult.Of(DecisionAnswerOutcome.Answered);
    }

    // ── Node grain: resume the parked run from the exact flow.decision node ──
    private async Task<AnswerDecisionResult> AnswerNodeAsync(Guid waitId, Guid runId, string nodeId, string? envelopeJson, IReadOnlyList<string> selectedOptions, string? freeText, DecisionAuthor author, CancellationToken cancellationToken)
    {
        if (Floored(author, envelopeJson)) return AnswerDecisionResult.Of(DecisionAnswerOutcome.RequiresHuman);

        if (!Validate(envelopeJson, selectedOptions, freeText, out var error)) return AnswerDecisionResult.Of(DecisionAnswerOutcome.Invalid, error);

        // Same act-as-user pre-flight the chat-Action resume path applies (ResumeByActionTokenAsync): if resolving this
        // decision makes a DOWNSTREAM node act AS the answerer on a provider they haven't linked (or lack repo access
        // for), refuse UP FRONT (throws ActorIdentityRequiredException → 428 / ActorRepoPermissionDeniedException → 403,
        // mapped by the global filter) instead of the run failing later in the background. Runs BEFORE the resume, so the
        // run stays parked for the retry. ONLY for a HUMAN author — a supervisor isn't acting-as-user. Agent-grain
        // decisions have no downstream workflow node, so this is node-only.
        if (author.IsHuman) await _identityGate.EnsureResponderCanActAsUserAsync(runId, nodeId, author.UserId!.Value, cancellationToken).ConfigureAwait(false);

        var json = BuildAnswerJson(waitId, selectedOptions, freeText, author);

        var resolved = await _resume.ResumeWaitAsync(runId, waitId, json, cancellationToken).ConfigureAwait(false);

        return AnswerDecisionResult.Of(resolved ? DecisionAnswerOutcome.Answered : DecisionAnswerOutcome.AlreadyResolved);
    }

    /// <summary>
    /// Defense-in-depth: a NON-human author may answer ONLY a decision whose EFFECTIVE policy is auto / supervisor —
    /// never one the floor reserves for a person, whether by a danger SIGNAL (approve_action / side-effecting / high-risk
    /// / no recommendation-or-reason) OR by an explicitly-declared <c>human_required</c> ("no auto / supervisor answer").
    /// Re-derived from the stashed envelope here so a wrong arbiter check can't slip a human-only decision through. A
    /// missing / unreadable envelope can't be proven safe, so it floors too. Always false for a human author (a human
    /// answers what the floor reserves for them — that is the point).
    /// </summary>
    private static bool Floored(DecisionAuthor author, string? envelopeJson)
    {
        if (author.IsHuman) return false;

        var env = TryDeserializeEnvelope(envelopeJson);

        return env is null || DecisionPolicyFloor.Effective(env) == DecisionPolicies.HumanRequired;
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

    private static string BuildAnswerJson(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, DecisionAuthor author) =>
        JsonSerializer.Serialize(new DecisionAnswer
        {
            DecisionId = decisionId,
            AnsweredBy = author.AnsweredBy,
            SelectedOptions = selectedOptions,
            FreeText = string.IsNullOrWhiteSpace(freeText) ? null : freeText,
            Rationale = author.Rationale,           // REQUIRED for a non-human answer (AC3); null for a human
            AnsweredByUserId = author.UserId,
        }, Json);

    private static DecisionRequest? TryDeserializeEnvelope(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson)) return null;

        try { return JsonSerializer.Deserialize<DecisionRequest>(envelopeJson, Json); }
        catch (JsonException) { return null; }
    }

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

    /// <summary>Who is recording an answer — the one axis that differs between a human (from the queue / card) and the supervisor arbiter (D4). A human carries a user id + no rationale (+ the act-as-user gate); the supervisor carries a required rationale + no user id (+ no gate, + the floor re-check). Keeps the routing one shared path.</summary>
    private sealed record DecisionAuthor(string AnsweredBy, Guid? UserId, string? Rationale)
    {
        public static DecisionAuthor Human(Guid userId) => new(DecisionAnsweredByKinds.Human, userId, null);

        public static DecisionAuthor Supervisor(string rationale) => new(DecisionAnsweredByKinds.Supervisor, null, rationale);

        public bool IsHuman => AnsweredBy == DecisionAnsweredByKinds.Human;
    }
}
