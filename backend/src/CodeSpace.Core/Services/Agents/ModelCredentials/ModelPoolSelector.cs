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
/// time, and returns the model id + key. Pure pool-driven: no env read, no default model.
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

    public async Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, bool requireStructured, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken)
    {
        // The pool is the team's ENABLED credentialed models under an ACTIVE credential FOR THE PROVIDER the client
        // serves (so the key authenticates that API), narrowed to structured-capable when the caller needs structured
        // output. Provider + model-id matching is CASE-INSENSITIVE (parity with the agent-side resolver + the clamp).
        var providerLower = provider.ToLower();

        var query = _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && (!requireStructured || m.SupportsStructuredOutput)
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

        // Deterministic total order (model id, then row id) so an unpinned pick is stable even when two credentials of
        // the same provider carry the same model id. The supervisor brain is picked by row id (ResolveByRowIdAsync), so
        // this name/provider path only decides an unpinned/ambient pick (planner / synthesis / llm.complete).
        var row = await query
            .OrderBy(m => m.ModelId)
            .ThenBy(m => m.Id)
            .Select(m => new { m.ModelId, m.Credential.Provider, m.Credential.EncryptedApiKey, m.Credential.BaseUrl })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row == null) return null;

        return ToPick(row.ModelId, row.Provider, row.EncryptedApiKey, row.BaseUrl);
    }

    public async Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, bool requireStructured, CancellationToken cancellationToken)
    {
        // The operator picked ONE exact row (the brain model) — resolve it under the same team / enabled / active /
        // structured guards as the pool query, so a missing / disabled / revoked / non-structured / cross-team row
        // fails closed rather than running an unbacked or wrong-capability model.
        var row = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Id == modelCredentialModelId && m.Enabled && (!requireStructured || m.SupportsStructuredOutput)
                && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active)
            .Select(m => new { m.ModelId, m.Credential.Provider, m.Credential.EncryptedApiKey, m.Credential.BaseUrl })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row == null) return null;

        return ToPick(row.ModelId, row.Provider, row.EncryptedApiKey, row.BaseUrl);
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
