using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Report-only, and structurally so: every read is <c>AsNoTracking</c>, nothing is added to the change tracker and
/// <c>SaveChanges</c> is never called. It asks a question the plane could not previously ask, and answering it must
/// not be able to change the answer.
/// </summary>
public sealed class LegacyPlacementSurveyor : ILegacyPlacementSurveyor
{
    private readonly CodeSpaceDbContext _db;
    private readonly IStorageProviderModuleCatalog _modules;
    private readonly IStorageRuntimeDriverBroker _broker;
    private readonly ILogger<LegacyPlacementSurveyor> _logger;

    public LegacyPlacementSurveyor(CodeSpaceDbContext db, IStorageProviderModuleCatalog modules, IStorageRuntimeDriverBroker broker, ILogger<LegacyPlacementSurveyor> logger)
    {
        _db = db;
        _modules = modules;
        _broker = broker;
        _logger = logger;
    }

    public async Task<LegacyPlacementSurvey> SurveyAsync(Guid teamId, Guid profileId, int limit, CancellationToken cancellationToken)
    {
        var found = await CountLegacyRowsAsync(teamId, cancellationToken).ConfigureAwait(false);
        var revision = await CurrentRevisionAsync(teamId, profileId, cancellationToken).ConfigureAwait(false);

        if (revision == null) return Report(teamId, Refused(profileId, null, found, LegacyPlacementSurveyRefusalValue.ProfileMissing));

        if (_modules.Get(revision.ProviderTypeKey) is not IStorageProviderLegacyLayout layout)
            return Report(teamId, Refused(profileId, revision.ProviderTypeKey, found, LegacyPlacementSurveyRefusalValue.ProviderHasNoLegacyLayout));

        var resolution = await _broker.OpenAsync(new StorageRuntimeDriverRequest(teamId, profileId, revision.Revision, StorageProfileEligibility.Read), cancellationToken).ConfigureAwait(false);

        if (resolution is not StorageRuntimeDriverResolution.Ready ready)
            return Report(teamId, Refused(profileId, revision.ProviderTypeKey, found, LegacyPlacementSurveyRefusalValue.DestinationUnavailable));

        try
        {
            var target = new SurveyTarget(profileId, revision.ProviderTypeKey, found, Configuration(revision), layout);
            var rows = await LegacyRowsAsync(teamId, Math.Clamp(limit, 1, LegacyPlacementSurveyLimits.MaxRowsPerPass), cancellationToken).ConfigureAwait(false);

            return Report(teamId, await WalkAsync(target, rows, ready.Lease, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await ready.Lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves each row against the profile's own layout, and asks the destination only about the ones it resolved.
    ///
    /// <para>An unresolved row is never HEADed, and that is the whole discipline of the pass: asking about a key the
    /// layout invented would return Missing for a healthy destination and read as lost bytes.</para>
    /// </summary>
    private static async Task<LegacyPlacementSurvey> WalkAsync(SurveyTarget target, IReadOnlyList<LegacyRow> rows, StorageRuntimeDriverLease lease, CancellationToken cancellationToken)
    {
        var resolved = 0;
        var confirmed = 0;
        var confirmedSizeBytes = 0L;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var objectKey = target.Layout.ResolveLegacyObjectKey(target.Configuration, row.Sha256, row.StorageUrl);
            if (objectKey == null) continue;

            resolved++;
            var observed = await ObservedSizeAsync(lease, objectKey, cancellationToken).ConfigureAwait(false);
            if (observed == null) continue;

            confirmed++;
            confirmedSizeBytes += observed.Value;
        }

        return Summarize(target, new SurveyTally(rows.Count, resolved, confirmed, confirmedSizeBytes));
    }

    /// <summary>The size the destination itself reports for a key, or null when it would not answer for one. Never the size the row claims — that is the claim under test.</summary>
    private static async Task<long?> ObservedSizeAsync(StorageRuntimeDriverLease lease, string objectKey, CancellationToken cancellationToken)
    {
        var head = await lease.Driver.HeadAsync(new ArtifactStorageHeadRequest(objectKey), cancellationToken).ConfigureAwait(false);

        return head.IsSuccess ? head.Metadata!.Length : null;
    }

    private static LegacyPlacementSurvey Summarize(SurveyTarget target, SurveyTally tally) => new()
    {
        ProfileId = target.ProfileId,
        ProviderTypeKey = target.ProviderTypeKey,
        Found = target.Found,
        Surveyed = tally.Surveyed,
        Resolved = tally.Resolved,
        Confirmed = tally.Confirmed,
        Unconfirmed = tally.Resolved - tally.Confirmed,
        ConfirmedSizeBytes = tally.ConfirmedSizeBytes,
        AdoptionAdmissible = LegacyAdoptionRules.AdmitsAdoption(LegacyPlacementSurveyRefusalValue.None, tally.Resolved, tally.Confirmed),
        Refusal = LegacyPlacementSurveyRefusalValue.None,
    };

    private static LegacyPlacementSurvey Refused(Guid profileId, string? providerTypeKey, int found, LegacyPlacementSurveyRefusalValue refusal) => new()
    {
        ProfileId = profileId,
        ProviderTypeKey = providerTypeKey,
        Found = found,
        Surveyed = 0,
        Resolved = 0,
        Confirmed = 0,
        Unconfirmed = 0,
        ConfirmedSizeBytes = 0,
        AdoptionAdmissible = LegacyAdoptionRules.AdmitsAdoption(refusal, 0, 0),
        Refusal = refusal,
    };

    /// <summary>The report itself. Phase one adds no page of its own, so the operator surface is the endpoint's body and this line.</summary>
    private LegacyPlacementSurvey Report(Guid teamId, LegacyPlacementSurvey survey)
    {
        _logger.LogInformation(
            "Legacy placement survey for team {TeamId} profile {ProfileId} on provider {ProviderTypeKey}: found {Found}, surveyed {Surveyed}, resolved {Resolved}, confirmed {Confirmed}, unconfirmed {Unconfirmed}, {ConfirmedSizeBytes} bytes confirmed; refusal {Refusal}, adoption admissible {AdoptionAdmissible}",
            teamId, survey.ProfileId, survey.ProviderTypeKey, survey.Found, survey.Surveyed, survey.Resolved, survey.Confirmed,
            survey.Unconfirmed, survey.ConfirmedSizeBytes, survey.Refusal, survey.AdoptionAdmissible);

        return survey;
    }

    private static JsonElement Configuration(SurveyRevision revision)
    {
        using var document = JsonDocument.Parse(revision.NonSecretConfigJson);
        return document.RootElement.Clone();
    }

    /// <summary>The revision the profile currently points at — the configuration a later minting pass would run against, never a superseded one.</summary>
    private async Task<SurveyRevision?> CurrentRevisionAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken) =>
        await _db.StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.TeamId == teamId && revision.StorageProfileId == profileId && revision.Revision == revision.Profile.CurrentRevision)
            .Select(revision => new SurveyRevision(revision.Revision, revision.ProviderTypeKey, revision.NonSecretConfigJson))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<int> CountLegacyRowsAsync(Guid teamId, CancellationToken cancellationToken) =>
        await LegacyRows(teamId).CountAsync(cancellationToken).ConfigureAwait(false);

    private async Task<List<LegacyRow>> LegacyRowsAsync(Guid teamId, int take, CancellationToken cancellationToken) =>
        await LegacyRows(teamId).OrderBy(row => row.Id).Take(take)
            .Select(row => new LegacyRow(row.Sha256, row.StorageUrl!)).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The pre-CAS population: a row that recorded a <c>storage_url</c> holds no <c>cas_artifact_object_id</c>, and therefore no <c>artifact_location</c> anywhere names it.</summary>
    private IQueryable<WorkflowArtifact> LegacyRows(Guid teamId) =>
        _db.WorkflowArtifact.AsNoTracking().Where(row => row.TeamId == teamId && row.StorageUrl != null);

    private sealed record SurveyRevision(int Revision, string ProviderTypeKey, string NonSecretConfigJson);
    private sealed record SurveyTarget(Guid ProfileId, string ProviderTypeKey, int Found, JsonElement Configuration, IStorageProviderLegacyLayout Layout);
    private sealed record SurveyTally(int Surveyed, int Resolved, int Confirmed, long ConfirmedSizeBytes);
    private sealed record LegacyRow(string Sha256, string StorageUrl);
}
