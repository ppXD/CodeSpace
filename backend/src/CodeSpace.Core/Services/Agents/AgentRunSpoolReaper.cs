using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Reclaims the host resources of agent runs that have FINISHED past a retention window — the disk
/// counterpart to <c>IWorkspaceJanitor</c>. The durable runner writes each run's stdout/stderr/exit/pid to a
/// spool directory so a restart can recover/re-attach it; this applies the configured terminal-run retention
/// policy to that host-local source. Terminal status alone does not prove log completeness. It is ALSO the backstop for the
/// durable runner's filtered-egress netns (B3.2b): a run that reached terminal via a path that skipped the runner's
/// per-terminal teardown (most notably a re-attach that could only complete from the exit marker) still carries its
/// netns key on the handle, so the reaper requests best-effort teardown before clearing it. The teardown API does
/// not return a durable cleanup receipt; successful spool cleanup does not prove namespace cleanup succeeded.
/// A nonterminal durable log-capture intent holds the spool beyond the ordinary age window: the raw files may be the
/// only source from which recovery can finish an Expected, Opened, or SourceFinalized capture after storage returns.
///
/// <para><b>Terminal-gated, not age-gated:</b> a live run has no <c>CompletedAt</c>, so the reaper can NEVER
/// touch a running run's spool however long it runs — which matters precisely because durable runs are meant
/// to be long-lived. Plus a containment guard: it only ever deletes a directory strictly under the spool
/// root, so a corrupt/forged handle path can't make it delete an arbitrary location.</para>
/// </summary>
public interface IAgentRunSpoolReaper
{
    /// <summary>Reclaim expired terminal spools with a matching launch host. Unknown ownership or failed filesystem cleanup retains the handle for retry. Returns the count whose cleanup handle was cleared.</summary>
    Task<int> ReapAsync(CancellationToken cancellationToken);
}

public sealed class AgentRunSpoolReaper : IAgentRunSpoolReaper, IScopedDependency
{
    /// <summary>
    /// Operator override (a TimeSpan, e.g. <c>"1.00:00:00"</c>) for how long a TERMINAL run's spool is kept
    /// before reaping; default 24h. Pinned by a test (Rule 8). The spool holds RAW (un-redacted) output, so a
    /// shorter window reduces raw-output-at-rest. An unsettled durable capture intent extends this window until
    /// its monotonic health state reaches a terminal outcome, because the spool may be its only replay source.
    /// </summary>
    public const string RetentionEnvVar = "CODESPACE_AGENT_RUN_SPOOL_RETENTION";

    private static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(6);

    /// <summary>Capture states that still own the raw spool as a recovery source. Terminal health states have already settled what survived and release this hold.</summary>
    internal static readonly AgentRunLogCaptureIntentState[] CaptureSourceHoldingStates = [AgentRunLogCaptureIntentState.Expected, AgentRunLogCaptureIntentState.Opened, AgentRunLogCaptureIntentState.SourceFinalized];

    /// <summary>Per-sweep cap so a large backlog can't run one tick forever; the next tick continues.</summary>
    public const int BatchSize = 200;

    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<AgentRunSpoolReaper> _logger;

