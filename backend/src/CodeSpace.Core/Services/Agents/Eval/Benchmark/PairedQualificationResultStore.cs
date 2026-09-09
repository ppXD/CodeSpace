using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

public sealed record PairedQualificationSealRequest
{
    public required Guid ObservationGroupId { get; init; }
    public required EvalSuiteManifest Manifest { get; init; }
    public required PairedQualificationOutcome Outcome { get; init; }
}

public interface IPairedQualificationResultStore
{
    Task<PairedQualificationOutcome> SealAsync(PairedQualificationSealRequest request, CancellationToken cancellationToken);
}

/// <summary>Seals a result only after the database contains the exact pre-registered observation universe.</summary>
public sealed class PairedQualificationResultStore : IPairedQualificationResultStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    public PairedQualificationResultStore(CodeSpaceDbContext db) => _db = db;

    public async Task<PairedQualificationOutcome> SealAsync(PairedQualificationSealRequest request, CancellationToken cancellationToken)
    {
        var protocol = await _db.PairedQualificationProtocol.AsNoTracking().SingleOrDefaultAsync(row => row.ObservationGroupId == request.ObservationGroupId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("protocol-not-found");
        var outcome = request.Outcome;
        var observations = await _db.BenchmarkResultRecord.AsNoTracking().Where(row => row.ObservationGroupId == protocol.ObservationGroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
        Validate(request, protocol, observations);

        var evidenceDigest = Digest(EvidenceJson(observations));
        var sealedOutcome = outcome with { EvidenceDigest = evidenceDigest };
        var outcomeJson = JsonSerializer.Serialize(sealedOutcome, Agents.AgentJson.Options);
        var resultDigest = Digest(JsonSerializer.Serialize(new[] { protocol.ProtocolDigest, evidenceDigest, protocol.StatisticsVersion, outcomeJson }, Agents.AgentJson.Options));
        _db.PairedQualificationResult.Add(new PairedQualificationResult
        {
            ObservationGroupId = protocol.ObservationGroupId, ProtocolDigest = protocol.ProtocolDigest,
            EvidenceDigest = evidenceDigest, ResultDigest = resultDigest, StatisticsVersion = protocol.StatisticsVersion,
            ExpectedObservationCount = ExpectedCount(protocol, request.Manifest), ObservationCount = observations.Count,
            QualifiedForCapabilityClaim = outcome.QualifiedForCapabilityClaim, OutcomeJson = outcomeJson,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return sealedOutcome with { ResultDigest = resultDigest };
    }

    private static void Validate(PairedQualificationSealRequest request, PairedQualificationProtocol protocol, IReadOnlyList<BenchmarkResultRecord> observations)
    {
        var manifest = request.Manifest;
        var outcome = request.Outcome;
        var expected = (from session in Enumerable.Range(0, protocol.SessionsPerCell)
                        from arm in new[] { "control", "candidate" }
                        from cell in manifest.Cells
                        select (session, arm, cell.TaskId, Mode: cell.Mode.ToString())).ToHashSet();
        var actual = observations.Select(row => (row.ObservationSession ?? -1, row.ObservationArm ?? string.Empty, row.TaskId, row.Mode)).ToHashSet();
        var expectedCount = ExpectedCount(protocol, manifest);

        if (observations.Count != expectedCount || actual.Count != observations.Count || !actual.SetEquals(expected)) throw Invalid("observation-census-mismatch");
        if (observations.Any(row => row.TeamId != protocol.TeamId || row.SuiteVersion != protocol.SuiteVersion || row.GitSha != protocol.CodeRevision || row.MaxCostUsd != protocol.MaxCostUsdPerLaunch)) throw Invalid("observation-protocol-mismatch");
        if (observations.Any(row => row.ModelCredentialModelId != (row.ObservationArm == "control" ? protocol.ControlModelRowId : protocol.CandidateModelRowId))) throw Invalid("observation-model-row-mismatch");
        if (protocol.RequiresResultDigest && observations.Any(row => string.IsNullOrWhiteSpace(row.SourceResultDigest))) throw Invalid("observation-result-digest-missing");
        if (outcome.ObservationGroupId != protocol.ObservationGroupId || outcome.ProtocolDigest != protocol.ProtocolDigest || outcome.CodeRevision != protocol.CodeRevision || outcome.SuiteDigest != protocol.SuiteDigest || outcome.SuiteVersion != protocol.SuiteVersion || outcome.PairedCells * 2 != expectedCount) throw Invalid("outcome-protocol-mismatch");
        if (protocol.StatisticsVersion != PairedQualificationOutcome.StatisticsVersion) throw Invalid("statistics-version-mismatch");
    }

    private static int ExpectedCount(PairedQualificationProtocol protocol, EvalSuiteManifest manifest) => checked(protocol.SessionsPerCell * 2 * manifest.Cells.Count);

    private static string EvidenceJson(IEnumerable<BenchmarkResultRecord> rows) => JsonSerializer.Serialize(rows
        .OrderBy(row => row.ObservationSession).ThenBy(row => row.TaskId, StringComparer.Ordinal).ThenBy(row => row.Mode, StringComparer.Ordinal).ThenBy(row => row.ObservationArm, StringComparer.Ordinal).ThenBy(row => row.Id)
        .Select(row => new
        {
            row.Id, row.TeamId, row.SuiteVersion, row.TaskId, row.Mode, row.Harness, row.Model, row.ModelCredentialModelId,
            row.SourceResultDigest,
            row.ObservedModel, row.ObservationGroupId, row.ObservationArm, row.ObservationSession, row.OutcomeState,
            row.OutcomeDetail, row.AgentRunId, row.Solved, row.RunStatus, row.ReviseRounds, row.McpFullCatalog,
            row.ExitReason, row.CostUsd, row.CostIndeterminate, row.MaxCostUsd, row.DurationSeconds, row.GitSha, row.CiRunId,
        }), Agents.AgentJson.Options);

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static DurableQualificationResultException Invalid(string reason) => new($"Paired qualification result cannot be sealed: {reason}.");
}
