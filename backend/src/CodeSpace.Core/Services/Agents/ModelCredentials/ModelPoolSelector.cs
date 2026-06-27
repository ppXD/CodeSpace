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
        // first, then a deterministic total order (model id, then row id) so the pick is stable even when two credentials
        // of the same provider carry the same model id. Mirrors the agent plane (ModelCredentialResolver) so a starred
        // model drives EVERY auto pick — the brain (planner / synthesis / llm.complete) included, not just agents. The
        // supervisor brain is picked by row id (ResolveByRowIdAsync); this name/provider path decides the ambient picks.
        var row = await query
            .OrderByDescending(m => m.IsDefault)
            .ThenBy(m => m.ModelId)
            .ThenBy(m => m.Id)
            .Select(m => new { m.ModelId, m.Credential.Provider, m.Credential.EncryptedApiKey, m.Credential.BaseUrl })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

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

        // Project the two columns on the DB side; dedupe + total-order + map in memory (a team's pool is small, and EF
        // can't translate Distinct→OrderBy over a constructed record). A model name can exist under two providers (both
        // harnesses list Custom), so the (id, provider) pair is the dedup key + the stable render order. The catalog
        // render order intentionally stays ALPHABETICAL and does NOT surface IsDefault — the operator default orders the
        // PICK (SelectAsync / SelectBrainRowIdAsync), not this displayed menu; capability-aware ordering of this catalog
        // is the follow-up (when the brain authors against a tier/score signal, not just id+provider).
        var rows = await query
            .Select(m => new { m.ModelId, m.Credential.Provider })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Distinct()
            .OrderBy(r => r.ModelId, StringComparer.Ordinal).ThenBy(r => r.Provider, StringComparer.Ordinal)
            .Select(r => new PoolModelInfo(r.ModelId, r.Provider))
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
            .OrderByDescending(m => m.IsDefault).ThenBy(m => m.ModelId).ThenBy(m => m.Id)
            .Select(m => new { m.Id, Provider = m.Credential.Provider })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // First STRUCTURED-eligible row in precedence order — the operator's default (IsDefault, #746) first, then model
        // id / row id — so a starred brain model wins the auto pick (a non-eligible default is skipped to the next). A
        // replay re-derives the SAME brain (the order is a stable total order over a frozen pool snapshot).
        return rows.FirstOrDefault(r => eligible.Contains(r.Provider.ToLower()))?.Id;
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
