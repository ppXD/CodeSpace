using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Chat;

public sealed class MessageInteractionService : IMessageInteractionService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowResumeService _resume;
    private readonly IResolvePolicyEvaluator _resolvePolicy;
    private readonly IToolCallApprovalResolver _toolApprovals;
    private readonly IDecisionRequestResolver _decisions;

    public MessageInteractionService(CodeSpaceDbContext db, IWorkflowResumeService resume, IResolvePolicyEvaluator resolvePolicy, IToolCallApprovalResolver toolApprovals, IDecisionRequestResolver decisions)
    {
        _db = db;
        _resume = resume;
        _resolvePolicy = resolvePolicy;
        _toolApprovals = toolApprovals;
        _decisions = decisions;
    }

    public async Task RespondAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken cancellationToken)
    {
        var message = await LoadMessageAsync(teamId, messageId, cancellationToken).ConfigureAwait(false);

        var interaction = MessageInteractionJson.Deserialize(message.InteractionJson)
            ?? throw new KeyNotFoundException($"Message {messageId} has no interaction to respond to.");

        EnsureOpen(interaction);

        // A comment is non-terminal discussion — any conversation member, repeatable, never resolves the
        // interaction. It just appends to the log + leaves the card Open (a living thread).
        if (MessageInteractionPolicy.IsComment(responseKey))
        {
            await AddCommentAsync(message, interaction, actorUserId, comment, teamId, cancellationToken).ConfigureAwait(false);
            return;
        }

        EnsureValidResponse(interaction, responseKey);
        EnsureCommentIfRequired(interaction, responseKey, comment);
        EnsureRequiredFields(interaction, values);
        await EnsureAllowedResponderAsync(teamId, message.ConversationId, interaction, actorUserId, cancellationToken).ConfigureAwait(false);

        await ResolveOrRecordVoteAsync(message, interaction, responseKey, actorUserId, comment, values, teamId, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkTimedOutAsync(Guid messageId, string responseKey, CancellationToken cancellationToken)
    {
        var message = await _db.Message
            .SingleOrDefaultAsync(m => m.Id == messageId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        var interaction = message?.InteractionJson is { } json ? MessageInteractionJson.Deserialize(json) : null;

        // No interaction, or a human already resolved it before the deadline fired → nothing to mirror.
        if (message == null || interaction is null || interaction.State != InteractionState.Open) return;

        message.InteractionJson = MessageInteractionJson.Serialize(interaction with
        {
            State = InteractionState.Resolved,
            Resolution = new InteractionResolution
            {
                ResponseKey = responseKey,
                ByUserId = Guid.Empty,   // system — nobody responded before the deadline
                Comment = "No response before the deadline.",
                AtUtc = DateTimeOffset.UtcNow,
            },
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── Terminal action: append the vote, then resolve the wait IF the policy is satisfied; else record + stay Open ──

    private async Task ResolveOrRecordVoteAsync(Message message, MessageInteraction interaction, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, Guid teamId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var withVote = interaction with
        {
            Responses = [.. interaction.Responses, new InteractionResponse { ByUserId = actorUserId, Kind = InteractionResponseKind.Action, Key = responseKey, Comment = comment, AtUtc = now }],
        };

        // Threshold not met yet (quorum short of N, no veto) → record the vote, leave the card Open, the run stays parked.
        if (!_resolvePolicy.ShouldResolve(withVote))
        {
            message.InteractionJson = MessageInteractionJson.Serialize(withVote);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Policy satisfied (first / quorum reached / veto) — this click is the tipping vote. Route the
        // decision to the wait; the outcome decides whether we stamp THIS decision on the card:
        //   • Resumed         — this click resolved the wait + re-dispatched the run → record it.
        //   • NoWait          — no parked wait (a post-and-continue card, or a run that already ended): the
        //                       card is a living thread decoupled from any run, so still record it — NOT an
        //                       error and NOT an expiry.
        //   • AlreadyResolved — a deadline timed out, or another responder already decided. The workflow's
        //                       decision is set; stamping this (late) one would make the card contradict the
        //                       workflow, so reject the click. (Concurrent humans are already serialized by
        //                       the FOR UPDATE lock in LoadMessageAsync + EnsureOpen, so this is the deadline /
        //                       cross-path case.)
        // (The identity gate inside the resume still throws 428/403 when a downstream node would act as the
        // responder — unchanged.)
        var outcome = await ResolveTargetAsync(interaction.Target, responseKey, actorUserId, comment, values, teamId, cancellationToken).ConfigureAwait(false);

        if (outcome == ActionResumeResult.AlreadyResolved) throw new InvalidOperationException("This interaction was already resolved.");

        message.InteractionJson = MessageInteractionJson.Serialize(withVote with
        {
            State = InteractionState.Resolved,
            Resolution = new InteractionResolution { ResponseKey = responseKey, ByUserId = actorUserId, Comment = comment, Values = values, AtUtc = now },
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── Comment (non-terminal): append to the log, stay Open ───────────────────────

    private async Task AddCommentAsync(Message message, MessageInteraction interaction, Guid actorUserId, string? comment, Guid teamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(comment)) throw new InvalidOperationException("A comment can't be empty.");

        // Discussion is open to any conversation member — AllowedResponderUserIds gates the DECISION, not
        // who may comment — so the whole team can participate on one card.
        var isMember = await IsConversationMemberAsync(teamId, message.ConversationId, actorUserId, cancellationToken).ConfigureAwait(false);

        if (!isMember) throw new InvalidOperationException("You are not a member of this conversation.");

        var appended = interaction with
        {
            Responses = [.. interaction.Responses, new InteractionResponse { ByUserId = actorUserId, Kind = InteractionResponseKind.Comment, Comment = comment, AtUtc = DateTimeOffset.UtcNow }],
        };

        message.InteractionJson = MessageInteractionJson.Serialize(appended);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── Load ────────────────────────────────────────────────────────────────────

    private async Task<Message> LoadMessageAsync(Guid teamId, Guid messageId, CancellationToken cancellationToken)
    {
        // Lock the row FOR UPDATE so concurrent responders to the same card serialize: the second waits
        // until the first commits, then re-reads the just-written interaction (and EnsureOpen rejects it if
        // the first already resolved). The lock holds for the ambient command transaction
        // (RespondToMessageCommand is an ICommand). Without it, two clicks read the same baseline and the
        // last writer clobbers the other — a dropped quorum vote, or a card resolution that disagrees with
        // the workflow decision. With no ambient transaction (e.g. a direct service call in a test) this is
        // a harmless no-op SELECT.
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM message WHERE id = {messageId} AND team_id = {teamId} AND deleted_date IS NULL FOR UPDATE", cancellationToken).ConfigureAwait(false);

        return await _db.Message
            .SingleOrDefaultAsync(m => m.Id == messageId && m.TeamId == teamId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Message {messageId} not found.");
    }

    // ─── Validation ──────────────────────────────────────────────────────────────

    private static void EnsureOpen(MessageInteraction interaction)
    {
        if (interaction.State != InteractionState.Open) throw new InvalidOperationException("This message's interaction is no longer open.");
    }

    private static void EnsureValidResponse(MessageInteraction interaction, string responseKey)
    {
        if (!MessageInteractionPolicy.IsValidResponse(interaction, responseKey)) throw new InvalidOperationException($"'{responseKey}' is not a valid response for this interaction.");
    }

    private static void EnsureCommentIfRequired(MessageInteraction interaction, string responseKey, string? comment)
    {
        if (MessageInteractionPolicy.RequiresComment(interaction, responseKey) && string.IsNullOrWhiteSpace(comment)) throw new InvalidOperationException($"Responding with '{responseKey}' requires a comment.");
    }

    private static void EnsureRequiredFields(MessageInteraction interaction, IReadOnlyDictionary<string, JsonElement>? values)
    {
        var missing = MessageInteractionPolicy.MissingRequiredFields(interaction, values);

        if (missing.Count > 0) throw new InvalidOperationException($"Missing required field(s): {string.Join(", ", missing)}.");
    }

    private async Task EnsureAllowedResponderAsync(Guid teamId, Guid conversationId, MessageInteraction interaction, Guid actorUserId, CancellationToken cancellationToken)
    {
        var isMember = await IsConversationMemberAsync(teamId, conversationId, actorUserId, cancellationToken).ConfigureAwait(false);

        if (!MessageInteractionPolicy.IsAllowedResponder(interaction, actorUserId, isMember)) throw new InvalidOperationException("You are not allowed to respond to this message.");
    }

    private async Task<bool> IsConversationMemberAsync(Guid teamId, Guid conversationId, Guid actorUserId, CancellationToken cancellationToken) =>
        await _db.ConversationMember.AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.UserId == actorUserId && m.TeamId == teamId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

    // ─── Dispatch (route the response to the interaction's target) ──────────────────

    private async Task<ActionResumeResult> ResolveTargetAsync(InteractionTarget target, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, Guid teamId, CancellationToken cancellationToken) =>
        target switch
        {
            WorkflowWaitTarget wait => await _resume.ResumeByActionTokenAsync(wait.Token, responseKey, actorUserId, comment, values, teamId, cancellationToken).ConfigureAwait(false),
            ToolCallApprovalTarget approval => await _toolApprovals.ResolveByTokenAsync(approval.Token, responseKey, actorUserId, teamId, cancellationToken).ConfigureAwait(false),
            DecisionRequestTarget decision => await _decisions.ResolveByTokenAsync(decision.Token, responseKey, comment, actorUserId, teamId, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported interaction target '{target.GetType().Name}'."),
        };
}
