using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>Refuses a paid qualification stage whose live runtime is not the runtime the campaign froze.</summary>
public interface IQualificationRuntimeGate
{
    /// <summary>Compare this host's live runtime against the campaign's frozen manifest, or throw <c>RuntimeManifestDriftException</c> naming the drifted field and <paramref name="stage"/>. A no-op for a campaign that froze no manifest.</summary>
    Task EnsureUnchangedAsync(Guid observationGroupId, QualificationRuntimeStage stage, CancellationToken cancellationToken);
}

/// <summary>
/// THE one choke point every paid paired-qualification stage passes through: cell admission, cell execution,
/// resume-that-executes, and the seal of executed work. One implementation rather than four inline comparisons so a
/// fifth paying stage cannot be added without an obvious place to call — and so the refusal is identical wherever it
/// fires.
///
/// <para><b>Fail-closed, write-nothing.</b> The comparison runs BEFORE the stage's own write, so a drifted runtime
/// leaves no admission row, no cell result, no observation and no seal. The refusal carries
/// <c>FailureKind.Conflict</c>: state moved underneath the campaign, and an operator decides — restore the frozen
/// runtime and resume, or abandon the campaign.</para>
///
/// <para><b>Frozen bytes, never a recomputation.</b> The baseline is the manifest JSON persisted on the protocol
/// row, read back through <c>QualificationRuntimeManifest.Parse</c>. It is deliberately NOT rebuilt from the row's
/// other fields: <c>PairedTaskLaunchQualificationRunner.ProtocolDigest</c> is not reproducible from a read-back row
/// (<c>MaxCostUsdPerLaunch</c> returns from <c>numeric(18,6)</c> carrying scale, which the digest serializes
/// verbatim), so a "verify the identity too" step here would fail on correct rows.</para>
///
/// <para><b>Two documented no-ops.</b> A campaign with no protocol row (an ordinary benchmark corpus run that
/// pre-registers nothing) and a LEGACY protocol committed before the runtime bundle existed (<c>null</c> manifest)
/// both pass every stage untouched. The gate enforces a manifest that was actually frozen; it never invents a
/// baseline, and it is not the place that decides whether freezing is mandatory.</para>
/// </summary>
public sealed class QualificationRuntimeGate : IQualificationRuntimeGate, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IQualificationRuntimeManifestCollector _collector;
    private readonly ILogger<QualificationRuntimeGate> _logger;

    public QualificationRuntimeGate(CodeSpaceDbContext db, IQualificationRuntimeManifestCollector collector, ILogger<QualificationRuntimeGate> logger)
    {
        _db = db;
        _collector = collector;
        _logger = logger;
    }

    public async Task EnsureUnchangedAsync(Guid observationGroupId, QualificationRuntimeStage stage, CancellationToken cancellationToken)
    {
        if (observationGroupId == Guid.Empty) throw new ArgumentException("A campaign observation group is required to verify its frozen runtime.", nameof(observationGroupId));

        var campaign = await LoadCampaignAsync(observationGroupId, cancellationToken).ConfigureAwait(false);

        // No protocol row (an ordinary corpus run) or a legacy one that froze no manifest: nothing was frozen, so
        // nothing is enforced — and no live observation is paid for either.
        if (campaign is null || FrozenManifestOf(campaign.ManifestJson) is not { } frozen) return;

        var observed = await _collector.ObserveAsync(Collect(observationGroupId, campaign), cancellationToken).ConfigureAwait(false);

        QualificationRuntimeManifest.EnsureNoDrift(frozen, observed, stage);

        _logger.LogDebug("Paired qualification campaign {ObservationGroupId} runtime unchanged at {Stage} against frozen manifest {ManifestDigest}", observationGroupId, stage, campaign.ManifestDigest);
    }

    /// <summary>
    /// The gate's whole precondition, as a pure decision over the one column that carries the baseline: a campaign
    /// that froze a manifest has one to compare, and a legacy protocol (or no protocol at all) has none. Internal so
    /// the no-op is pinned directly (InternalsVisibleTo) rather than only through a durable flow — it is the branch
    /// that decides whether an entire pre-existing campaign keeps running.
    /// </summary>
    internal static QualificationRuntimeManifest? FrozenManifestOf(string? runtimeManifestJson) =>
        runtimeManifestJson is { } json ? QualificationRuntimeManifest.Parse(json) : null;

    /// <summary>The live observation is taken for the arms the PROTOCOL row names, never the arms a caller passes — a campaign cannot be compared against a runtime assembled from someone else's models.</summary>
    private static QualificationRuntimeCollectRequest Collect(Guid observationGroupId, FrozenCampaign campaign) => new()
    {
        ObservationGroupId = observationGroupId, TeamId = campaign.TeamId,
        ControlModelRowId = campaign.ControlModelRowId, CandidateModelRowId = campaign.CandidateModelRowId,
    };

    private async Task<FrozenCampaign?> LoadCampaignAsync(Guid observationGroupId, CancellationToken cancellationToken) =>
        await _db.PairedQualificationProtocol.AsNoTracking().Where(row => row.ObservationGroupId == observationGroupId)
            .Select(row => new FrozenCampaign(row.RuntimeManifestJson, row.RuntimeManifestDigest, row.TeamId, row.ControlModelRowId, row.CandidateModelRowId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private sealed record FrozenCampaign(string? ManifestJson, string? ManifestDigest, Guid TeamId, Guid ControlModelRowId, Guid CandidateModelRowId);
}
