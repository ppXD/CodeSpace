namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The record that one team was pointed at the deployment default for one data class.
///
/// <para><b>This lane creates the table; the MATERIALIZER lane fills it.</b> No code in this build inserts a row.</para>
///
/// <para>It doubles as the record of EXPLICIT adoption: for a data class whose template is
/// <see cref="StorageDefaultAdoptionPolicy.Explicit"/>, the presence of a row for (team, data class) is exactly what
/// "this team adopted it" means — which is why <see cref="TeamId"/> plus <see cref="DataClassTypeKey"/> is the unique
/// key rather than the surrogate id. A later re-materialization updates the row the team already owns instead of
/// appending a second claim.</para>
/// </summary>
public class StorageDefaultMaterialization : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }

    /// <summary>The routed data class that was materialized for this team, for example <c>workflow-artifact/v1</c>.</summary>
    public string DataClassTypeKey { get; set; } = string.Empty;

    /// <summary>The team-owned storage profile the materializer produced. Bound to <see cref="TeamId"/> by a composite FK.</summary>
    public Guid StorageProfileId { get; set; }

    /// <summary>The <see cref="StorageDefault.Revision"/> this team was materialized from — compare against the template's current revision to find stale teams.</summary>
    public int SourceRevision { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }

    public Team Team { get; set; } = default!;
}
