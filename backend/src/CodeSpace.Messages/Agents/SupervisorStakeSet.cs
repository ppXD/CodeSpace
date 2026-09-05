namespace CodeSpace.Messages.Agents;

/// <summary>
/// The obligations ONE authorization wave stakes, as a single noun (Rule 18.1) — built by the spawn wave and by
/// <c>RealSupervisorActionExecutor.ExecuteRetryAsync</c>, consumed by the shared staging chokepoint.
///
/// <para>The three fields were three same-typed optional parameters on that chokepoint, so a call site that passed
/// them POSITIONALLY landed the delivery set in the acceptance slot and still compiled: a spec-less unit came back
/// owing a Required <c>acceptance:&lt;id&gt;</c> no grader would ever answer while its real delivery evidence was
/// demoted to authorized-not-applicable. Naming them on one record makes that cross-wire a compile error rather
/// than a silent contract inversion — the sets are NOT interchangeable, because acceptance is owed only where the
/// plan authored an oracle and delivery only where the unit expects its change to arrive.</para>
/// </summary>
public sealed record SupervisorStakeSet
{
    /// <summary>Each staged unit's EFFECTIVE contract hash (dispatch overrides included) — the content identity receipts bind to. A unit absent here has no known contract and stakes nothing at all.</summary>
    public required IReadOnlyDictionary<string, string> ContractHashes { get; init; }

    /// <summary>The units whose planned spec authored an oracle — the only ones that owe an acceptance verdict.</summary>
    public IReadOnlyCollection<string> AcceptanceUnits { get; init; } = Array.Empty<string>();

    /// <summary>The units whose planned spec expects its change to ARRIVE somewhere — the only ones that owe delivery/output evidence.</summary>
    public IReadOnlyCollection<string> DeliveryUnits { get; init; } = Array.Empty<string>();
}
