using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Reclaims the host resources of agent runs that have FINISHED past a retention window — the disk
/// counterpart to <c>IWorkspaceJanitor</c>. The durable runner writes each run's stdout/stderr/exit/pid to a
/// spool directory so a restart can recover/re-attach it; once the run is terminal that spool is debris (its
/// redacted output is already in the append-only event log), so this ages it out. It is ALSO the backstop for the
/// durable runner's filtered-egress netns (B3.2b): a run that reached terminal via a path that skipped the runner's
/// per-terminal teardown (most notably a re-attach that could only complete from the exit marker) still carries its
/// netns key on the handle, so the reaper tears that netns down from the handle before clearing it — the last point
/// the key is available, the guarantee against a permanently-leaked namespace.
///
/// <para><b>Terminal-gated, not age-gated:</b> a live run has no <c>CompletedAt</c>, so the reaper can NEVER
/// touch a running run's spool however long it runs — which matters precisely because durable runs are meant
/// to be long-lived. Plus a containment guard: it only ever deletes a directory strictly under the spool
/// root, so a corrupt/forged handle path can't make it delete an arbitrary location.</para>
/// </summary>
public interface IAgentRunSpoolReaper
{
    /// <summary>Delete the spool directory of every terminal run whose CompletedAt is older than the retention window, then clear its handle so it isn't re-swept. Best-effort + idempotent + safe from multiple replicas. Returns the count reaped.</summary>
    Task<int> ReapAsync(CancellationToken cancellationToken);
}

public sealed class AgentRunSpoolReaper : IAgentRunSpoolReaper, IScopedDependency
{
    /// <summary>
    /// Operator override (a TimeSpan, e.g. <c>"1.00:00:00"</c>) for how long a TERMINAL run's spool is kept
    /// before reaping; default 24h. Pinned by a test (Rule 8). The spool holds RAW (un-redacted) output, so a
    /// shorter window reduces raw-output-at-rest; the durable event log already has the redacted copy, so
    /// recovery/re-attach never needs a terminal run's spool.
    /// </summary>
    public const string RetentionEnvVar = "CODESPACE_AGENT_RUN_SPOOL_RETENTION";

    private static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

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
        var cutoff = DateTimeOffset.UtcNow - Retention;

        // Terminal (CompletedAt is set only on a terminal flip) + old enough + still carries a handle (= not yet
        // reaped). A live Running run has a null CompletedAt, so it never enters this set no matter how long it runs.
        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(r => r.CompletedAt != null && r.CompletedAt < cutoff && r.RunnerHandleJson != null)
            .OrderBy(r => r.CompletedAt)
            .Take(BatchSize)
            .Select(r => new { r.Id, r.RunnerHandleJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var reaped = 0;

        foreach (var c in candidates)
            if (await ReapOneAsync(c.Id, c.RunnerHandleJson!, cancellationToken).ConfigureAwait(false))
                reaped++;

        if (reaped > 0)
            _logger.LogInformation("AgentRunSpoolReaper: reclaimed {Reaped} terminal-run spool(s)", reaped);

        return reaped;
    }

    private async Task<bool> ReapOneAsync(Guid runId, string handleJson, CancellationToken cancellationToken)
    {
        var handle = TryDeserialize(handleJson);

        // Delete the spool dir ONLY when it's strictly under the spool root (else a forged/corrupt handle path
        // could point anywhere). A gone / out-of-root / unparseable handle just skips the delete.
        if (handle?.SpoolDirectory is { } dir && IsUnderSpoolRoot(dir))
            DeleteQuietly(dir);

        // S6: a revised run leaves one spool PER ROUND (ReviseSpoolKey) and the handle points only at the LAST
        // round's — so sweep the run's whole spool family (the bare round-0 dir + every "-rN" sibling) through the
        // same containment guard. Without this, every earlier round's raw un-redacted output + per-round config home
        // (MCP token declaration, session transcript) would sit on disk forever, defeating the retention window.
        foreach (var sibling in RoundSpoolFamily(runId))
            if (IsUnderSpoolRoot(sibling))
                DeleteQuietly(sibling);

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

        // Clear the handle regardless (the spool is reclaimed or irrelevant, and a terminal run never re-attaches)
        // so the run drops out of the candidate set and isn't re-processed every sweep.
        var cleared = await _db.AgentRun
            .Where(r => r.Id == runId && r.RunnerHandleJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RunnerHandleJson, (string?)null), cancellationToken)
            .ConfigureAwait(false);

        return cleared == 1;
    }

    private static SandboxHandle? TryDeserialize(string handleJson)
    {
        try { return JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Every spool directory a run can have left behind across S6 revise rounds: the bare run-key dir (round 0) plus every existing <c>-rN</c> suffixed sibling. Computed from the RUN ID — not the handle — so earlier rounds are found even though the handle points only at the last one. Best-effort enumeration; internal so it's unit-pinned.</summary>
    internal static IReadOnlyList<string> RoundSpoolFamily(Guid runId)
    {
        var root = LocalProcessRunner.SpoolRoot();
        var family = new List<string> { Path.Combine(root, runId.ToString("N")) };

        try { if (Directory.Exists(root)) family.AddRange(Directory.GetDirectories(root, $"{runId:N}-r*")); }
        catch (Exception) { /* enumeration is best-effort — the handle-pointed dir already got its targeted delete */ }

        return family;
    }

    /// <summary>Containment guard (security-critical, so unit-pinned): true only when <paramref name="dir"/> is strictly UNDER the spool root — never the root itself, never an arbitrary path a corrupt handle might carry.</summary>
    internal static bool IsUnderSpoolRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        var root = Path.GetFullPath(LocalProcessRunner.SpoolRoot());
        var full = Path.GetFullPath(dir);

        return full.Length > root.Length && full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void DeleteQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "AgentRunSpoolReaper: failed to delete spool dir {Dir}", dir); }
    }
}
