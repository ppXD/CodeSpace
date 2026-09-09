namespace CodeSpace.Core.Persistence.Entities;

/// <summary>Immutable authorization for one paid paired cell. Its existence means execution may have crossed the external-call boundary, so absence of the matching observation is indeterminate and must never be replayed automatically.</summary>
public sealed class PairedQualificationCellAdmission : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid ObservationGroupId { get; set; }
    public int ObservationSession { get; set; }
    public string ObservationArm { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public Guid ModelCredentialModelId { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
