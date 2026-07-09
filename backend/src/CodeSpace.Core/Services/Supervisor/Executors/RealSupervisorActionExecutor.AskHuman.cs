using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The ASK-HUMAN half of the real executor (PR-E E4, Rule 10 <c>.AskHuman.cs</c>): a THIRD park path beside
/// spawn's AgentRun barrier + plan/merge's self-advance. The supervisor pauses MID-LOOP for a human answer.
///
/// <para>The decision posts a question CARD (an <c>action_buttons</c> "Answer" affordance that requires a
/// comment — the human's free-text answer) into the supervisor run's OWN team conversation, carrying a fresh
/// per-turn correlation token. The node then parks on a SINGLE <c>Action</c> wait keyed
/// <c>&lt;nodeId&gt;#turn{N}#ask</c> — NOT the wait-for-all agent barrier, NOT a self-advance. The human's
/// answer rides the EXISTING <c>ResumeByActionTokenAsync</c> path (team-scoped, authenticated responder), which
/// resolves the wait + re-dispatches; the supervisor re-enters and the rehydrate folds the answer into the next
/// turn's context (so the decider sees "you asked X, the human answered Y").</para>
///
/// <para>EXACTLY-ONCE — the card is posted at most once across replays: the recorded outcome stores the token,
/// and a crash that left the spawn-style decision stuck Running is recovered by the re-entry anchor — an
/// EXISTING pending Action wait keyed to this turn means a prior pass already posted + parked, so we re-derive
/// the SAME token + re-park WITHOUT re-posting (mirrors spawn's <c>ReparkOnExistingWaits</c>). The node only
/// reaches an ask_human turn with no pending agent waits, and the Action wait key is per-turn, so an existing
/// one here is necessarily THIS decision's crash residue.</para>
///
/// <para>Tenancy: the card is posted into the run's OWN team conversation (the id is carried from node config,
/// asserted to belong to the run's team via <see cref="Chat.IChatBotService.ConversationBelongsToTeamAsync"/>
/// before posting — never model-supplied, never cross-team). No authored / cross-team / unknown conversation →
/// the turn DEGRADES to a synchronous no-surface stop-style outcome (self-advance), never a crash and never a
/// hang on a card no one can see.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    /// <summary>Ask the human: post the question card + park on the single Action wait; or, on a crash-recovery re-entry where this turn's wait already exists, EITHER re-park on the still-pending wait (no double-ask) OR — when the human already answered while the decision was stuck non-terminal — fold that answer in + self-advance (never re-park on a Resolved wait that will never be resumed again); or degrade when no usable conversation is authored.</summary>
    private async Task<SupervisorExecution> ExecuteAskHumanAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var ask = Deserialize<SupervisorAskHumanPayload>(decision.PayloadJson) ?? new SupervisorAskHumanPayload { Question = "" };

        var existing = await ExistingTurnAskWaitAsync(context, cancellationToken).ConfigureAwait(false);

        if (existing is { } wait)
            return wait.Status == WorkflowWaitStatuses.Resolved
                ? AdvanceWithResolvedAnswer(context, ask.Question, wait.Token, wait.PayloadJson)
                : ReparkOnExistingAsk(context, ask.Question, wait.Token);

        if (string.IsNullOrWhiteSpace(ask.Question))
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(RejectedAskHumanOutcome, AgentJson.Options));

        if (!await CanPostToConversationAsync(context, cancellationToken).ConfigureAwait(false))
            return DegradeNoSurface(ask.Question);

        return await PostQuestionAndParkAsync(ask.Question, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Post the question card (single "Answer" button requiring the human's free-text comment) + stage the per-turn Action wait, then record the token + question in the outcome. The node parks on the one wait; the human's answer resumes it.</summary>
    private async Task<SupervisorExecution> PostQuestionAndParkAsync(string question, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");

        var interaction = BuildQuestionCard(token);

        var posted = await _bot.PostAsBotAsync(context.ConversationId!.Value, QuestionBody(question), interaction, cancellationToken).ConfigureAwait(false);

        StageAskWait(context, token);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Supervisor posted an ask_human question (message {MessageId}) at turn {Turn} on node {NodeId}; parking on the human's answer", posted.Id, context.TurnNumber, context.NodeId);

        return SupervisorExecution.ParkedOnHuman(AskOutcome(question, token, answer: null), token);
    }

    /// <summary>Re-derive the SAME park-on-human classification from the token a prior crashed pass already posted — re-park WITHOUT re-posting the card (crash recovery, no double-ask).</summary>
    private SupervisorExecution ReparkOnExistingAsk(SupervisorTurnContext context, string question, string token)
    {
        _logger.LogInformation("Supervisor re-parking on the ask_human wait already posted at turn {Turn} on node {NodeId} (crash recovery — no duplicate question)", context.TurnNumber, context.NodeId);

        return SupervisorExecution.ParkedOnHuman(AskOutcome(question, token, answer: null), token);
    }

    /// <summary>
    /// Crash-recovery where THIS turn's ask wait already exists AND is RESOLVED: the human answered while the
    /// decision was stuck non-terminal (a crash landed after the card+wait committed but before the terminal
    /// record, then the answer resolved the wait before the run was re-dispatched). Re-parking here would suspend
    /// on an already-Resolved wait that the resume path will never fire again → permanent hang; recording
    /// <c>answer:null</c> would clobber the human's durable answer. Instead fold the answer into the outcome +
    /// self-advance — the next turn's decider proceeds with "you asked X, the human answered Y" in context.
    /// </summary>
    private SupervisorExecution AdvanceWithResolvedAnswer(SupervisorTurnContext context, string question, string token, string? waitPayloadJson)
    {
        var answer = SupervisorOutcome.ReadAnswerComment(waitPayloadJson);

        _logger.LogInformation("Supervisor ask_human wait at turn {Turn} on node {NodeId} was already resolved during a crash-recovery re-entry — folding the human's answer + self-advancing (no re-park on the resolved wait)", context.TurnNumber, context.NodeId);

        return SupervisorExecution.Synchronous(AskOutcome(question, token, answer));
    }

    /// <summary>
    /// P0-2 (action schema validation): the ask_human payload named no question — either the model omitted the
    /// <c>askHuman</c> sub-object entirely (schema-legal; only <c>kind</c> is root-required) or supplied a blank
    /// <c>question</c> (the schema places no <c>minLength</c> on it). Checked BEFORE the tenancy/post step so a
    /// blank question is REJECTED rather than posting a boilerplate card and spending a real human interaction on
    /// nothing — strictly worse than a spawn no-op, since it can't be self-corrected until the human replies.
    /// </summary>
    internal static readonly object RejectedAskHumanOutcome = new
    {
        askHuman = "rejected",
        reason = "the ask_human decision carried no question text",
    };

    /// <summary>No usable conversation (none authored, or the tenancy check failed) → degrade to a SYNCHRONOUS no-surface outcome so the node self-advances rather than hanging on a card no one can answer.</summary>
    private SupervisorExecution DegradeNoSurface(string question)
    {
        _logger.LogWarning("Supervisor ask_human has no usable team conversation to post into — degrading to a no-surface synchronous outcome (self-advance)");

        var outcome = JsonSerializer.Serialize(new { question, askHuman = "no-conversation", answer = (string?)null }, AgentJson.Options);

        return SupervisorExecution.Synchronous(outcome);
    }

    /// <summary>True only when a conversation is authored AND belongs to the run's team (tenancy guard — never post a card into, or auto-join the bot to, a foreign / unknown conversation named in node config).</summary>
    private async Task<bool> CanPostToConversationAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
        context.ConversationId is { } conversationId
        && await _bot.ConversationBelongsToTeamAsync(conversationId, context.TeamId, cancellationToken).ConfigureAwait(false);

    /// <summary>This turn's already-staged ask_human Action wait (token + status + resolved payload), or null when none — the recovery anchor for a crash AFTER the card + wait committed but before the terminal was recorded. The status distinguishes a still-Pending wait (re-park, no double-ask) from one the human already Resolved (fold the answer + self-advance — never re-park on a wait the resume path won't fire again).</summary>
    private async Task<ExistingAskWait?> ExistingTurnAskWaitAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == context.SupervisorRunId && w.NodeId == context.NodeId
                        && w.WaitKind == WorkflowWaitKinds.Action && w.IterationKey == SupervisorOutcome.HumanWaitKey(context.NodeId, context.TurnNumber))
            .Select(w => new ExistingAskWait(w.Token, w.Status, w.PayloadJson))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>This turn's existing ask_human Action wait, read on a crash-recovery re-entry: the correlation token, the wait status (Pending → re-park; Resolved → fold + self-advance), and the resolved payload (the human's <c>{ action, by, comment }</c> answer, null while Pending). A reference type so an empty <c>FirstOrDefaultAsync</c> projection reads as null (a struct would project to a non-null default).</summary>
    private sealed record ExistingAskWait(string Token, string Status, string? PayloadJson);

    /// <summary>Stage the single Action wait the ask_human turn parks on, keyed <c>&lt;nodeId&gt;#turn{N}#ask</c>. Token = the card's correlation token (the human's answer via ResumeByActionTokenAsync resolves the wait by it). Distinct per turn → no collision with a later ask_human.</summary>
    private void StageAskWait(SupervisorTurnContext context, string token)
    {
        _db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = context.SupervisorRunId,
            NodeId = context.NodeId,
            IterationKey = SupervisorOutcome.HumanWaitKey(context.NodeId, context.TurnNumber),
            WaitKind = WorkflowWaitKinds.Action,
            Token = token,
            Status = WorkflowWaitStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>The question card: a single "Answer" action button that REQUIRES a comment — so the click carries the human's free-text answer. Routed by a <see cref="WorkflowWaitTarget"/> (the same token-correlated routing flow.wait_action uses), first-responder-wins.</summary>
    private MessageInteraction BuildQuestionCard(string token)
    {
        var component = _components.Build(AnswerButtonConfig())
            ?? throw new InvalidOperationException("The ask_human action-buttons component factory is not registered.");

        return new MessageInteraction
        {
            Component = component,
            Target = new WorkflowWaitTarget { Token = token },
            AllowedResponderUserIds = null,   // any team member may answer (the conversation is already team-scoped)
            Resolve = new ResolvePolicy(),     // first responder's answer resolves the wait
        };
    }

    /// <summary>The single-button config the registry builds into an ActionButtonsComponent (mirrors the MCP approval card's ApprovalButtonsConfig). "Answer" requires a comment, so the human's reply IS their answer.</summary>
    private static JsonElement AnswerButtonConfig() => JsonSerializer.SerializeToElement(new
    {
        kind = "action_buttons",
        buttons = new object[]
        {
            new { key = AnswerActionKey, label = "Answer", style = "Primary", requiresComment = true },
        },
    }, AgentJson.Options);

    /// <summary>The card body shown to the human — names that the supervisor is asking, plus the question.</summary>
    private static string QuestionBody(string question) =>
        string.IsNullOrWhiteSpace(question)
            ? "The supervisor is asking for your input."
            : $"The supervisor is asking: {question}";

    /// <summary>The recorded ask_human outcome: the question, the wait token (a replay re-derives the park + token without re-posting), and the answer (null until the human replies + the fold writes it).</summary>
    private static string AskOutcome(string question, string token, string? answer) =>
        JsonSerializer.Serialize(new { question, askHumanToken = token, answer }, AgentJson.Options);

    /// <summary>The action key the single answer button sends. The human's answer is carried in the click's comment.</summary>
    public const string AnswerActionKey = "answer";
}
