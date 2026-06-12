using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
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
/// "load activations of this type, fire matches, persist a request + run, hand to the
/// background-job dispatcher."
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
    private readonly ILogger<RunSourceDispatcher> _logger;

    public RunSourceDispatcher(CodeSpaceDbContext db, IRunSourceMatcherRegistry matcherRegistry, IRunStarter runStarter, Dispatch.IWorkflowRunDispatcher runDispatcher, IIngestionAuditor auditor, IPostCommitActions postCommit, ILogger<RunSourceDispatcher> logger)
    {
        _db = db;
        _matcherRegistry = matcherRegistry;
        _runStarter = runStarter;
        _runDispatcher = runDispatcher;
        _auditor = auditor;
        _postCommit = postCommit;
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
        var candidateMatchers = _matcherRegistry.All.Where(m => CanHandle(m, normalizedEvent)).ToList();

        if (candidateMatchers.Count == 0)
        {
            // No matcher knows about this event type at all — that's a normalizer bug or a
            // matcher missing for a newly-added event. Audit it; the engine has no built-in
            // matcher catalog so this surface only fires for malformed registrations.
            await WriteNoMatchAuditAsync(normalizedEvent, cancellationToken).ConfigureAwait(false);
            return;
        }

        var activationTypeKeys = candidateMatchers.Select(m => m.TypeKey).ToList();
        var activations = await LoadActiveActivationsAsync(activationTypeKeys, cancellationToken).ConfigureAwait(false);

        if (activations.Count == 0)
        {
            // At least one matcher could classify this event, but no workflow subscribes to
            // it. Write a Rejected audit row so the operator sees "your PR was detected but
            // no workflow listens for it" instead of silence.
            await WriteNoMatchAuditAsync(normalizedEvent, cancellationToken).ConfigureAwait(false);
            return;
        }

        var firedRunIds = new List<Guid>();
        foreach (var activation in activations)
        {
            var matcher = candidateMatchers.First(m => m.TypeKey == activation.TypeKey);
            var runId = await FireIfMatchesAsync(activation, matcher, normalizedEvent, cancellationToken).ConfigureAwait(false);
            if (runId.HasValue) firedRunIds.Add(runId.Value);
        }

        // All activations of the right type existed, but their CONFIG filters (e.g.
        // repositoryId scope) excluded this event. Audit the no-fire outcome so the operator
        // can see "your activation didn't match because its repositoryId filter excluded this PR".
        if (firedRunIds.Count == 0)
            await WriteNoMatchAuditAsync(normalizedEvent, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Dispatch each matched run AFTER commit. RunAfterCommitAsync defers into the post-commit drain
        // while a transaction is open (the ReceiveWebhookCommand path), so a worker can't pick up a
        // runId before its row is visible — the exact race the previous inline dispatch hit, since the
        // SaveChanges above only flushes within the still-open command transaction. With no ambient
        // transaction (an event published directly, e.g. in tests) it runs inline. Reconciler covers any
        // row whose dispatch is dropped (e.g. Hangfire transient outage).
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

    /// <summary>
    /// Look up the team that owns the event's repository, then write the Rejected audit row.
    /// Best-effort: if the repository was deleted between webhook receipt and dispatch, we
    /// skip the audit row (no team to attribute it to).
    /// </summary>
    private async Task WriteNoMatchAuditAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken)
    {
        var teamId = await _db.Repository.AsNoTracking()
            .Where(r => r.Id == normalizedEvent.RepositoryId)
            .Select(r => (Guid?)r.TeamId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (teamId == null)
        {
            _logger.LogWarning(
                "Dispatcher: no-match for {EventType} on repository {RepositoryId}, but repository was deleted — skipping audit",
                normalizedEvent.GetType().Name, normalizedEvent.RepositoryId);
            return;
        }

        await _auditor.WriteNoMatchRejectedAsync(normalizedEvent, teamId.Value, cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<WorkflowActivation>> LoadActiveActivationsAsync(IReadOnlyList<string> typeKeys, CancellationToken cancellationToken)
    {
        return await _db.WorkflowActivation
            .Include(a => a.Workflow)
            .Where(a => typeKeys.Contains(a.TypeKey)
                        && a.Enabled
                        && a.DeletedDate == null
                        && a.Workflow.Enabled
                        && a.Workflow.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid?> FireIfMatchesAsync(WorkflowActivation activation, IRunSourceMatcher matcher, NormalizedEvent normalizedEvent, CancellationToken cancellationToken)
    {
        var config = JsonDocument.Parse(activation.ConfigJson).RootElement;

        if (!matcher.Match(normalizedEvent, config)) return null;

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
            ActivationSnapshotJson = SerializeActivationSnapshot(activation),
            ExternalEventId = normalizedEvent.ProviderEventId,
            IdempotencyKey = SynthesiseProviderIdempotencyKey(matcher.TypeKey, normalizedEvent.ProviderEventId, activation.Id),
        }, cancellationToken).ConfigureAwait(false);

        if (runId == Guid.Empty)
        {
            _logger.LogInformation(
                "Skipped duplicate provider delivery: workflow {WorkflowId} activation {ActivationId} delivery {DeliveryId}",
                activation.WorkflowId, activation.Id, normalizedEvent.ProviderEventId);
            return null;
        }

        _logger.LogInformation(
            "Fired workflow {WorkflowId} via activation {TypeKey} → run {RunId}",
            activation.WorkflowId, activation.TypeKey, runId);
        return runId;
    }

    /// <summary>
    /// Composite idempotency key for a provider event: <c>{sourceType}:{deliveryId}:{activationId}</c>.
    /// Lets the SAME delivery fan out to N activations (each gets a unique key) while a
    /// provider's duplicate redelivery of the same delivery id for the SAME activation
    /// dedupes via the uq_wrr_idempotency_key partial index.
    /// </summary>
    private static string SynthesiseProviderIdempotencyKey(string sourceType, string deliveryId, Guid activationId) =>
        $"{sourceType}:{deliveryId}:{activationId:N}";

    private static string SerializeActivationSnapshot(WorkflowActivation a)
    {
        // Capture the matched activation row verbatim. Replay tooling reads this to reproduce
        // the original match decision even after the activation is edited or deleted.
        var snapshot = new
        {
            id = a.Id,
            typeKey = a.TypeKey,
            config = JsonDocument.Parse(a.ConfigJson).RootElement,
            enabled = a.Enabled,
        };
        return JsonSerializer.Serialize(snapshot);
    }
}
