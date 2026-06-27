using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.ModelCredentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

public sealed class ModelCredentialService : IModelCredentialService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly IPayloadEncryptor _encryptor;
    private readonly IModelReflector _reflector;

    public ModelCredentialService(CodeSpaceDbContext db, ICurrentTeam currentTeam, IPayloadEncryptor encryptor, IModelReflector reflector)
    {
        _db = db;
        _currentTeam = currentTeam;
        _encryptor = encryptor;
        _reflector = reflector;
    }

    public async Task<IReadOnlyList<ModelCredentialSummary>> ListAsync(string? provider, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var query = _db.ModelCredential.AsNoTracking().Where(c => c.TeamId == teamId && c.DeletedDate == null);

        if (!string.IsNullOrWhiteSpace(provider)) query = query.Where(c => c.Provider == provider);

        var rows = await query.OrderByDescending(c => c.CreatedDate).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Derive the masked hint transiently (decrypt → last 4 → discard); a team has few credentials.
        return rows.Select(ToSummary).ToList();
    }

    public async Task<Guid> AddAsync(string provider, string displayName, string? apiKey, string? baseUrl, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var credential = new ModelCredential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = provider,
            DisplayName = displayName,
            EncryptedApiKey = EncryptOrNull(apiKey),
            BaseUrl = NullIfBlank(baseUrl),
            Status = CredentialStatus.Active,
        };

        await _db.ModelCredential.AddAsync(credential, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return credential.Id;
    }

    public async Task<Guid> UpdateAsync(Guid id, string displayName, string? apiKey, string? baseUrl, CancellationToken cancellationToken)
    {
        var credential = await LoadActiveAsync(id, cancellationToken).ConfigureAwait(false);

        credential.DisplayName = displayName;
        credential.BaseUrl = NullIfBlank(baseUrl);

        // Write-only secret: a blank key keeps the existing one; a value rotates it.
        if (!string.IsNullOrWhiteSpace(apiKey)) credential.EncryptedApiKey = _encryptor.Encrypt(apiKey);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return credential.Id;
    }

    public async Task<Guid> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var credential = await _db.ModelCredential.FirstOrDefaultAsync(c => c.Id == id && c.TeamId == teamId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Model credential {id} not found.");

        // Clear the key as well as soft-deleting — once dropped, nothing can present it even if a row lingers.
        credential.Status = CredentialStatus.Revoked;
        credential.EncryptedApiKey = null;
        credential.DeletedDate = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return credential.Id;
    }

    private async Task<ModelCredential> LoadActiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        return await _db.ModelCredential.FirstOrDefaultAsync(c => c.Id == id && c.TeamId == teamId && c.DeletedDate == null && c.Status == CredentialStatus.Active, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Model credential {id} not found.");
    }

    public async Task<IReadOnlyList<CredentialedModelSummary>> ListModelsAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        await LoadActiveAsync(credentialId, cancellationToken).ConfigureAwait(false);   // team-scope guard

        var rows = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.ModelCredentialId == credentialId)
            .OrderBy(m => m.ModelId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(ToModelSummary).ToList();
    }

    public async Task<Guid> AddModelAsync(Guid credentialId, string modelId, string? displayName, CancellationToken cancellationToken)
    {
        var normalized = (modelId ?? "").Trim();

        if (normalized.Length == 0) throw new ArgumentException("A model id is required.", nameof(modelId));

        await LoadActiveAsync(credentialId, cancellationToken).ConfigureAwait(false);   // team-scope guard

        if (await _db.ModelCredentialModel.AnyAsync(m => m.ModelCredentialId == credentialId && m.ModelId == normalized, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Model '{normalized}' is already on this credential.");

        var row = new ModelCredentialModel
        {
            Id = Guid.NewGuid(),
            ModelCredentialId = credentialId,
            ModelId = normalized,
            DisplayName = NullIfBlank(displayName),
            Source = ModelSource.Manual,
        };

        await _db.ModelCredentialModel.AddAsync(row, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.Id;
    }

    public async Task<Guid> RemoveModelAsync(Guid credentialId, Guid modelRowId, CancellationToken cancellationToken)
    {
        await LoadActiveAsync(credentialId, cancellationToken).ConfigureAwait(false);   // team-scope guard

        var row = await _db.ModelCredentialModel.FirstOrDefaultAsync(m => m.Id == modelRowId && m.ModelCredentialId == credentialId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Model {modelRowId} not found on credential {credentialId}.");

        _db.ModelCredentialModel.Remove(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.Id;
    }

    /// <summary>
    /// Mark ONE model as the credential's default for an "auto" run (no pinned model) — the resolver orders default-marked
    /// rows first in the pool pick, so an operator can make "auto" use a model they know works. At most one default per
    /// credential: setting one CLEARS any other default on the same credential, in a single transaction. The row must be
    /// ENABLED (a hidden model can't be the default) and on a team-scoped Active credential. Passing the same row twice is
    /// idempotent. There is no separate "clear" verb — re-marking moves the star; deleting/disabling the row drops it.
    /// </summary>
    public async Task<Guid> SetDefaultModelAsync(Guid credentialId, Guid modelRowId, CancellationToken cancellationToken)
    {
        await LoadActiveAsync(credentialId, cancellationToken).ConfigureAwait(false);   // team-scope guard

        // Serialize concurrent set-default writers on the SAME credential (a double-click / two operators): without it,
        // two transactions can each read the pre-commit snapshot and leave TWO defaults (a lost update — the rows carry
        // no concurrency token). A txn-scoped advisory lock, the same primitive RefreshModelsAsync uses; per-credential.
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey(credentialId)})", cancellationToken).ConfigureAwait(false);

        var rows = await _db.ModelCredentialModel.Where(m => m.ModelCredentialId == credentialId).ToListAsync(cancellationToken).ConfigureAwait(false);

        var target = rows.FirstOrDefault(m => m.Id == modelRowId)
            ?? throw new KeyNotFoundException($"Model {modelRowId} not found on credential {credentialId}.");

        if (!target.Enabled) throw new InvalidOperationException($"Model '{target.ModelId}' is disabled and cannot be the default.");

        foreach (var m in rows) m.IsDefault = m.Id == modelRowId;   // exactly one default per credential

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return target.Id;
    }

    public async Task<int> RefreshModelsAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await LoadActiveAsync(credentialId, cancellationToken).ConfigureAwait(false);   // team-scope guard

        // Serialize concurrent refreshes of the SAME credential (a double-click / two operators) so they don't race
        // the (credential, model id) unique index into a 23505 that would poison the ambient transaction. A
        // txn-scoped advisory lock blocks the second refresh until the first commits + releases it — the second then
        // reads the first's rows and UPDATEs (no colliding insert). Per-credential, so unrelated refreshes never block.
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey(credentialId)})", cancellationToken).ConfigureAwait(false);

        var resolved = new ResolvedModelCredential
        {
            Provider = credential.Provider,
            ApiKey = string.IsNullOrEmpty(credential.EncryptedApiKey) ? null : _encryptor.Decrypt(credential.EncryptedApiKey),
            BaseUrl = credential.BaseUrl,
        };

        if (!_reflector.CanReflect(resolved)) return 0;   // manual-only credential — a no-op, never an error

        var reflected = await _reflector.ListModelsAsync(resolved, cancellationToken).ConfigureAwait(false);

        return await UpsertReflectedAsync(credentialId, reflected, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconcile the credential's list with the freshly-reflected set: a NEW reflected id is added; an EXISTING
    /// reflected row is refreshed + re-enabled; a MANUAL row is NEVER touched (an operator's hand-entry is sovereign);
    /// a previously-reflected row that VANISHED from the listing is disabled (provenance kept), never deleted. Returns
    /// the count reflected.
    /// </summary>
    private async Task<int> UpsertReflectedAsync(Guid credentialId, IReadOnlyList<ReflectedModel> reflected, CancellationToken cancellationToken)
    {
        var existing = await _db.ModelCredentialModel.Where(m => m.ModelCredentialId == credentialId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byModelId = existing.ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rm in reflected)
        {
            seen.Add(rm.ModelId);

            if (byModelId.TryGetValue(rm.ModelId, out var row))
            {
                if (row.Source == ModelSource.Manual) continue;   // never clobber an operator's manual row

                row.DisplayName = rm.DisplayName;
                row.Enabled = true;   // a re-appeared model is re-enabled
            }
            else
            {
                _db.ModelCredentialModel.Add(new ModelCredentialModel
                {
                    Id = Guid.NewGuid(),
                    ModelCredentialId = credentialId,
                    ModelId = rm.ModelId,
                    DisplayName = rm.DisplayName,
                    Source = ModelSource.Reflected,
                });
            }
        }

        // A previously-reflected model that vanished from the listing → disable (keep provenance), never delete.
        foreach (var row in existing.Where(m => m.Source == ModelSource.Reflected && !seen.Contains(m.ModelId)))
            row.Enabled = false;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return reflected.Count;
    }

    /// <summary>A stable 64-bit key for the credential's advisory lock (the first 8 bytes of its Guid). A collision between two unrelated credentials would only serialize their refreshes — a harmless perf nudge, never a correctness issue.</summary>
    private static long AdvisoryLockKey(Guid credentialId) => BitConverter.ToInt64(credentialId.ToByteArray());

    private static CredentialedModelSummary ToModelSummary(ModelCredentialModel m) => new()
    {
        Id = m.Id,
        ModelId = m.ModelId,
        DisplayName = m.DisplayName,
        Enabled = m.Enabled,
        IsDefault = m.IsDefault,
        CapabilityTier = m.CapabilityTier,
        ProbedCapabilityTier = m.ProbedCapabilityTier,
        Available = m.Available,
    };

    private ModelCredentialSummary ToSummary(ModelCredential c)
    {
        var keyHint = SafeKeyHint(_encryptor, c.EncryptedApiKey);

        return new ModelCredentialSummary
        {
            Id = c.Id,
            TeamId = c.TeamId,
            Provider = c.Provider,
            DisplayName = c.DisplayName,
            KeyHint = keyHint,
            // A stored key that masked to null can only be one that FAILED to decrypt — a real key never masks to
            // null (a non-blank secret is never stored). Surface it so the UI prompts re-entry instead of showing a
            // dead key as the benign "no key".
            KeyUnreadable = !string.IsNullOrEmpty(c.EncryptedApiKey) && keyHint is null,
            BaseUrl = c.BaseUrl,
            Status = c.Status,
            CreatedDate = c.CreatedDate,
        };
    }

    private string? EncryptOrNull(string? apiKey) => string.IsNullOrWhiteSpace(apiKey) ? null : _encryptor.Encrypt(apiKey);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The masked tail for the list view — best-effort. A secret encrypted under a since-rotated or lost Data
    /// Protection key (after a key-ring migration, say) can no longer be decrypted; the list must still render, so an
    /// unreadable key yields a null hint rather than throwing — the operator needs the list precisely to re-enter the
    /// dead keys. Like <c>CredentialService.TryReadOAuthPayload</c>'s decrypt-or-null handling, but narrowed to the two
    /// "the stored value is unreadable" exceptions so a genuine bug still surfaces. Point-of-use paths
    /// (<c>ModelCredentialResolver</c> / <c>ModelPoolSelector</c>) still throw when the credential is actually used.
    /// </summary>
    internal static string? SafeKeyHint(IPayloadEncryptor encryptor, string? encryptedApiKey)
    {
        if (string.IsNullOrEmpty(encryptedApiKey)) return null;

        try { return MaskKey(encryptor.Decrypt(encryptedApiKey)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }

    /// <summary>Mask a plaintext key to its last 4 chars (e.g. <c>····a1b2</c>); null for a keyless credential. The only place a key tail is ever surfaced — never the full key.</summary>
    internal static string? MaskKey(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        var tail = plaintext.Length <= 4 ? plaintext : plaintext[^4..];
        return "····" + tail;
    }
}
