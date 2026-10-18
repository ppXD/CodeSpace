using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Credentials;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Health.Checks;

/// <summary>
/// Whether the disconnected-credential binding-repair sweep exists on this deployment, when it last ran, and how many
/// credentials it is waiting to repair. Only a Worker-role pod registers recurring jobs, so an API-only fleet — or workers
/// still on a build from before the sweep — has no sweep at all, and nothing but a query against Hangfire's tables said so.
///
/// <para>The schedule is Hangfire's own record, read from the same storage from any role (the API pod's report describes the
/// worker's sweep). The backlog is counted live — the rows the sweep would take — so it stays right when the sweep is run by a
/// build that records nothing. Both are kept for <see cref="SnapshotLifetime"/>: this check answers an anonymous endpoint and
/// reads the database.</para>
///
/// <para>Degraded, never Unhealthy, and never a refusal: a deployment without the sweep serves requests and runs agents; what it
/// does not do is carry a repository onto its owner's reconnect.</para>
///
/// <para>The scheduler's own error text is never part of the finding. Hangfire stores an exception chain there — up to a hundred
/// stack frames, and for a storage fault the server's host and port — and <c>/health</c> is read by callers who are not signed
/// in. The finding says an error was recorded; the full text goes to the log, once per distinct error.</para>
/// </summary>
public sealed class CredentialRepairSweepHealthCheck : IHealthCheck, ISingletonDependency
{
    /// <summary>The check's name in <c>/health</c> — a contract, pinned by test.</summary>
    public const string Name = "credential-repair-sweep";

    /// <summary>The recurring-job id the Worker role registers the sweep under (<see cref="DisconnectedCredentialBindingRepairRecurringJob.JobId"/>).</summary>
    public const string JobId = nameof(DisconnectedCredentialBindingRepairRecurringJob);

    /// <summary>How long a reading is reused.</summary>
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(30);

    /// <summary>How late the next scheduled run may be before the schedule is called stalled: Hangfire's scheduler polls every few seconds, so a run minutes past due means no scheduler is running.</summary>
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<CredentialRepairSweepHealthCheck> _logger;
    private readonly JobStorage? _storage;
    private readonly object _gate = new();
    private (DateTimeOffset At, HealthCheckResult Result)? _last;
    private string? _loggedSchedulerError;

    public CredentialRepairSweepHealthCheck(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CredentialRepairSweepHealthCheck> logger, JobStorage? storage = null)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
        _storage = storage;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (Fresh() is { } cached) return cached;

        var result = await ReadAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate) _last = (_clock.GetUtcNow(), result);

        return result;
    }

    private HealthCheckResult? Fresh()
    {
        lock (_gate) return _last is { } last && _clock.GetUtcNow() - last.At < SnapshotLifetime ? last.Result : null;
    }

    private async Task<HealthCheckResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (_storage is null) return HealthCheckResult.Degraded("This host has no job storage, so whether the repair sweep is scheduled cannot be read.");

        try
        {
            var entry = ReadSchedule(_storage);

            LogSchedulerError(entry);

            var candidates = await CountCandidatesAsync(cancellationToken).ConfigureAwait(false);

            return Assess(entry, candidates, _clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded($"The repair sweep's schedule could not be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Where the scheduler's recorded error goes instead of the report: a Warning with its full text, the first time a disabled
    /// entry carries it and again only when the text changes or returns after the entry recovered. The check is read every
    /// <see cref="SnapshotLifetime"/>, so without the memory one stuck entry would log twice a minute.
    /// </summary>
    internal void LogSchedulerError(RecurringJobDto? entry)
    {
        var error = entry is { Removed: false, NextExecution: null } && !string.IsNullOrWhiteSpace(entry.Error) ? entry.Error : null;

        lock (_gate)
        {
            if (error == _loggedSchedulerError) return;

            _loggedSchedulerError = error;
        }

        if (error is not null) _logger.LogWarning("Hangfire's scheduler disabled the {JobId} schedule after an error: {SchedulerError}", JobId, error);
    }

    private static RecurringJobDto? ReadSchedule(JobStorage storage)
    {
        using var connection = storage.GetConnection();

        return connection.GetRecurringJobs([JobId]).SingleOrDefault();
    }

    /// <summary>A failed count is null, not an exception: the schedule is still worth reporting. A scope of its own because this check is a singleton and the census reads through a request-lifetime context.</summary>
    private async Task<int?> CountCandidatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<IStrandedCredentialCensus>().CountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The finding for one reading of the schedule and the backlog. Pure: the clock and both reads are arguments.</summary>
    public static HealthCheckResult Assess(RecurringJobDto? entry, int? candidates, DateTime nowUtc)
    {
        var data = new Dictionary<string, object> { ["registered"] = entry is { Removed: false } };

        if (candidates is { } count) data["strandedCandidates"] = count;

        if (entry is null or { Removed: true })
            return HealthCheckResult.Degraded($"The repair sweep ({JobId}) is not registered: only a Worker-role pod registers recurring jobs, so no worker of a build that has it has started against this database.", data: data);

        AddSchedule(data, entry);

        if (Stalled(entry, nowUtc) is { } stalled) return HealthCheckResult.Degraded(stalled, data: data);

        if (candidates is null) return HealthCheckResult.Degraded("The repair sweep is scheduled, but the credentials it has to repair could not be counted.", data: data);

        return HealthCheckResult.Healthy(entry.LastExecution is null ? "The repair sweep is scheduled and has not run yet." : "The repair sweep is scheduled and running.", data);
    }

    private static void AddSchedule(Dictionary<string, object> data, RecurringJobDto entry)
    {
        data["cron"] = entry.Cron;

        if (entry.LastExecution is { } last) data["lastExecutionUtc"] = last;
        if (entry.NextExecution is { } next) data["nextExecutionUtc"] = next;
        if (entry.LastJobState is { } state) data["lastJobState"] = state;
    }

    /// <summary>Why the schedule is not doing its job, or null when it is: disabled by the scheduler, overdue, or its last run failed.</summary>
    private static string? Stalled(RecurringJobDto entry, DateTime nowUtc)
    {
        if (entry.NextExecution is null)
            return $"The repair sweep is registered but disabled: Hangfire's scheduler gave it no next run{(string.IsNullOrWhiteSpace(entry.Error) ? "" : " and recorded an error (its text is in this pod's log and on the recurring job in Hangfire's storage)")}. A current worker re-enables it.";

        if (nowUtc - entry.NextExecution.Value > OverdueAfter)
            return $"The repair sweep is overdue by {(nowUtc - entry.NextExecution.Value).TotalMinutes:0} minutes: no live worker is running Hangfire's scheduler.";

        return entry.LastJobState == "Failed" ? "The repair sweep's last run failed." : null;
    }
}
