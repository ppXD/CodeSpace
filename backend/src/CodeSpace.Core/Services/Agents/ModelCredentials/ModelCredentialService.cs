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

    public async Task<int> RefreshModelsAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await LoadActiveAsync(credentialId, cancellationToken).ConfigureAwait(false);   // team-scope guard

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
                ApplyCapabilities(row, rm.Capabilities);
                row.Enabled = true;   // a re-appeared model is re-enabled
            }
            else
            {
                var added = new ModelCredentialModel
                {
                    Id = Guid.NewGuid(),
                    ModelCredentialId = credentialId,
                    ModelId = rm.ModelId,
                    DisplayName = rm.DisplayName,
                    Source = ModelSource.Reflected,
                };
                ApplyCapabilities(added, rm.Capabilities);
                _db.ModelCredentialModel.Add(added);
            }
        }

        // A previously-reflected model that vanished from the listing → disable (keep provenance), never delete.
        foreach (var row in existing.Where(m => m.Source == ModelSource.Reflected && !seen.Contains(m.ModelId)))
            row.Enabled = false;

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent refresh won the (credential, model id) race — its rows are already written, so this
            // refresh is a benign no-op (the convergent listing) rather than a 500. The refresh endpoint is the one
            // re-triggerable action, so a double-click / two-operator overlap is expected, not exotic.
        }

        return reflected.Count;
    }

    /// <summary>A Postgres unique-constraint violation (23505) — a concurrent writer beat us to the (credential, model id) index. Mirrors <c>ConversationService.IsUniqueViolation</c>.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static void ApplyCapabilities(ModelCredentialModel row, ModelCapabilityFlags caps)
    {
        row.SupportsStructuredOutput = caps.SupportsStructuredOutput;
        row.SupportsToolUse = caps.SupportsToolUse;
        row.RecommendedForSupervisor = caps.RecommendedForSupervisor;
    }

    private static CredentialedModelSummary ToModelSummary(ModelCredentialModel m) => new()
    {
        Id = m.Id,
        ModelId = m.ModelId,
        DisplayName = m.DisplayName,
        Enabled = m.Enabled,
    };

    private ModelCredentialSummary ToSummary(ModelCredential c) => new()
    {
        Id = c.Id,
        TeamId = c.TeamId,
        Provider = c.Provider,
        DisplayName = c.DisplayName,
        KeyHint = MaskKey(string.IsNullOrEmpty(c.EncryptedApiKey) ? null : _encryptor.Decrypt(c.EncryptedApiKey)),
        BaseUrl = c.BaseUrl,
        Status = c.Status,
        CreatedDate = c.CreatedDate,
    };

    private string? EncryptOrNull(string? apiKey) => string.IsNullOrWhiteSpace(apiKey) ? null : _encryptor.Encrypt(apiKey);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Mask a plaintext key to its last 4 chars (e.g. <c>····a1b2</c>); null for a keyless credential. The only place a key tail is ever surfaced — never the full key.</summary>
    internal static string? MaskKey(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        var tail = plaintext.Length <= 4 ? plaintext : plaintext[^4..];
        return "····" + tail;
    }
}
