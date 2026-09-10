using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>Which campaign's runtime to observe. The arms are NAMED rather than positional on purpose: swapping control for candidate would produce a different manifest and read as a substituted runtime.</summary>
public sealed record QualificationRuntimeCollectRequest
{
    /// <summary>The campaign's own identity — and, as a per-campaign random value, the HMAC salt every credential fingerprint in the manifest is taken under.</summary>
    public required Guid ObservationGroupId { get; init; }

    public required Guid TeamId { get; init; }

    public required Guid ControlModelRowId { get; init; }

    public required Guid CandidateModelRowId { get; init; }
}

/// <summary>Observes one campaign's complete live runtime bundle — the manifest a campaign freezes at pre-registration and is compared against at every stage that does or seals paid work.</summary>
public interface IQualificationRuntimeManifestCollector
{
    Task<QualificationRuntimeManifest> ObserveAsync(QualificationRuntimeCollectRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// THE seam through which a campaign's runtime is observed — at pre-registration (where the manifest is frozen onto
/// the protocol row) and at every later stage <c>IQualificationRuntimeGate</c> guards. Both go through this ONE
/// method for the reason <see cref="QualificationRuntimeManifestObserver"/> documents: harness and endpoint ordering
/// are part of the frozen seam, so a manifest assembled any other way could reorder silently and misread as a
/// substituted runtime.
///
/// <para>It adds to the observer exactly the two things the observer cannot derive locally — the per-arm credential
/// endpoint identities (a team-scoped database read plus the just-in-time decryption the salted fingerprint
/// consumes) and the reviewer resolution. The secret is decrypted, consumed into an HMAC, and never retained: the
/// returned record cannot hold key material by construction.</para>
/// </summary>
public sealed class QualificationRuntimeManifestCollector : IQualificationRuntimeManifestCollector, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IQualificationRuntimeManifestObserver _observer;
    private readonly IPayloadEncryptor _encryptor;

    public QualificationRuntimeManifestCollector(CodeSpaceDbContext db, IQualificationRuntimeManifestObserver observer, IPayloadEncryptor encryptor)
    {
        _db = db;
        _observer = observer;
        _encryptor = encryptor;
    }

    public async Task<QualificationRuntimeManifest> ObserveAsync(QualificationRuntimeCollectRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        var endpoints = await ObserveEndpointsAsync(request, cancellationToken).ConfigureAwait(false);

        // A paired qualification pins no judge or critic model row of its own — the rubric judge auto-resolves — so
        // both roles freeze as UNPINNED rather than as a guessed row. The day a campaign pins one, the manifest
        // moves and every stage below compares it.
        return _observer.Observe(endpoints, QualificationRuntimeManifestObserver.ResolveReviewer(null, null));
    }

    private async Task<IReadOnlyList<CredentialEndpointIdentity>> ObserveEndpointsAsync(QualificationRuntimeCollectRequest request, CancellationToken cancellationToken)
    {
        var salt = request.ObservationGroupId.ToString("N");
        var arms = await LoadArmCredentialsAsync(request, cancellationToken).ConfigureAwait(false);

        return
        [
            Endpoint(QualificationCredentialRole.Control, request.ControlModelRowId, arms, salt),
            Endpoint(QualificationCredentialRole.Candidate, request.CandidateModelRowId, arms, salt),
        ];
    }

    /// <summary>
    /// The two arms' credential rows, scoped to the campaign's team so no other team's endpoint can ever enter this
    /// manifest. DELIBERATELY not filtered on <c>Enabled</c> / <c>Status</c> / <c>DeletedDate</c>: a revoked or
    /// disabled credential is a different refusal, owned by the selection gates that already name it
    /// (<c>selection-unavailable</c>), and folding it in here would make a drift refusal lie about its cause.
    /// </summary>
    private async Task<Dictionary<Guid, ArmCredential>> LoadArmCredentialsAsync(QualificationRuntimeCollectRequest request, CancellationToken cancellationToken)
    {
        var rowIds = new[] { request.ControlModelRowId, request.CandidateModelRowId };
        var rows = await _db.ModelCredentialModel.AsNoTracking()
            .Where(row => rowIds.Contains(row.Id) && row.Credential.TeamId == request.TeamId)
            .Select(row => new ArmCredential(row.Id, row.ModelCredentialId, row.Credential.Provider, row.Credential.BaseUrl, row.Credential.EncryptedApiKey))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(row => row.ModelRowId);
    }

    private CredentialEndpointIdentity Endpoint(QualificationCredentialRole role, Guid modelRowId, IReadOnlyDictionary<Guid, ArmCredential> arms, string salt)
    {
        if (!arms.TryGetValue(modelRowId, out var arm))
            throw new InvalidOperationException($"Qualification {role} model row {modelRowId} does not belong to its campaign team, so the campaign's runtime cannot be observed.");

        return CredentialEndpointIdentity.Observe(role, modelRowId, arm.CredentialRowId, arm.Provider, arm.BaseUrl, Secret(arm.EncryptedApiKey), salt);
    }

    /// <summary>Decrypt just in time. A keyless provider (a local Ollama reached over its base URL) has no ciphertext, and <c>Fingerprint</c> domain-separates that absence from an empty key.</summary>
    private string? Secret(string? encryptedApiKey) => encryptedApiKey is null ? null : _encryptor.Decrypt(encryptedApiKey);

    private static void Validate(QualificationRuntimeCollectRequest request)
    {
        if (request.ObservationGroupId == Guid.Empty) throw new ArgumentException("A campaign observation group is required — it is the fingerprint salt, and a constant would let two campaigns' fingerprints of one key be correlated.", nameof(request));
        if (request.TeamId == Guid.Empty) throw new ArgumentException("A campaign team is required to scope its credential endpoints.", nameof(request));
        if (request.ControlModelRowId == Guid.Empty || request.CandidateModelRowId == Guid.Empty) throw new ArgumentException("Both campaign arms require an exact credential-model row.", nameof(request));
    }

    private sealed record ArmCredential(Guid ModelRowId, Guid CredentialRowId, string Provider, string? BaseUrl, string? EncryptedApiKey);
}
