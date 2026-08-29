namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// How long a stored artifact's plane intends to keep it. Consumed today by the agent-run log stream, which stamps
/// each segment's tier at capture time.
///
/// <para>Previously co-located with the run-grain lineage entity that never got a writer; it survived that table's
/// removal because live code stamps and reads it — the tier is a property of the bytes, not of the lineage row.</para>
/// </summary>
public enum ArtifactRetention
{
    Ephemeral,
    Run,
    Team,
    Compliance,
    Permanent,
}
