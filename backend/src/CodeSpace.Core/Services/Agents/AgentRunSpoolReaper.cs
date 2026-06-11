using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Reclaims the on-disk spool of agent runs that have FINISHED past a retention window — the disk
/// counterpart to <c>IWorkspaceJanitor</c>. The durable runner writes each run's stdout/stderr/exit/pid to a
/// spool directory so a restart can recover/re-attach it; once the run is terminal that spool is debris (its
/// redacted output is already in the append-only event log), so this ages it out.
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
        // Delete the spool dir ONLY when it's strictly under the spool root (else a forged/corrupt handle path
        // could point anywhere). A gone / out-of-root / unparseable handle just skips the delete.
        if (TryResolveSpoolDir(handleJson) is { } dir && IsUnderSpoolRoot(dir))
            DeleteQuietly(dir);

        // Clear the handle regardless (the spool is reclaimed or irrelevant, and a terminal run never re-attaches)
        // so the run drops out of the candidate set and isn't re-processed every sweep.
        var cleared = await _db.AgentRun
            .Where(r => r.Id == runId && r.RunnerHandleJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RunnerHandleJson, (string?)null), cancellationToken)
            .ConfigureAwait(false);

        return cleared == 1;
    }

    private static string? TryResolveSpoolDir(string handleJson)
    {
        try { return JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options)?.SpoolDirectory; }
        catch (JsonException) { return null; }
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
