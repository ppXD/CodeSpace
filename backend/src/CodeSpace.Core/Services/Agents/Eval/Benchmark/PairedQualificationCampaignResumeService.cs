using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

public interface IPairedQualificationCampaignResumeService
{
    Task<PairedQualificationOutcome> ResumeAsync(Guid observationGroupId, CancellationToken cancellationToken);
}

/// <summary>Adopts one interrupted paired campaign and executes only cells absent from its immutable durable keyset.</summary>
public sealed class PairedQualificationCampaignResumeService : IPairedQualificationCampaignResumeService, IScopedDependency
{
    private readonly IHiddenSuiteSource _suite;
    private readonly IPairedCorpusBenchmarkRunner _corpus;
    private readonly CodeSpaceDbContext _db;
    private readonly PairedQualificationRecoveryService _recovery;
    private readonly IPairedQualificationCampaignLock _campaignLock;
    private readonly IQualificationRuntimeGate _runtimeGate;

    public PairedQualificationCampaignResumeService(IHiddenSuiteSource suite, IPairedCorpusBenchmarkRunner corpus, CodeSpaceDbContext db, PairedQualificationRecoveryService recovery, IPairedQualificationCampaignLock campaignLock, IQualificationRuntimeGate runtimeGate)
    {
        _suite = suite;
        _corpus = corpus;
        _db = db;
        _recovery = recovery;
        _campaignLock = campaignLock;
        _runtimeGate = runtimeGate;
    }

