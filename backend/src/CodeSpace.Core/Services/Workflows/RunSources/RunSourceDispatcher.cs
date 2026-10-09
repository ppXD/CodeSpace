using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Completion.Exceptions;
using CodeSpace.Core.Services.Workflows.RunSources.Admission;
using CodeSpace.Core.Services.Workflows.RunSources.Admission.Exceptions;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;
using CodeSpace.Messages.Events.Push;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// The bridge from provider events to workflow runs. The webhook ingestion path publishes
/// concrete <see cref="NormalizedEvent"/> subclasses via MediatR; this class registers an
/// <see cref="INotificationHandler{TNotification}"/> for every event type a built-in matcher
/// knows about, and routes all of them through one shared <see cref="DispatchAsync"/> method.
///
/// Adding a new source event type means:
///   1. add a new <c>IRunSourceMatcher</c>
///   2. add one more <c>INotificationHandler&lt;NewEvent&gt;</c> line + a tiny pass-through method
///
/// Two lines per new event type. The matcher does the matching; the dispatcher's job is
/// "load the event repository's team's activations of this type, fire matches, persist a
/// request + run, hand to the background-job dispatcher." A delivery never reaches another
/// team's activations — an activation is a subscription inside its own team.
///
/// Pipeline per match: insert <see cref="WorkflowRunRequest"/> (Consumed) → insert
/// <see cref="WorkflowRun"/> (Pending) pointing at the request → call <c>IWorkflowRunDispatcher</c>
/// to CAS Pending→Enqueued + enqueue the Hangfire job. The request row carries the source
/// identity + raw normalised payload + frozen activation snapshot, so the run is just an
/// execution handle.
/// </summary>
public sealed class RunSourceDispatcher :
    INotificationHandler<PullRequestOpenedEvent>,
    INotificationHandler<PullRequestSynchronizedEvent>,
    INotificationHandler<PullRequestMergedEvent>,
    INotificationHandler<PushReceivedEvent>,
    IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IRunSourceMatcherRegistry _matcherRegistry;
    private readonly IRunStarter _runStarter;
    private readonly Dispatch.IWorkflowRunDispatcher _runDispatcher;
    private readonly IIngestionAuditor _auditor;
    private readonly IPostCommitActions _postCommit;
    private readonly IPullRequestTriggerAdmission _admission;
    private readonly ILogger<RunSourceDispatcher> _logger;

    public RunSourceDispatcher(CodeSpaceDbContext db, IRunSourceMatcherRegistry matcherRegistry, IRunStarter runStarter, Dispatch.IWorkflowRunDispatcher runDispatcher, IIngestionAuditor auditor, IPostCommitActions postCommit, IPullRequestTriggerAdmission admission, ILogger<RunSourceDispatcher> logger)
    {
        _db = db;
        _matcherRegistry = matcherRegistry;
        _runStarter = runStarter;
        _runDispatcher = runDispatcher;
        _auditor = auditor;
        _postCommit = postCommit;
        _admission = admission;
        _logger = logger;
    }

    public Task Handle(PullRequestOpenedEvent notification, CancellationToken cancellationToken) =>
        DispatchAsync(notification, cancellationToken);

    public Task Handle(PullRequestSynchronizedEvent notification, CancellationToken cancellationToken) =>
        DispatchAsync(notification, cancellationToken);

    public Task Handle(PullRequestMergedEvent notification, CancellationToken cancellationToken) =>
        DispatchAsync(notification, cancellationToken);

    public Task Handle(PushReceivedEvent notification, CancellationToken cancellationToken) =>
        DispatchAsync(notification, cancellationToken);

    private async Task DispatchAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken)
    {
        var teamId = await ResolveEventTeamAsync(normalizedEvent, cancellationToken).ConfigureAwait(false);

        if (teamId == null)
        {
            // Normalization only ever names a repository its hook resolved, so this is a repository removed (or
            // never held) between receipt and dispatch: no team may run it, and there is no team to attribute an
            // audit row to.
            _logger.LogWarning("Dispatcher: {EventType} names repository {RepositoryId}, which no team holds — nothing to start or audit", normalizedEvent.GetType().Name, normalizedEvent.RepositoryId);
            return;
        }

        var candidateMatchers = _matcherRegistry.All.Where(m => CanHandle(m, normalizedEvent)).ToList();

        if (candidateMatchers.Count == 0)
        {
            // No matcher knows about this event type at all — that's a normalizer bug or a
            // matcher missing for a newly-added event. Audit it; the engine has no built-in
            // matcher catalog so this surface only fires for malformed registrations.
            await _auditor.WriteNoMatchRejectedAsync(normalizedEvent, teamId.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        var activationTypeKeys = candidateMatchers.Select(m => m.TypeKey).ToList();
        var activations = await LoadActiveActivationsAsync(teamId.Value, activationTypeKeys, cancellationToken).ConfigureAwait(false);

        if (activations.Count == 0)
        {
            // At least one matcher could classify this event, but no workflow subscribes to
            // it. Write a Rejected audit row so the operator sees "your PR was detected but
            // no workflow listens for it" instead of silence.
            await _auditor.WriteNoMatchRejectedAsync(normalizedEvent, teamId.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        var (firedRunIds, anyRefused) = await FireEachAsync(activations, candidateMatchers, normalizedEvent, cancellationToken).ConfigureAwait(false);

        // All activations of the right type existed, but their CONFIG filters (e.g.
        // repositoryId scope) excluded this event. Audit the no-fire outcome so the operator
        // can see "your activation didn't match because its repositoryId filter excluded this PR".
        if (firedRunIds.Count == 0 && !anyRefused)
            await _auditor.WriteNoMatchRejectedAsync(normalizedEvent, teamId.Value, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await DispatchAfterCommitAsync(firedRunIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The team holding the event's repository — the ONLY team whose activations may receive it. Resolved once,
    /// before any activation is read: an activation is a subscription inside its own team, so one naming no
    /// repository means "any repository of this team", never "any repository anywhere". A removed repository is
    /// held by no team; nothing it delivers may start a run.
    /// </summary>
    private async Task<Guid?> ResolveEventTeamAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Where(r => r.Id == normalizedEvent.RepositoryId && r.DeletedDate == null)
            .Select(r => (Guid?)r.TeamId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Fire every activation, each in isolation. A refusal that belongs to ONE activation — its publisher's authority,
    /// its definition's completion opt-in — costs that activation's run and leaves a Rejected row naming it; every
    /// sibling still starts. Anything else (a database or job-store failure) is not that activation's property and
    /// propagates, so the delivery rolls back and the provider redelivers.
    /// </summary>
    private async Task<(List<Guid> FiredRunIds, bool AnyRefused)> FireEachAsync(IReadOnlyList<WorkflowActivation> activations, IReadOnlyList<IRunSourceMatcher> candidateMatchers, NormalizedEvent normalizedEvent, CancellationToken cancellationToken)
    {
        var firedRunIds = new List<Guid>();
        var anyRefused = false;

        foreach (var activation in activations)
        {
            var matcher = candidateMatchers.First(m => m.TypeKey == activation.TypeKey);

            try
            {
                var runId = await FireIfMatchesAsync(activation, matcher, normalizedEvent, cancellationToken).ConfigureAwait(false);
                if (runId.HasValue) firedRunIds.Add(runId.Value);
            }
            catch (AgentAuthorityDeniedException ex)
            {
                anyRefused = true;
                _logger.LogWarning("Webhook activation authority refused. ActivationId={ActivationId} WorkflowId={WorkflowId} TeamId={TeamId} Code={Code} Reason={Reason}", activation.Id, activation.WorkflowId, activation.Workflow.TeamId, ex.Code, ex.Reason);
                await AuditRefusalAsync(activation, normalizedEvent, ex.Code, ex.Reason, PerDeliveryRefusalKey("authority", activation, normalizedEvent), cancellationToken).ConfigureAwait(false);
            }
            catch (CompletionAdmissionRefusedException ex)
            {
                anyRefused = true;
                _logger.LogWarning("Webhook activation completion admission refused. ActivationId={ActivationId} WorkflowId={WorkflowId} TeamId={TeamId} Reason={Reason}", activation.Id, activation.WorkflowId, activation.Workflow.TeamId, ex.Message);
                await AuditRefusalAsync(activation, normalizedEvent, WorkflowRunRequestRejectionReasons.CompletionAdmissionRefused, ex.Message, PerDeliveryRefusalKey("admission", activation, normalizedEvent), cancellationToken).ConfigureAwait(false);
            }
            catch (PullRequestAuthorRefusedException ex)
            {
                anyRefused = true;
                _logger.LogInformation("Webhook activation refused a pull request author. ActivationId={ActivationId} WorkflowId={WorkflowId} TeamId={TeamId} Reason={Reason}", activation.Id, activation.WorkflowId, activation.Workflow.TeamId, ex.Message);
                await AuditRefusalAsync(activation, normalizedEvent, WorkflowRunRequestRejectionReasons.AuthorNotMember, ex.Message, ex.AuditKey, cancellationToken).ConfigureAwait(false);
            }
            catch (PullRequestTriggerDebouncedException ex)
            {
                anyRefused = true;
                _logger.LogInformation("Webhook activation debounced a pull request. ActivationId={ActivationId} WorkflowId={WorkflowId} TeamId={TeamId} Reason={Reason}", activation.Id, activation.WorkflowId, activation.Workflow.TeamId, ex.Message);
                await AuditRefusalAsync(activation, normalizedEvent, WorkflowRunRequestRejectionReasons.PullRequestDebounced, ex.Message, ex.AuditKey, cancellationToken).ConfigureAwait(false);
            }
        }

        return (firedRunIds, anyRefused);
    }

    /// <summary>One Rejected row per <paramref name="dedupKey"/>, so a provider's redelivery of the same refused event collapses onto it.</summary>
    private async Task AuditRefusalAsync(WorkflowActivation activation, NormalizedEvent normalizedEvent, string reason, string detail, string dedupKey, CancellationToken cancellationToken) =>
        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = activation.Workflow.TeamId, RepositoryId = normalizedEvent.RepositoryId, SourceType = activation.TypeKey,
            ExternalEventId = normalizedEvent.ProviderEventId, Reason = reason,
            Detail = $"Activation {activation.Id} workflow {activation.WorkflowId}: {detail}",
            DedupKey = dedupKey,
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>One row per (activation, delivery, refusal kind) — a refusal that is the activation's own property and rare.</summary>
    private static string PerDeliveryRefusalKey(string refusalKind, WorkflowActivation activation, NormalizedEvent normalizedEvent) =>
        $"rejected:{refusalKind}:{activation.Id:N}:{normalizedEvent.ProviderEventId}";

    /// <summary>
    /// Dispatch each matched run AFTER commit. RunAfterCommitAsync defers into the post-commit drain
    /// while a transaction is open (the ReceiveWebhookCommand path), so a worker can't pick up a
    /// runId before its row is visible — the exact race the previous inline dispatch hit, since the
    /// SaveChanges above only flushes within the still-open command transaction. With no ambient
    /// transaction (an event published directly, e.g. in tests) it runs inline. Reconciler covers any
    /// row whose dispatch is dropped (e.g. Hangfire transient outage).
    /// </summary>
    private async Task DispatchAfterCommitAsync(IReadOnlyList<Guid> firedRunIds, CancellationToken cancellationToken)
    {
        foreach (var runId in firedRunIds)
        {
            try
            {
                await _postCommit.RunAfterCommitAsync(ct => _runDispatcher.DispatchAsync(runId, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Webhook dispatcher: failed to dispatch run {RunId}; reconciler will retry", runId);
            }
        }
    }

    /// <summary>True iff this matcher knows the event TYPE (cheap probe; type-check only, no DB hit).</summary>
    private static bool CanHandle(IRunSourceMatcher matcher, NormalizedEvent normalizedEvent)
    {
        try
        {
            // Try an empty config; matchers MUST tolerate a missing-filter config (treat as "any").
            // If a matcher rejects the event TYPE itself it will return false here.
            return matcher.Match(normalizedEvent, JsonDocument.Parse("{}").RootElement);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ordered by id: firing an activation can take a row lock that is held until the delivery commits (the pull-request
    /// debounce claim), so two concurrent deliveries must visit activations in the same order or each could hold what the
    /// other waits for.
    /// </summary>
    private async Task<IReadOnlyList<WorkflowActivation>> LoadActiveActivationsAsync(Guid teamId, IReadOnlyList<string> typeKeys, CancellationToken cancellationToken)
    {
        return await _db.WorkflowActivation
            .Include(a => a.Workflow)
            .Where(a => a.Workflow.TeamId == teamId
                        && typeKeys.Contains(a.TypeKey)
                        && a.Enabled
                        && a.DeletedDate == null
                        && a.Workflow.Enabled
                        && a.Workflow.DeletedDate == null)
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid?> FireIfMatchesAsync(WorkflowActivation activation, IRunSourceMatcher matcher, NormalizedEvent normalizedEvent, CancellationToken cancellationToken)
    {
        var config = JsonDocument.Parse(activation.ConfigJson).RootElement;

        if (!matcher.Match(normalizedEvent, config)) return null;

        var idempotencyKey = SynthesiseProviderIdempotencyKey(matcher.TypeKey, normalizedEvent.ProviderEventId, activation.Id);

        if (await IsAlreadyStartedAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)) return SkipDuplicate(activation, normalizedEvent);

        await _admission.EnsureAdmittedAsync(activation, normalizedEvent, config, cancellationToken).ConfigureAwait(false);

        var payload = matcher.BuildPayload(normalizedEvent);

        // Hand the envelope to the unified starter. The starter stages workflow_run_request +
        // workflow_run + emits run.queued; we provide only the source-specific fields (matcher
        // TypeKey, activation lineage).
        //
        // Provider-event idempotency: thread the delivery id (ProviderEventId — e.g.
        // GitHub's X-GitHub-Delivery) into both ExternalEventId (audit) AND a synthesised
        // IdempotencyKey that includes the activation id. RunStarter's unique-violation catch
        // turns a duplicate delivery for the SAME activation into a silent no-op while still
        // letting fan-out across activations succeed (each gets its own unique key).
        var runId = await _runStarter.StartAsync(new RunSourceEnvelope
        {
            TeamId = activation.Workflow.TeamId,
            WorkflowId = activation.WorkflowId,
            WorkflowVersion = activation.Workflow.LatestVersion,
            SourceType = matcher.TypeKey,
            ActorType = WorkflowRunActorTypes.Webhook,
            ActorId = null,                                     // webhook actor is anonymous
            NormalizedPayloadJson = payload.GetRawText(),
            CreatedBy = SystemUsers.SeederId,                   // engine-initiated row; no user identity
            ActivationId = activation.Id,
            ActivationSnapshotJson = ActivationAuthoritySnapshot.Serialize(activation),
            ExternalEventId = normalizedEvent.ProviderEventId,
            IdempotencyKey = idempotencyKey,
        }, cancellationToken).ConfigureAwait(false);

        if (runId == Guid.Empty) return SkipDuplicate(activation, normalizedEvent);

        _logger.LogInformation(
            "Fired workflow {WorkflowId} via activation {TypeKey} → run {RunId}",
            activation.WorkflowId, activation.TypeKey, runId);
        return runId;
    }

    /// <summary>
    /// A delivery this activation already started a run for — the provider redelivering it, an operator pressing
    /// Redeliver, a captured body posted again under its own id — is a duplicate before it is anything else. Answered
    /// here, ahead of admission, it cannot take the pull-request debounce claim a second time (which would restart the
    /// window and swallow the next real event) or send a provider another standing lookup. Two copies racing each other
    /// both pass this read; RunStarter's unique index still keeps one.
    /// </summary>
    private async Task<bool> IsAlreadyStartedAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        await _db.WorkflowRunRequest.AsNoTracking().AnyAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false);

    private Guid? SkipDuplicate(WorkflowActivation activation, NormalizedEvent normalizedEvent)
    {
        _logger.LogInformation("Skipped duplicate provider delivery: workflow {WorkflowId} activation {ActivationId} delivery {DeliveryId}", activation.WorkflowId, activation.Id, normalizedEvent.ProviderEventId);
        return null;
    }

    /// <summary>
    /// Composite idempotency key for a provider event: <c>{sourceType}:{deliveryId}:{activationId}</c>.
    /// Lets the SAME delivery fan out to N activations (each gets a unique key) while a
    /// provider's duplicate redelivery of the same delivery id for the SAME activation
    /// dedupes via the uq_wrr_idempotency_key partial index.
    /// </summary>
    private static string SynthesiseProviderIdempotencyKey(string sourceType, string deliveryId, Guid activationId) =>
        $"{sourceType}:{deliveryId}:{activationId:N}";


}