    public AgentRunSpoolReaper(CodeSpaceDbContext db, ILogger<AgentRunSpoolReaper> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>The retention window — the env override parsed as a positive TimeSpan, else the 24h default. Internal + static so it's unit-pinned.</summary>
    internal static TimeSpan Retention =>
        TimeSpan.TryParse(Environment.GetEnvironmentVariable(RetentionEnvVar), out var v) && v > TimeSpan.Zero ? v : DefaultRetention;

    public async Task<int> ReapAsync(CancellationToken cancellationToken)
    {
        var now = await _db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = now - Retention;
        var host = LocalProcessRunner.CurrentHost;

        // Filter ownership BEFORE LIMIT: foreign and legacy handles must neither lose their only cleanup evidence
        // nor fill every local batch forever. An unstamped legacy handle has no provable host owner.
        var candidates = await _db.AgentRun.FromSqlInterpolated($"""
            SELECT agent_run.*, xmin FROM agent_run
            WHERE lower(runner_handle ->> 'launchHost') = lower({host})
            """).AsNoTracking()
            .Where(r => r.Status != AgentRunStatus.Queued && r.Status != AgentRunStatus.Running && r.CompletedAt != null && r.CompletedAt < cutoff && r.RunnerHandleJson != null
                && (r.SpoolCleanupNextAttemptAt == null || r.SpoolCleanupNextAttemptAt <= now)
                && !_db.AgentRunLogCaptureIntent.Any(intent => intent.TeamId == r.TeamId && intent.AgentRunId == r.Id && CaptureSourceHoldingStates.Contains(intent.State)))
            // Least-attempted eligible work first: a permanently bad oldest batch cannot regain the front of the
            // hourly queue as soon as its backoff expires. Completion time and id keep each attempt tier stable.
            .OrderBy(r => r.SpoolCleanupAttempts).ThenBy(r => r.SpoolCleanupNextAttemptAt).ThenBy(r => r.CompletedAt).ThenBy(r => r.Id)
            .Take(BatchSize)
            .Select(r => new CleanupCandidate(r.Id, r.RunnerHandleJson!, r.FenceEpoch, r.CompletedAt!.Value, r.SpoolCleanupAttempts))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var reaped = 0;

        foreach (var c in candidates)
            if (await ReapOneAsync(c, now, cancellationToken).ConfigureAwait(false))
                reaped++;

        if (reaped > 0)
            _logger.LogInformation("AgentRunSpoolReaper: reclaimed {Reaped} terminal-run spool(s)", reaped);

        return reaped;
    }

    private async Task<bool> ReapOneAsync(CleanupCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Recheck and lock before filesystem side effects. A candidate read is not authority to delete after a
        // handle replacement, retry-state advance, or lifecycle change. Concurrent reapers serialize on this row.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await _db.AgentRun.FromSqlInterpolated($"""
            SELECT agent_run.*, xmin FROM agent_run
            WHERE id = {candidate.Id} AND runner_handle = CAST({candidate.HandleJson} AS jsonb)
                AND fence_epoch = {candidate.FenceEpoch} AND completed_at = {candidate.CompletedAt}
                AND spool_cleanup_attempts = {candidate.Attempts} AND status NOT IN ('Queued', 'Running')
            FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (current == null) return false;

        // Candidate discovery is only an optimization. Recheck under the run's row lock before touching the
        // filesystem: the capture-admission trigger takes FOR SHARE before it verifies Running + fence, so this
        // FOR UPDATE orders against a racing admission and the following read sees its durable obligation.
        var captureHoldsSource = await _db.AgentRunLogCaptureIntent.AsNoTracking()
            .AnyAsync(intent => intent.TeamId == current.TeamId && intent.AgentRunId == current.Id && CaptureSourceHoldingStates.Contains(intent.State), cancellationToken).ConfigureAwait(false);
        if (captureHoldsSource) return false;

        var handle = TryDeserialize(candidate.HandleJson);
        if (string.IsNullOrWhiteSpace(handle?.LaunchHost) || !string.Equals(handle.LaunchHost, LocalProcessRunner.CurrentHost, StringComparison.OrdinalIgnoreCase))
            return await ScheduleRetryAsync(candidate, now, "invalid-runner-handle", transaction, cancellationToken).ConfigureAwait(false);
        if (!IsUnderSpoolRoot(handle.SpoolDirectory))
            return await ScheduleRetryAsync(candidate, now, "invalid-spool-path", transaction, cancellationToken).ConfigureAwait(false);

        // Enumerate the entire family before deleting anything. A refused directory listing cannot prove earlier
        // rounds are absent. Keep the handle through partial deletion so the next sweep can finish the remainder.
        try
        {
            var directories = RoundSpoolFamily(candidate.Id).Append(handle.SpoolDirectory).Distinct(StringComparer.Ordinal).ToArray();
            if (directories.Any(dir => !IsUnderSpoolRoot(dir)))
                return await ScheduleRetryAsync(candidate, now, "invalid-spool-path", transaction, cancellationToken).ConfigureAwait(false);
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { Directory.Delete(directory, recursive: true); }
                catch (DirectoryNotFoundException) { /* an earlier sweep already removed this exact directory */ }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "AgentRunSpoolReaper: retaining cleanup handle for run {RunId} after filesystem cleanup failed", candidate.Id);
            var errorCode = exception is UnauthorizedAccessException ? "filesystem-access" : "filesystem-io";
            return await ScheduleRetryAsync(candidate, now, errorCode, transaction, cancellationToken).ConfigureAwait(false);
        }

        // Backstop the durable runner's per-terminal-path filtered-egress netns teardown (B3.2b): a run that reached
        // terminal via a path that SKIPPED it — most notably a re-attach that could only complete from the exit marker
        // (e.g. after the run's credential rotated) — would otherwise leave its netns/veth/nft-table on the host with
        // no other reaper, and it leaks permanently once the handle below is cleared. This is the LAST point the key is
        // available, so tear it down here. Best-effort + idempotent: a no-op when the fast path already freed it, when
        // the run had no netns, or on a host without ip/nft (the executor swallows each failed command).
        if (handle?.EgressNetnsKey is { Length: > 0 } netnsKey)
            await FilteredEgressNetns.TeardownAsync(netnsKey, cancellationToken).ConfigureAwait(false);

        // Same backstop for the run's cgroup-v2 resource-cap leaf (B4) — reconstructed from the persisted key + the
        // operator's configured root. Best-effort + idempotent: a no-op when the fast path already reaped it, the run
        // had no cap, or no root is configured.
        if (handle?.CgroupRunKey is { Length: > 0 } cgroupKey && CgroupResourceLimit.CgroupRoot is { } cgroupRoot)
            await CgroupResourceLimit.TeardownAsync(cgroupRoot, cgroupKey, cancellationToken).ConfigureAwait(false);

        // A crash or DB failure after deletion leaves this exact handle intact; a later sweep observes the
        // missing directories and can finish. Never clear a replacement handle or a newly active lifecycle.
        var cleared = await _db.AgentRun
            .Where(r => r.Id == candidate.Id && r.RunnerHandleJson == candidate.HandleJson && r.FenceEpoch == candidate.FenceEpoch && r.CompletedAt == candidate.CompletedAt
                && r.SpoolCleanupAttempts == candidate.Attempts && r.Status != AgentRunStatus.Queued && r.Status != AgentRunStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RunnerHandleJson, (string?)null)
                .SetProperty(r => r.SpoolCleanupAttempts, 0)
                .SetProperty(r => r.SpoolCleanupLastAttemptAt, (DateTimeOffset?)null)
                .SetProperty(r => r.SpoolCleanupNextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(r => r.SpoolCleanupLastErrorCode, (string?)null), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return cleared == 1;
    }

    private async Task<bool> ScheduleRetryAsync(CleanupCandidate candidate, DateTimeOffset now, string errorCode, IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        var nextAttempts = candidate.Attempts == int.MaxValue ? int.MaxValue : candidate.Attempts + 1;
        var nextAttemptAt = now + RetryDelay(candidate.Id, nextAttempts);
        var changed = await _db.AgentRun
            .Where(r => r.Id == candidate.Id && r.RunnerHandleJson == candidate.HandleJson && r.FenceEpoch == candidate.FenceEpoch && r.CompletedAt == candidate.CompletedAt
                && r.SpoolCleanupAttempts == candidate.Attempts && r.Status != AgentRunStatus.Queued && r.Status != AgentRunStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SpoolCleanupAttempts, nextAttempts)
                .SetProperty(r => r.SpoolCleanupLastAttemptAt, now)
                .SetProperty(r => r.SpoolCleanupNextAttemptAt, nextAttemptAt)
                .SetProperty(r => r.SpoolCleanupLastErrorCode, errorCode), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 1)
            _logger.LogWarning("AgentRunSpoolReaper: deferred cleanup for run {RunId} after {ErrorCode}; attempt {Attempt} is due at {NextAttemptAt}", candidate.Id, errorCode, nextAttempts, nextAttemptAt);
        return false;
    }

