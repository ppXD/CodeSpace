namespace CodeSpace.Core.Persistence.Entities;

/// <summary>Immutable identity and single-assignment terminal result for one paid paired cell. An unsettled row means execution may have crossed the external-call boundary and must never be replayed automatically; a settled row can repair a missing observation without another provider call.</summary>
public sealed class PairedQualificationCellAdmission : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid ObservationGroupId { get; set; }
    public int ObservationSession { get; set; }
    public string ObservationArm { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public Guid ModelCredentialModelId { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
