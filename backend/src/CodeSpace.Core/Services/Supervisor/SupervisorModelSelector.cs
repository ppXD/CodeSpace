using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// EF-backed <see cref="ISupervisorModelSelector"/>. Queries the team's credentialed-model pool (the
/// <c>ModelCredentialModel</c> rows S1–S3 build) for a structured-capable model the brain can run, applies the
/// operator's pool + pin bounds, decrypts the chosen row's backing credential just-in-time, and returns the
/// model id + key. Pure pool-driven: no env read, no default model.
/// </summary>
public sealed class SupervisorModelSelector : ISupervisorModelSelector, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPayloadEncryptor _encryptor;

    public SupervisorModelSelector(CodeSpaceDbContext db, IPayloadEncryptor encryptor)
    {
        _db = db;
        _encryptor = encryptor;
    }

    public async Task<SupervisorModelPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken)
    {
        // The brain needs structured output, so the pool is the team's ENABLED, structured-capable credentialed models
        // under an ACTIVE credential FOR THE PROVIDER the structured client serves (so the key authenticates that API).
        // Provider + model-id matching is CASE-INSENSITIVE, consistent with the agent-side resolver + SupervisorModelClamp.
        var providerLower = provider.ToLower();

        var query = _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.SupportsStructuredOutput
                && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active
                && m.Credential.Provider.ToLower() == providerLower);

        var pin = NullIfBlank(pinnedModel)?.ToLower();
        var allowed = allowedModels is { Count: > 0 } ? allowedModels.Select(a => a.Trim().ToLower()).ToList() : null;

        // The pin wins (the operator chose ONE model — it must be in the pool); else the allowed pool bounds it
        // (empty/null = all the team's qualifying models).
        if (pin != null)
            query = query.Where(m => m.ModelId.ToLower() == pin);
        else if (allowed != null)
            query = query.Where(m => allowed.Contains(m.ModelId.ToLower()));

        // Prefer a supervisor-recommended model; tie-break by model id, then row id for a TOTAL order so the pick is
        // stable even when two credentials of the same provider carry the same recommended model.
        var row = await query
            .OrderByDescending(m => m.RecommendedForSupervisor)
            .ThenBy(m => m.ModelId)
            .ThenBy(m => m.Id)
            .Select(m => new { m.ModelId, m.Credential.Provider, m.Credential.EncryptedApiKey, m.Credential.BaseUrl })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row == null) return null;

        return new SupervisorModelPick
        {
            ModelId = row.ModelId,
            Credential = new ResolvedModelCredential
            {
                Provider = row.Provider,
                ApiKey = string.IsNullOrEmpty(row.EncryptedApiKey) ? null : _encryptor.Decrypt(row.EncryptedApiKey),
                BaseUrl = row.BaseUrl,
            },
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
