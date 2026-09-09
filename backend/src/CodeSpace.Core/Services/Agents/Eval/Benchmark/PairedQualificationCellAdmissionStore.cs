using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

public enum PairedQualificationCellAdmissionDecision
{
    Admitted = 0,
    AlreadyAdmitted = 1,
}

public sealed record PairedQualificationCellAdmissionRequest
{
    public required Guid ObservationGroupId { get; init; }
    public required int ObservationSession { get; init; }
    public required string ObservationArm { get; init; }
    public required string TaskId { get; init; }
    public required BenchmarkMode Mode { get; init; }
    public required Guid ModelCredentialModelId { get; init; }
}

public interface IPairedQualificationCellAdmissionStore
{
    Task<PairedQualificationCellAdmissionDecision> AdmitAsync(PairedQualificationCellAdmissionRequest request, CancellationToken cancellationToken);
}

/// <summary>Commits one immutable cell authorization before model execution. A duplicate is an ambiguous prior execution, never permission to replay.</summary>
public sealed class PairedQualificationCellAdmissionStore : IPairedQualificationCellAdmissionStore, IScopedDependency
{
    private const string UniqueConstraint = "uq_paired_qualification_cell_admission_key";
    private readonly CodeSpaceDbContext _db;

    public PairedQualificationCellAdmissionStore(CodeSpaceDbContext db) => _db = db;

    public async Task<PairedQualificationCellAdmissionDecision> AdmitAsync(PairedQualificationCellAdmissionRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var row = new PairedQualificationCellAdmission
        {
            Id = Guid.NewGuid(), ObservationGroupId = request.ObservationGroupId, ObservationSession = request.ObservationSession,
            ObservationArm = request.ObservationArm, TaskId = request.TaskId, Mode = request.Mode.ToString(), ModelCredentialModelId = request.ModelCredentialModelId,
        };
        _db.PairedQualificationCellAdmission.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return PairedQualificationCellAdmissionDecision.Admitted;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: UniqueConstraint })
        {
            _db.Entry(row).State = EntityState.Detached;
            return PairedQualificationCellAdmissionDecision.AlreadyAdmitted;
        }
        catch
        {
            _db.Entry(row).State = EntityState.Detached;
            throw;
        }
    }

    private static void Validate(PairedQualificationCellAdmissionRequest request)
    {
        if (request.ObservationGroupId == Guid.Empty) throw new ArgumentException("An observation group is required.", nameof(request));
        if (request.ObservationSession < 0) throw new ArgumentOutOfRangeException(nameof(request), "Observation session cannot be negative.");
        if (request.ObservationArm is not ("control" or "candidate")) throw new ArgumentException("A known paired arm is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TaskId)) throw new ArgumentException("A benchmark task is required.", nameof(request));
        if (request.ModelCredentialModelId == Guid.Empty) throw new ArgumentException("An exact credential-model row is required.", nameof(request));
    }
}
