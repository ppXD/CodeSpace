using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// EF-backed <see cref="IModelPoolSelector"/>. Queries the team's credentialed-model pool for a qualifying model,
/// applies the provider / structured / allowed-pool / pin bounds, decrypts the chosen row's backing credential just-in-
/// time, and returns the model id + key. Pure pool-driven: no env read, no hardcoded system default — an UNPINNED auto
/// pick is ordered by the operator's per-credential default (<c>IsDefault</c>, #746) first, then model id / row id, so a
/// starred model drives the brain plane (planner / supervisor / synthesis) the same way it already drives the agent plane.
/// </summary>
public sealed class ModelPoolSelector : IModelPoolSelector, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPayloadEncryptor _encryptor;

    public ModelPoolSelector(CodeSpaceDbContext db, IPayloadEncryptor encryptor)
    {
        _db = db;
        _encryptor = encryptor;
    }

    public async Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken)
    {
        // The pool is the team's ENABLED credentialed models under an ACTIVE credential FOR THE PROVIDER the client
        // serves (so the key authenticates that API). GENERIC — no capability gate: structured output is the client's
        // job (it degrades to a prompt-only JSON floor). Provider + model-id matching is CASE-INSENSITIVE (parity with
        // the agent-side resolver + the clamp).
        var providerLower = provider.ToLower();

        var query = _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled
                && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active
                && m.Credential.Provider.ToLower() == providerLower);

        var pin = NullIfBlank(pinnedModel)?.ToLower();
        var allowed = allowedModels is { Count: > 0 } ? allowedModels.Select(a => a.Trim().ToLower()).ToList() : null;

        // The pin wins (the caller chose ONE model — it must be in the pool); else the allowed pool bounds it
        // (empty/null = all the team's qualifying models).
        if (pin != null)
            query = query.Where(m => m.ModelId.ToLower() == pin);
        else if (allowed != null)
            query = query.Where(m => allowed.Contains(m.ModelId.ToLower()));

        // Precedence ladder for an UNPINNED auto pick: the operator's per-credential default (IsDefault, #746) wins
        // first; then the cached capability TIER (frontier > strong > basic > unknown — "auto = the strongest available
        // brain"); then a deterministic total order (model id, then row id) for stability across two credentials of the
        // same model id. Mirrors the agent plane (ModelCredentialResolver) so a starred model drives every auto pick.
        // The supervisor brain is picked by row id (ResolveByRowIdAsync); this name/provider path decides the ambient
        // picks. Ordered IN-MEMORY because capability_tier is stored as TEXT — a DB ORDER BY would sort it alphabetically,
        // not by rank — over the (small per-team) candidate pool. The model-id tie-break uses StringComparer.Ordinal ON
        // PURPOSE: it is locale-INDEPENDENT, so the pick is identical in CI and locally (the prior DB-collation tie-break
        // could differ across environments — see LlmCompleteUsageFlowTests). Advisory ordering only — never a filter.
        var candidates = await query
            .Select(m => new { m.ModelId, m.IsDefault, m.CapabilityTier, m.Credential.Provider, m.Credential.EncryptedApiKey, m.Credential.BaseUrl, m.Id })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var row = candidates
            .OrderByDescending(m => m.IsDefault)
            .ThenByDescending(m => (int)(m.CapabilityTier ?? ModelCapabilityTier.Unknown))
            .ThenBy(m => m.ModelId, StringComparer.Ordinal)
            .ThenBy(m => m.Id)
            .FirstOrDefault();

        if (row == null) return null;

        return ToPick(row.ModelId, row.Provider, row.EncryptedApiKey, row.BaseUrl);
    }

    public async Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken)
    {
        // The operator picked ONE exact row (the brain model) — resolve it under the same team / enabled / active guards
        // as the pool query, so a missing / disabled / revoked / cross-team row fails closed rather than running an
        // unbacked model. No capability gate: the structured client degrades a non-structured model at call time.
        var row = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Id == modelCredentialModelId && m.Enabled
                && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active)
            .Select(m => new { m.ModelId, m.Credential.Provider, m.Credential.EncryptedApiKey, m.Credential.BaseUrl })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row == null) return null;

        return ToPick(row.ModelId, row.Provider, row.EncryptedApiKey, row.BaseUrl);
    }

    public async Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken)
    {
        // The L4 brain authored a model NAME for this agent — resolve it to a credentialed row the team owns, bounded by
        // the operator's allowed pool (empty = ALL team rows). Case-insensitive name match (parity with the in-process
        // path). No structured requirement — a coding agent doesn't need structured output. None → null (fail closed).
        var nameLower = modelName.Trim().ToLower();

        var query = _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.ModelId.ToLower() == nameLower
                && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active);

        if (allowedRowIds is { Count: > 0 }) query = query.Where(m => allowedRowIds.Contains(m.Id));

        // Tie-break by row id for a deterministic pick when two credentials of the team back the same model id.
        var row = await query
            .OrderBy(m => m.Id)
            .Select(m => new { m.ModelId, m.ModelCredentialId, m.Credential.Provider })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return row == null ? null : new ModelDispatchRef { ModelId = row.ModelId, ModelCredentialId = row.ModelCredentialId, Provider = row.Provider };
    }

    public async Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken)
    {
        var query = _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active);

        if (allowedRowIds is { Count: > 0 }) query = query.Where(m => allowedRowIds.Contains(m.Id));

        // Project (id, provider, tier) on the DB side; dedupe + total-order + map in memory (a team's pool is small, and
        // EF can't translate GroupBy→OrderBy over a constructed record). A model name can exist under two providers (both
        // harnesses list Custom) AND under two credentials (each tiered separately), so the (id, provider) pair is the
        // dedup key and the BEST (highest) tier across its rows is carried (a model's capability is its own, not the
        // credential's). The render order intentionally stays ALPHABETICAL and does NOT surface IsDefault or the tier —
        // the operator default orders the PICK (SelectAsync / SelectBrainRowIdAsync), not this displayed menu, and
        // tier-aware ORDERING of this catalog is a later slice; this slice only surfaces the tier as a render hint.
        var rows = await query
            .Select(m => new { m.ModelId, m.Credential.Provider, m.CapabilityTier })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => new { r.ModelId, r.Provider })
            .Select(g => new PoolModelInfo(g.Key.ModelId, g.Key.Provider, g.Max(r => r.CapabilityTier ?? ModelCapabilityTier.Unknown)))
            .OrderBy(p => p.ModelId, StringComparer.Ordinal).ThenBy(p => p.Provider, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken)
    {
        if (eligibleProviders.Count == 0) return null;

        // Lower-case the eligible providers for a case-insensitive provider match (parity with the rest of the selector),
        // matched in-memory: the small set isn't worth an EF Contains over a constructed list, and a structured-provider
        // set is a handful of names. The DB query is the same active-credential / enabled predicate as the pool.
        var eligible = eligibleProviders.Select(p => p.ToLower()).ToHashSet();

        var rows = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active)
            .Select(m => new { m.Id, m.ModelId, m.IsDefault, m.CapabilityTier, Provider = m.Credential.Provider })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The highest-precedence STRUCTURED-eligible row — the operator's default (IsDefault, #746) first, then the cached
        // capability TIER (frontier > strong > basic > unknown — "auto = the strongest available brain"), then model id /
        // row id — so a starred brain wins, else the strongest, else alphabetical (a non-eligible row is excluded). A
        // replay re-derives the SAME brain (a stable total order over a frozen pool snapshot). Ordered IN-MEMORY because
        // capability_tier is stored as TEXT (a DB ORDER BY would sort it alphabetically, not by rank).
        return rows
            .Where(r => eligible.Contains(r.Provider.ToLower()))
            .OrderByDescending(r => r.IsDefault)
            .ThenByDescending(r => (int)(r.CapabilityTier ?? ModelCapabilityTier.Unknown))
            .ThenBy(r => r.ModelId, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefault();
    }

    public async Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken)
    {
        if (eligibleProviders.Count == 0) return null;

        // The operator's brain pin is honored IFF it's a real ENABLED row under an ACTIVE team credential whose provider a
        // structured client serves — the SAME guards as the auto brain pick (SelectBrainRowIdAsync), so a pinned and an
        // auto brain are interchangeable. We read only the provider (the cheapest check) and compare it case-insensitively
        // in-memory against the eligible set (a handful of structured-provider names). A missing / disabled / revoked /
        // cross-team / non-structured pin yields null → the caller falls back to auto (never bakes a NoBrainModelStop brain).
        var provider = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Id == modelCredentialModelId && m.Enabled
                && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active)
            .Select(m => m.Credential.Provider)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var eligible = eligibleProviders.Select(p => p.ToLower()).ToHashSet();

        return provider != null && eligible.Contains(provider.ToLower()) ? modelCredentialModelId : null;
    }

    private ModelPoolPick ToPick(string modelId, string provider, string? encryptedApiKey, string? baseUrl) => new()
    {
        ModelId = modelId,
        Credential = new ResolvedModelCredential
        {
            Provider = provider,
            ApiKey = string.IsNullOrEmpty(encryptedApiKey) ? null : _encryptor.Decrypt(encryptedApiKey),
            BaseUrl = baseUrl,
        },
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
