using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;

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

public sealed record PairedQualificationCellAdmissionOutcome(Guid AdmissionId, PairedQualificationCellAdmissionDecision Decision, BenchmarkResult? CompletedResult);

public interface IPairedQualificationCellAdmissionStore
{
    Task<PairedQualificationCellAdmissionOutcome> AdmitAsync(PairedQualificationCellAdmissionRequest request, CancellationToken cancellationToken);
    Task CompleteAsync(Guid admissionId, BenchmarkResult result, CancellationToken cancellationToken);
}

/// <summary>Commits one immutable cell identity before model execution and single-assigns its terminal result. A duplicate is replayable only from that sealed result; an unsettled duplicate remains indeterminate.</summary>
public sealed class PairedQualificationCellAdmissionStore : IPairedQualificationCellAdmissionStore, IScopedDependency
{
    private const string UniqueConstraint = "uq_paired_qualification_cell_admission_key";
    private readonly CodeSpaceDbContext _db;

    public PairedQualificationCellAdmissionStore(CodeSpaceDbContext db) => _db = db;

    public async Task<PairedQualificationCellAdmissionOutcome> AdmitAsync(PairedQualificationCellAdmissionRequest request, CancellationToken cancellationToken)
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
            return new PairedQualificationCellAdmissionOutcome(row.Id, PairedQualificationCellAdmissionDecision.Admitted, null);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: UniqueConstraint })
        {
            _db.Entry(row).State = EntityState.Detached;
            var existing = await _db.PairedQualificationCellAdmission.AsNoTracking().SingleAsync(value => value.ObservationGroupId == request.ObservationGroupId && value.ObservationSession == request.ObservationSession && value.ObservationArm == request.ObservationArm && value.TaskId == request.TaskId && value.Mode == request.Mode.ToString(), cancellationToken).ConfigureAwait(false);
            return new PairedQualificationCellAdmissionOutcome(existing.Id, PairedQualificationCellAdmissionDecision.AlreadyAdmitted, Deserialize(existing.ResultJson));
        }
        catch
        {
            _db.Entry(row).State = EntityState.Detached;
            throw;
        }
    }

    public async Task CompleteAsync(Guid admissionId, BenchmarkResult result, CancellationToken cancellationToken)
    {
        if (admissionId == Guid.Empty) throw new ArgumentException("An admission id is required.", nameof(admissionId));
        var row = await _db.PairedQualificationCellAdmission.SingleOrDefaultAsync(value => value.Id == admissionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Paired qualification cell admission {admissionId} was not found.");
        if (row.TaskId != result.TaskId || row.Mode != result.Mode.ToString()) throw new InvalidOperationException("Paired qualification result identity does not match its admission.");
        var json = JsonSerializer.Serialize(result, Agents.AgentJson.Options);
        if (row.ResultJson is not null)
        {
            if (!Equivalent(row.ResultJson, json)) throw new InvalidOperationException("Paired qualification cell admission already has a different terminal result.");
            return;
        }
        row.ResultJson = json;
        row.CompletedAt = DateTimeOffset.UtcNow;
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.RaiseException, MessageText: var message } && message.Contains("admission is immutable", StringComparison.Ordinal))
        {
            _db.Entry(row).State = EntityState.Detached;
            var settled = await _db.PairedQualificationCellAdmission.AsNoTracking().SingleAsync(value => value.Id == admissionId, cancellationToken).ConfigureAwait(false);
            if (settled.ResultJson is not null && Equivalent(settled.ResultJson, json)) return;
            throw new InvalidOperationException("Paired qualification cell admission already has a different terminal result.", exception);
        }
    }

    private static BenchmarkResult? Deserialize(string? json) => json is null ? null : JsonSerializer.Deserialize<BenchmarkResult>(json, Agents.AgentJson.Options) ?? throw new InvalidOperationException("Paired qualification cell admission has an invalid terminal result.");

    private static bool Equivalent(string existing, string candidate)
    {
        using var existingDocument = JsonDocument.Parse(existing);
        using var candidateDocument = JsonDocument.Parse(candidate);
        return JsonElement.DeepEquals(existingDocument.RootElement, candidateDocument.RootElement);
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
