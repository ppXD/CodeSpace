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
}
