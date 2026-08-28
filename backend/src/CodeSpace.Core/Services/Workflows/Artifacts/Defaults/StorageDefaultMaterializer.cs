using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

/// <summary>
/// The pipeline. Each step either enriches <c>_ctx</c> or throws the one outcome it is the guard for, so the ordering
/// constraints are visible as a list of names rather than a conjunction the reader has to re-derive.
///
/// <para>The composition detail that makes this safe: the storage services save through a plain
/// <c>SaveChangesAsync</c> and open no transaction of their own, so every write below enlists in the transaction
/// opened here and commits only at the end. Reusing them instead of building entities directly also means their
/// admission rules — namespace fingerprint, credential/provider agreement, profile-Active-before-route, the exact
/// pinned revision — are enforced once, where they live, rather than replicated here where they would drift.</para>
/// </summary>
public sealed partial class StorageDefaultMaterializer : IStorageDefaultMaterializer, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IStorageProviderModuleCatalog _catalog;
    private readonly IStorageCredentialService _credentials;
    private readonly IStorageProfileService _profiles;
    private readonly IStorageRouteService _routes;
    private readonly IStorageProfileProbeService _probe;
    private readonly IPayloadEncryptor _encryptor;
    private readonly TimeProvider _clock;
    private MaterializationContext _ctx = default!;

    public StorageDefaultMaterializer(CodeSpaceDbContext db, IStorageProviderModuleCatalog catalog, IStorageCredentialService credentials,
        IStorageProfileService profiles, IStorageRouteService routes, IStorageProfileProbeService probe, IPayloadEncryptor encryptor, TimeProvider clock)
    {
        _db = db;
        _catalog = catalog;
        _credentials = credentials;
        _profiles = profiles;
        _routes = routes;
        _probe = probe;
        _encryptor = encryptor;
        _clock = clock;
    }

    public async Task<StorageMaterialization> MaterializeAsync(StorageMaterializationRequest request, CancellationToken cancellationToken)
    {
        _ctx = BuildContext(request);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await StorageBootstrapLock.TakeAsync(_db.Database, _ctx.TeamId, cancellationToken).ConfigureAwait(false);

            await LoadTemplateAsync(cancellationToken).ConfigureAwait(false);
            EnsureAdoptionChosen();
            await EnsureNothingClaimsThisClassAsync(cancellationToken).ConfigureAwait(false);

            await CreateCredentialAsync(cancellationToken).ConfigureAwait(false);
            await CreateActiveProfileAsync(cancellationToken).ConfigureAwait(false);
            await ProveDestinationWritableAsync(cancellationToken).ConfigureAwait(false);
            await CreateActiveRouteAsync(cancellationToken).ConfigureAwait(false);
            await RecordProvenanceAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new StorageMaterialization.Materialized(_ctx.ProfileId, _ctx.RouteId, _ctx.Template.Revision);
        }
        catch (MaterializationHaltException halt)
        {
            // Rolled back as ONE unit on purpose. Every row this pipeline writes is undeletable once committed, so a
            // partial materialization would be permanent: an orphaned Active profile keeps its credential unrevokable,
            // and a route that reached Active can never return to Draft.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return halt.Outcome;
        }
    }

    private static MaterializationContext BuildContext(StorageMaterializationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty) throw new ArgumentException("A team id is required.", nameof(request));
        if (request.ActorId == Guid.Empty) throw new ArgumentException("An actor id is required.", nameof(request));

        return new MaterializationContext
        {
            TeamId = request.TeamId,
            DataClassTypeKey = StorageDefaultRules.NormalizeDataClassTypeKey(request.DataClassTypeKey),
            ActorId = request.ActorId,
            Automatic = request.Automatic,
        };
    }

    /// <summary>The one outcome that is not a success, carried out of the pipeline by the step that decided it.</summary>
    private sealed class MaterializationHaltException : Exception
    {
        public MaterializationHaltException(StorageMaterialization outcome) => Outcome = outcome;

        public StorageMaterialization Outcome { get; }
    }

    private static MaterializationHaltException Halt(StorageMaterialization outcome) => new(outcome);
}
