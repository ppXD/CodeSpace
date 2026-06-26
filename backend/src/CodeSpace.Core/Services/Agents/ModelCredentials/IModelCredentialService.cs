using CodeSpace.Messages.Dtos.ModelCredentials;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// Team-scoped management of model credentials — the write/read surface the settings UI drives (distinct from
/// the just-in-time <see cref="IModelCredentialResolver"/>, which runs in the background keyed on the run's
/// team). Every operation is scoped to the caller's current team; the secret is encrypted on write and NEVER
/// returned (reads surface only a masked hint).
/// </summary>
public interface IModelCredentialService
{
    Task<IReadOnlyList<ModelCredentialSummary>> ListAsync(string? provider, CancellationToken cancellationToken);

    /// <summary>Create a credential for the current team. <paramref name="apiKey"/> null/blank = a keyless provider. Returns the new id.</summary>
    Task<Guid> AddAsync(string provider, string displayName, string? apiKey, string? baseUrl, CancellationToken cancellationToken);

    /// <summary>Update display name + base URL, and rotate the key only when <paramref name="apiKey"/> is non-blank (write-only). Throws when the id isn't an active credential of the current team.</summary>
    Task<Guid> UpdateAsync(Guid id, string displayName, string? apiKey, string? baseUrl, CancellationToken cancellationToken);

    /// <summary>Soft-delete + clear the key. Idempotent. Throws when the id isn't a credential of the current team.</summary>
    Task<Guid> RevokeAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>List a credential's maintained models (the pick-from-list surface). Throws when the id isn't an active credential of the current team.</summary>
    Task<IReadOnlyList<CredentialedModelSummary>> ListModelsAsync(Guid credentialId, CancellationToken cancellationToken);

    /// <summary>Manually add a model to a credential's list (<c>Source = Manual</c>). Throws when the id isn't an active credential of the current team, the model id is blank, or it is already on the credential. Returns the new row id.</summary>
    Task<Guid> AddModelAsync(Guid credentialId, string modelId, string? displayName, CancellationToken cancellationToken);

    /// <summary>Remove a model row from a credential's list. Throws when the credential isn't the current team's or the row isn't under it. Returns the removed row id.</summary>
    Task<Guid> RemoveModelAsync(Guid credentialId, Guid modelRowId, CancellationToken cancellationToken);

    /// <summary>Mark ONE enabled model as the credential's default for an "auto" run (clears any other default on the same credential). Throws when the credential isn't the current team's, the row isn't under it, or the row is disabled. Returns the marked row id.</summary>
    Task<Guid> SetDefaultModelAsync(Guid credentialId, Guid modelRowId, CancellationToken cancellationToken);

    /// <summary>Reflect the credential's provider endpoint (when reflectable) and UPSERT the discovered models onto its list — reflected rows refreshed + re-enabled, manual rows untouched, vanished reflected rows disabled. A non-reflectable (manual-only) credential is a no-op returning 0. Throws when the id isn't an active credential of the current team. Returns the count reflected.</summary>
    Task<int> RefreshModelsAsync(Guid credentialId, CancellationToken cancellationToken);
}
