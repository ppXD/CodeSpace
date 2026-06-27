using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// EF-backed <see cref="IModelCapabilityTieringService"/>. Mirrors <c>LlmEffortClassifier</c>'s resolve → structured-call
/// → fail-closed shape: resolve a structured client + pool model for the team (<see cref="InProcessStructuredModel"/>),
/// send the batch of un-tiered ids constrained by <see cref="ModelTieringSchema"/>, and write each verdict (incl. a
/// brain-authored <c>unknown</c> for an opaque id) to <c>capability_tier</c> + <c>last_tiered_at</c>. Scoped (DbContext +
/// the pool selector are per-request).
/// </summary>
public sealed class ModelCapabilityTieringService : IModelCapabilityTieringService, IScopedDependency
{
    private const int MaxBatch = 50;   // a team's pool is small; bound the batch so the reply fits the output budget (no truncation-drop)

    /// <summary>Back-off before re-attempting a model that stayed un-tiered after an attempt (the brain omitted it, or the reply was empty) — so a stuck id isn't re-tiered every tick, but a transient miss (e.g. a one-off truncation) recovers on the next day's tick.</summary>
    private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);

    private readonly ILLMClientRegistry _clients;
    private readonly IModelPoolSelector _models;
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<ModelCapabilityTieringService> _logger;

    public ModelCapabilityTieringService(ILLMClientRegistry clients, IModelPoolSelector models, CodeSpaceDbContext db, ILogger<ModelCapabilityTieringService> logger)
    {
        _clients = clients;
        _models = models;
        _db = db;
        _logger = logger;
    }

    public async Task TierTeamAsync(Guid teamId, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await PendingRows(teamId)
                .OrderBy(m => m.ModelId).ThenBy(m => m.Id)   // deterministic batch (reproducible which rows a tick tiers)
                .Take(MaxBatch)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (rows.Count == 0) return;

            var ids = rows.Select(r => r.ModelId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (await InProcessStructuredModel.ResolveAsync(_clients, _models, teamId, cancellationToken).ConfigureAwait(false) is not { } resolved)
                return;   // no structured provider with a team model → nothing to tier with; leave un-tiered (fail-closed)

            var (structured, pick) = resolved;

            var completion = await structured.CompleteStructuredAsync(BuildRequest(pick, ids), cancellationToken).ConfigureAwait(false);

            var byId = (completion.Json.Deserialize<ModelTierAssignments>(ModelTieringSchema.Options)?.Models ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Id))
                .GroupBy(a => a.Id!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => ModelTieringSchema.ParseTier(g.First().Tier), StringComparer.OrdinalIgnoreCase);

            // Stamp last_tiered_at on EVERY row we attempted — a verdict sets the tier; an id the brain OMITTED (or a
            // degenerate empty reply) leaves the tier null but records the attempt, so the back-off gate skips it next
            // tick instead of re-firing a live call forever (it is re-tried once RetryWindow elapses, when a transient
            // truncation may have cleared). This makes the call a true cached fact, steady-state a no-op.
            var now = DateTimeOffset.UtcNow;

            foreach (var row in rows)
            {
                row.LastTieredAt = now;

                if (byId.TryGetValue(row.ModelId.Trim(), out var tier)) row.CapabilityTier = tier;
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tiering is an ADVISORY enrichment — any miss (a keyless credential the client rejects, a transport/gateway
            // fault, a malformed reply) leaves the rows un-tiered (NULL) and the catalog byte-identical. Never crash the
            // caller (the backfill tick). A genuine cancellation propagates. (A throw is NOT stamped, so a transient fault
            // re-tries next tick; a persistent one is the operator's to fix — the cost is bounded to one call per tick.)
            _logger.LogWarning(ex, "Capability tiering for team {TeamId} failed; leaving its models un-tiered (advisory hint only)", teamId);
        }
    }

    public async Task<int> TierAllPendingAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - RetryWindow;

        var teamIds = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active
                && m.CapabilityTier == null && (m.LastTieredAt == null || m.LastTieredAt < cutoff))
            .Select(m => m.Credential.TeamId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var teamId in teamIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TierTeamAsync(teamId, cancellationToken).ConfigureAwait(false);
        }

        return teamIds.Count;
    }

    /// <summary>
    /// The team's tiering CANDIDATES (TRACKED, so the write persists): ENABLED rows under an ACTIVE credential whose
    /// <c>capability_tier</c> is still NULL AND that were not attempted within <see cref="RetryWindow"/> (the <c>last_tiered_at</c>
    /// back-off). A row that already has a verdict (incl. a cached <c>Unknown</c> for an opaque id, the later objective
    /// probe's job) is skipped; a row that was attempted but got NO verdict backs off rather than re-firing every tick.
    /// </summary>
    private IQueryable<Persistence.Entities.ModelCredentialModel> PendingRows(Guid teamId)
    {
        var cutoff = DateTimeOffset.UtcNow - RetryWindow;

        return _db.ModelCredentialModel
            .Where(m => m.Enabled && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active
                && m.CapabilityTier == null && (m.LastTieredAt == null || m.LastTieredAt < cutoff));
    }

    private static StructuredLLMCompletionRequest BuildRequest(ModelPoolPick pick, IReadOnlyList<string> ids)
    {
        var user = new StringBuilder("Tier each of these model ids:\n");
        foreach (var id in ids) user.Append("  - ").AppendLine(id);

        return new StructuredLLMCompletionRequest
        {
            Model = pick.ModelId,
            SystemPrompt = SystemPrompt,
            UserPrompt = user.ToString(),
            JsonSchema = ModelTieringSchema.ResponseSchema,
            MaxOutputTokens = 8192,   // ample for a MaxBatch-sized {id,tier} list — no truncation-drop of trailing ids
            Temperature = 0.0,
            Credential = pick.Credential,
        };
    }

    private const string SystemPrompt =
        "You tier LLM models by their general CODING capability, judging from the model id ALONE. " +
        "For each id return one tier: 'frontier' (the strongest current coding models), 'strong' (capable mid-tier), " +
        "'basic' (small / weak / older), or 'unknown' if the id is an opaque or renamed alias you genuinely do not " +
        "recognise — do NOT guess for an unrecognisable id. Return one entry per id given, using the id verbatim. " +
        "Return ONLY the schema-constrained JSON.";
}
