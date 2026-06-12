using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.RunSources.Schedule;

/// <summary>
/// Fires <c>trigger.schedule</c> activations whose cron is due. The <c>workflow_activation</c> row
/// (TypeKey <c>trigger.schedule</c>, ConfigJson <c>{ cron }</c>) IS the schedule registry — no
/// separate matcher, since a schedule has no inbound event to match against.
///
/// <para>Idempotency: each due occurrence fires with <c>SourceInstanceId = activationId</c> +
/// <c>ExternalEventId = occurrence-unix-seconds</c>, so the run-request unique index collapses any
/// repeat (overlapping ticks, a redelivered tick after a crash) to a single run. A bounded look-back
/// window (default 2 min, overridable) bounds catch-up after downtime to a few runs rather than a
/// flood — older missed occurrences are intentionally skipped.</para>
/// </summary>
public sealed class ScheduleTriggerService : IScheduleTriggerService, IScopedDependency
{
    /// <summary>Operator override for the look-back window, in seconds. Must exceed the tick interval
    /// (one minute) plus jitter; the default of 120s comfortably does. Renaming breaks any operator who
    /// pinned a value — hard-pinned by a unit test.</summary>
    public const string LookbackSecondsEnvVar = "CODESPACE_SCHEDULE_TRIGGER_LOOKBACK_SECONDS";

    private const int DefaultLookbackSeconds = 120;

    private readonly CodeSpaceDbContext _db;
    private readonly IRunStarter _runStarter;
    private readonly IWorkflowRunDispatcher _runDispatcher;
    private readonly IPostCommitActions _postCommit;
    private readonly ILogger<ScheduleTriggerService> _logger;

    public ScheduleTriggerService(CodeSpaceDbContext db, IRunStarter runStarter, IWorkflowRunDispatcher runDispatcher, IPostCommitActions postCommit, ILogger<ScheduleTriggerService> logger)
    {
        _db = db;
        _runStarter = runStarter;
        _runDispatcher = runDispatcher;
        _postCommit = postCommit;
        _logger = logger;
    }

    public async Task<int> FireDueSchedulesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var from = now - LookbackWindow();

        var activations = await LoadActiveScheduleActivationsAsync(cancellationToken).ConfigureAwait(false);

        var firedRunIds = new List<Guid>();

        foreach (var activation in activations)
        {
            firedRunIds.AddRange(await FireActivationOccurrencesAsync(activation, from, now, cancellationToken).ConfigureAwait(false));
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await DispatchAfterCommitAsync(firedRunIds, cancellationToken).ConfigureAwait(false);

        return firedRunIds.Count;
    }

    private async Task<IReadOnlyList<Guid>> FireActivationOccurrencesAsync(WorkflowActivation activation, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var cron = ReadCron(activation.ConfigJson);

        if (!ScheduleOccurrenceCalculator.TryGetOccurrences(cron ?? "", from, to, out var occurrences))
        {
            _logger.LogWarning("Skipping schedule activation {ActivationId}: invalid cron expression {Cron}", activation.Id, cron);
            return Array.Empty<Guid>();
        }

        var fired = new List<Guid>();

        foreach (var occurrence in occurrences)
        {
            var runId = await FireOccurrenceAsync(activation, cron!, occurrence, cancellationToken).ConfigureAwait(false);
            if (runId != Guid.Empty) fired.Add(runId);
        }

        return fired;
    }

    private async Task<Guid> FireOccurrenceAsync(WorkflowActivation activation, string cron, DateTimeOffset occurrence, CancellationToken cancellationToken)
    {
        var occurrenceUtc = occurrence.ToUniversalTime();

        var payload = JsonSerializer.Serialize(new
        {
            scheduledFor = occurrenceUtc.ToString("O"),
            cron
        });

        var runId = await _runStarter.StartAsync(new RunSourceEnvelope
        {
            TeamId = activation.Workflow.TeamId,
            WorkflowId = activation.WorkflowId,
            WorkflowVersion = activation.Workflow.LatestVersion,
            SourceType = WorkflowRunSourceTypes.ScheduleCron,
            ActorType = WorkflowRunActorTypes.System,
            ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = payload,
            CreatedBy = SystemUsers.SeederId,
            ActivationId = activation.Id,
            ActivationSnapshotJson = SerializeActivationSnapshot(activation),
            SourceInstanceId = activation.Id.ToString(),
            ExternalEventId = occurrenceUtc.ToUnixTimeSeconds().ToString(),
            IdempotencyKey = $"{WorkflowRunSourceTypes.ScheduleCron}:{activation.Id:N}:{occurrenceUtc.ToUnixTimeSeconds()}",
        }, cancellationToken).ConfigureAwait(false);

        if (runId == Guid.Empty)
        {
            _logger.LogDebug("Schedule activation {ActivationId} occurrence {Occurrence} already fired — skipping duplicate", activation.Id, occurrenceUtc);
            return Guid.Empty;
        }

        _logger.LogInformation("Fired scheduled workflow {WorkflowId} (activation {ActivationId}) for {Occurrence} → run {RunId}", activation.WorkflowId, activation.Id, occurrenceUtc, runId);
        return runId;
    }

    private async Task DispatchAfterCommitAsync(IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        foreach (var runId in runIds)
        {
            try
            {
                await _postCommit.RunAfterCommitAsync(ct => _runDispatcher.DispatchAsync(runId, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Schedule producer: failed to dispatch run {RunId}; reconciler will retry", runId);
            }
        }
    }

    private async Task<IReadOnlyList<WorkflowActivation>> LoadActiveScheduleActivationsAsync(CancellationToken cancellationToken) =>
        await _db.WorkflowActivation
            .Include(a => a.Workflow)
            .Where(a => a.TypeKey == "trigger.schedule"
                        && a.Enabled
                        && a.DeletedDate == null
                        && a.Workflow.Enabled
                        && a.Workflow.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static string? ReadCron(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.TryGetProperty("cron", out var cronEl) && cronEl.ValueKind == JsonValueKind.String
                ? cronEl.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeActivationSnapshot(WorkflowActivation a) =>
        JsonSerializer.Serialize(new
        {
            id = a.Id,
            typeKey = a.TypeKey,
            config = JsonDocument.Parse(a.ConfigJson).RootElement,
            enabled = a.Enabled,
        });

    private static TimeSpan LookbackWindow()
    {
        var raw = Environment.GetEnvironmentVariable(LookbackSecondsEnvVar);

        if (int.TryParse(raw, out var seconds) && seconds > 0) return TimeSpan.FromSeconds(seconds);

        return TimeSpan.FromSeconds(DefaultLookbackSeconds);
    }
}