    /// <summary>Stable per-run jitter spreads retry storms while preserving monotonic exponential backoff and a hard cap.</summary>
    internal static TimeSpan RetryDelay(Guid runId, int attempt)
    {
        var exponent = Math.Clamp((long)attempt - 1, 0, 9);
        var unjitteredSeconds = BaseRetryDelay.TotalSeconds * (1L << (int)exponent);
        var jitter = 1d + runId.ToByteArray()[0] / 255d * 0.2d;
        return TimeSpan.FromSeconds(Math.Min(MaxRetryDelay.TotalSeconds, unjitteredSeconds * jitter));
    }

    private static SandboxHandle? TryDeserialize(string handleJson)
    {
        try { return JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Every spool directory a run can have left behind across revise rounds. A missing root is already clean; every other enumeration error must preserve the cleanup handle.</summary>
    internal static IReadOnlyList<string> RoundSpoolFamily(Guid runId)
    {
        var root = LocalProcessRunner.SpoolRoot();
        var family = new List<string> { Path.Combine(root, runId.ToString("N")) };

        try { family.AddRange(Directory.GetDirectories(root, $"{runId:N}-r*")); }
        catch (DirectoryNotFoundException) { /* already removed */ }

        return family;
    }

    /// <summary>Containment guard (security-critical, so unit-pinned): true only when <paramref name="dir"/> is strictly UNDER the spool root — never the root itself, never an arbitrary path a corrupt handle might carry.</summary>
    internal static bool IsUnderSpoolRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        try
        {
            var root = Path.GetFullPath(LocalProcessRunner.SpoolRoot());
            var full = Path.GetFullPath(dir);
            return full.Length > root.Length && full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    private sealed record CleanupCandidate(Guid Id, string HandleJson, long FenceEpoch, DateTimeOffset CompletedAt, int Attempts);
}
