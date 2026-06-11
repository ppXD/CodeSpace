using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.ModelCredentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

public sealed class ModelCredentialService : IModelCredentialService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly IPayloadEncryptor _encryptor;

    public ModelCredentialService(CodeSpaceDbContext db, ICurrentTeam currentTeam, IPayloadEncryptor encryptor)
    {
        _db = db;
        _currentTeam = currentTeam;
        _encryptor = encryptor;
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
