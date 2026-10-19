using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// The one question a move asks before it commits: can this credential read that repository, and is it the one that was meant? Asked of the
/// provider as the target credential, the way a bind looks a repository up — a target that cannot would turn a repository's loud failure into a
/// quiet one (runs 404 instead of naming a disconnected credential).
/// </summary>
public interface IRepositoryReadProbe
{
    /// <summary>
    /// Look the repository up by its external id as <paramref name="target"/> (whose <c>ProviderInstance</c> may be loaded or not). Only a provider that
    /// ANSWERED says anything about the target — a 401, 403 or 404, an insufficient scope, or a grant refused when its token was renewed — and that is a
    /// <see cref="RepositoryRead"/> that is not readable. A provider that did not answer (a 5xx, a 429, a dropped connection, a local rate limit) says nothing
    /// about it, and throws <see cref="RelinkVerificationUnavailableException"/> so nobody is blamed. <paramref name="sourceName"/> is whatever the repository
    /// is being taken off, for that message; null for one repository on its own.
    /// </summary>
    Task<RepositoryRead> ReadAsync(Credential target, string externalId, string? sourceName, CancellationToken cancellationToken);
}

/// <summary>What the provider said when the target looked a repository up: nothing it could read, or the repository at the path the provider names.</summary>
public sealed record RepositoryRead(bool Readable, string? FullPath)
{
    public static readonly RepositoryRead Refused = new(false, null);

    /// <summary>Readable, and the path the provider names is the repository's own — the one definition of "the same repository" for a move to another connection: a case-insensitive path, as the provider's lookups are.</summary>
    public bool IsAt(string fullPath) => Readable && string.Equals(FullPath, fullPath, StringComparison.OrdinalIgnoreCase);
}
