using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Defaults;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class AdoptStorageDefaultCommandHandler : IRequestHandler<AdoptStorageDefaultCommand, StorageAdoptionResult>
{
    private readonly IStorageDefaultMaterializer _materializer;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AdoptStorageDefaultCommandHandler(IStorageDefaultMaterializer materializer, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _materializer = materializer;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageAdoptionResult> Handle(AdoptStorageDefaultCommand request, CancellationToken cancellationToken)
    {
        // Automatic: false. This request came from a person who chose it, which is exactly the distinction an Explicit
        // template exists to enforce.
        var outcome = await _materializer.MaterializeAsync(
            new StorageMaterializationRequest(_currentTeam.Id!.Value, request.DataClassTypeKey, _currentUser.Id!.Value, Automatic: false), cancellationToken)
            .ConfigureAwait(false);

        return Describe(outcome);
    }

    /// <summary>
    /// Exhaustive by CASE over a closed set, so an outcome added later fails to compile here rather than reaching a
    /// client as a default that reads like success.
    ///
    /// <para><c>AdoptionRequiresChoice</c> and <c>TeamNotFound</c> are unreachable from this handler and are mapped
    /// rather than ignored: the first cannot occur because this caller IS the choice, and the second because the team
    /// was resolved from an authenticated request before the command reached here. Mapping them keeps the switch total
    /// without inventing wire values for states a client can never observe — both collapse onto the nearest true
    /// statement.</para>
    /// </summary>
    private static StorageAdoptionResult Describe(StorageMaterialization outcome) => outcome switch
    {
        StorageMaterialization.Materialized materialized => new StorageAdoptionResult
        {
            Outcome = StorageAdoptionOutcomeValue.Adopted,
            StorageProfileId = materialized.StorageProfileId,
            StorageRouteId = materialized.StorageRouteId,
            SourceRevision = materialized.SourceRevision,
        },
        StorageMaterialization.AlreadyMaterialized already => new StorageAdoptionResult
        {
            Outcome = StorageAdoptionOutcomeValue.AlreadyAdopted,
            StorageProfileId = already.StorageProfileId,
            SourceRevision = already.SourceRevision,
        },
        StorageMaterialization.TeamOwnsRoute owns => new StorageAdoptionResult
        {
            Outcome = StorageAdoptionOutcomeValue.TeamOwnsRoute,
            StorageRouteId = owns.StorageRouteId,
        },
        StorageMaterialization.DestinationUnusable unusable => new StorageAdoptionResult
        {
            Outcome = StorageAdoptionOutcomeValue.DestinationUnusable,
            Detail = unusable.Reason,
        },
        StorageMaterialization.TemplateDisabled => new StorageAdoptionResult { Outcome = StorageAdoptionOutcomeValue.TemplateDisabled },
        StorageMaterialization.RaceLost => new StorageAdoptionResult { Outcome = StorageAdoptionOutcomeValue.RaceLost },
        StorageMaterialization.NoTemplate or StorageMaterialization.AdoptionRequiresChoice or StorageMaterialization.TeamNotFound
            => new StorageAdoptionResult { Outcome = StorageAdoptionOutcomeValue.NoTemplate },
        _ => throw new InvalidOperationException($"Storage materialization outcome '{outcome.GetType().Name}' has no adoption result — a new outcome must be mapped rather than reported as one of the existing ones."),
    };
}