    public async Task<PairedQualificationOutcome> ResumeAsync(Guid observationGroupId, CancellationToken cancellationToken)
    {
        if (observationGroupId == Guid.Empty) throw Invalid("observation-group-unbound");
        await using var claim = await _campaignLock.AcquireAsync(observationGroupId, cancellationToken).ConfigureAwait(false);
        var protocol = await _db.PairedQualificationProtocol.AsNoTracking().SingleOrDefaultAsync(row => row.ObservationGroupId == observationGroupId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("protocol-not-found");
        if (await _db.PairedQualificationResult.AsNoTracking().AnyAsync(row => row.ObservationGroupId == observationGroupId, cancellationToken).ConfigureAwait(false)) throw Invalid("result-already-sealed");

        var suite = _suite.Load() ?? throw Invalid("sealed-suite-unavailable");
        var manifest = EvalSuite.ManifestFor(suite.Tasks, suite.SuiteContentHash);
        if (suite.SuiteContentHash != protocol.SuiteDigest || manifest.Version != protocol.SuiteVersion || CorpusBenchmarkRunner.ExecutionPathFor(manifest) != BenchmarkExecutionPath.TaskLaunch) throw Invalid("sealed-suite-mismatch");
        var control = Selection(protocol.ControlSelectionJson, protocol.ControlModelRowId, protocol.MaxCostUsdPerLaunch);
        var candidate = Selection(protocol.CandidateSelectionJson, protocol.CandidateModelRowId, protocol.MaxCostUsdPerLaunch);
        await ValidateCurrentModelsAsync(protocol, control, candidate, cancellationToken).ConfigureAwait(false);

        var observations = await ObservationsAsync(observationGroupId, cancellationToken).ConfigureAwait(false);
        ValidateSubset(protocol, manifest, observations, control, candidate);
        var missing = Missing(protocol, manifest, observations);

        await EnsureRuntimeUnchangedForExecutionAsync(observationGroupId, missing, cancellationToken).ConfigureAwait(false);

        foreach (var target in missing)
        {
            if (await ExistsAsync(observationGroupId, target, cancellationToken).ConfigureAwait(false)) continue;
            await _corpus.RunPairedAsync(new PairedCorpusBenchmarkRequest
            {
                Tasks = suite.Tasks, TeamId = protocol.TeamId, Control = control, Candidate = candidate,
                ObservationGroupId = observationGroupId, ObservationSession = target.Session, OrderingSeed = protocol.OrderingSeed,
                CodeRevision = protocol.CodeRevision, FixtureStager = suite.FixtureStager, SuiteContentHash = suite.SuiteContentHash,
                SelectedCells = new[] { new PairedCorpusBenchmarkCell { TaskId = target.TaskId, Mode = target.Mode, Arm = target.Arm } },
            }, cancellationToken).ConfigureAwait(false);
            if (!await ExistsAsync(observationGroupId, target, cancellationToken).ConfigureAwait(false)) throw Invalid("observation-not-appended");
        }

        return await _recovery.RecoverClaimedAsync(observationGroupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verify the campaign's frozen runtime before this process pays for a single absent cell. Gated on there being
    /// absent cells AT ALL: an adoption that finds the census already complete executes nothing and falls through to
    /// replay, and refusing that would strand a fully-paid campaign on a host whose runtime moved after its last cell.
    /// </summary>
    private async Task EnsureRuntimeUnchangedForExecutionAsync(Guid observationGroupId, IReadOnlyList<MissingCell> missing, CancellationToken cancellationToken)
    {
        if (missing.Count == 0) return;

        await _runtimeGate.EnsureUnchangedAsync(observationGroupId, QualificationRuntimeStage.Resume, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateCurrentModelsAsync(PairedQualificationProtocol protocol, BenchmarkAgentSelection control, BenchmarkAgentSelection candidate, CancellationToken cancellationToken)
    {
        var rowIds = new[] { protocol.ControlModelRowId, protocol.CandidateModelRowId };
        var rows = await _db.ModelCredentialModel.AsNoTracking()
            .Where(row => rowIds.Contains(row.Id) && row.Enabled && row.Credential.TeamId == protocol.TeamId && row.Credential.DeletedDate == null && row.Credential.Status == CredentialStatus.Active)
            .Select(row => new { row.Id, row.ModelId, row.ModelCredentialId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count != 2) throw Invalid("selection-unavailable");
        foreach (var selection in new[] { control, candidate })
        {
            var row = rows.Single(value => value.Id == selection.ModelCredentialModelId);
            if (row.ModelId != selection.Model || row.ModelCredentialId != selection.ModelCredentialId) throw Invalid("selection-drift");
        }
    }

    private async Task<List<BenchmarkResultRecord>> ObservationsAsync(Guid groupId, CancellationToken cancellationToken) =>
        await _db.BenchmarkResultRecord.AsNoTracking().Where(row => row.ObservationGroupId == groupId).ToListAsync(cancellationToken).ConfigureAwait(false);

    private Task<bool> ExistsAsync(Guid groupId, MissingCell target, CancellationToken cancellationToken) =>
        _db.BenchmarkResultRecord.AsNoTracking().AnyAsync(row => row.ObservationGroupId == groupId && row.ObservationSession == target.Session && row.ObservationArm == target.Arm && row.TaskId == target.TaskId && row.Mode == target.Mode.ToString(), cancellationToken);

    private static void ValidateSubset(PairedQualificationProtocol protocol, EvalSuiteManifest manifest, IReadOnlyList<BenchmarkResultRecord> observations, BenchmarkAgentSelection control, BenchmarkAgentSelection candidate)
    {
        var expected = (from session in Enumerable.Range(0, protocol.SessionsPerCell)
                        from arm in new[] { "control", "candidate" }
                        from cell in manifest.Cells
                        select (session, arm, cell.TaskId, Mode: cell.Mode.ToString())).ToHashSet();
        var actual = observations.Select(row => (row.ObservationSession ?? -1, row.ObservationArm ?? string.Empty, row.TaskId, row.Mode)).ToList();
        if (actual.Distinct().Count() != actual.Count || actual.Any(key => !expected.Contains(key))) throw Invalid("observation-keyset-invalid");
        if (observations.Any(row => row.TeamId != protocol.TeamId || row.SuiteVersion != protocol.SuiteVersion || row.GitSha != protocol.CodeRevision || row.MaxCostUsd != protocol.MaxCostUsdPerLaunch)) throw Invalid("observation-protocol-mismatch");
        if (observations.Any(row => !Matches(row, row.ObservationArm == "control" ? control : candidate))) throw Invalid("observation-selection-mismatch");
        if (protocol.RequiresResultDigest && observations.Any(row => string.IsNullOrWhiteSpace(row.SourceResultDigest))) throw Invalid("observation-result-digest-missing");
    }

    private static bool Matches(BenchmarkResultRecord row, BenchmarkAgentSelection selection) => row.ModelCredentialModelId == selection.ModelCredentialModelId && row.Harness == selection.Harness && row.Model == selection.Model;

    private static IReadOnlyList<MissingCell> Missing(PairedQualificationProtocol protocol, EvalSuiteManifest manifest, IReadOnlyList<BenchmarkResultRecord> observations)
    {
        var present = observations.Select(row => (Session: row.ObservationSession!.Value, Arm: row.ObservationArm!, row.TaskId, row.Mode)).ToHashSet();
        var missing = new List<MissingCell>();
        foreach (var session in Enumerable.Range(0, protocol.SessionsPerCell))
        {
            var selectedIndex = 0;
            foreach (var cell in manifest.Cells.OrderBy(cell => CellOrder(protocol.OrderingSeed, cell.TaskId, cell.Mode, session), StringComparer.Ordinal))
            {
                var arms = new[] { "control", "candidate" }.Where(arm => !present.Contains((session, arm, cell.TaskId, cell.Mode.ToString()))).ToHashSet(StringComparer.Ordinal);
                if (arms.Count == 0) continue;
                var candidateFirst = (selectedIndex++ + session) % 2 == 0;
                foreach (var arm in OrderedArms(arms, candidateFirst)) missing.Add(new MissingCell(session, cell.TaskId, cell.Mode, arm));
            }
        }
        return missing;
    }

    private static BenchmarkAgentSelection Selection(string? json, Guid rowId, decimal cap)
    {
        if (string.IsNullOrWhiteSpace(json)) throw Invalid("selection-snapshot-unavailable");
        BenchmarkAgentSelection selection;
        try { selection = JsonSerializer.Deserialize<BenchmarkAgentSelection>(json, Agents.AgentJson.Options) ?? throw Invalid("selection-snapshot-invalid"); }
        catch (JsonException) { throw Invalid("selection-snapshot-invalid"); }
        if (selection.ModelCredentialModelId != rowId || selection.ModelCredentialId is null || string.IsNullOrWhiteSpace(selection.Model) || selection.MaxCostUsd != cap) throw Invalid("selection-snapshot-mismatch");
        return selection;
    }

    private static string CellOrder(string seed, string taskId, BenchmarkMode mode, int session) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}\u001f{session}\u001f{taskId}\u001f{mode}")));
    private static IEnumerable<string> OrderedArms(IReadOnlySet<string> arms, bool candidateFirst)
    {
        var first = candidateFirst ? "candidate" : "control";
        var second = candidateFirst ? "control" : "candidate";
        if (arms.Contains(first)) yield return first;
        if (arms.Contains(second)) yield return second;
    }

    private sealed record MissingCell(int Session, string TaskId, BenchmarkMode Mode, string Arm);
    private static DurableQualificationResultException Invalid(string reason) => new($"Paired qualification campaign cannot resume: {reason}.");
}
