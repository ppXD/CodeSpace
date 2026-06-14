using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Durably expire every undecided tool-call approval (item D3) whose deadline has passed, so a re-call gets a clean
/// terminal instead of an approval that lingers forever. Dispatched by the recurring reaper job each minute; can also
/// be sent ad-hoc (admin path / tests).
///
/// <para>NOT tenant-scoped — a system-wide internal sweep with no actor context. Finds no governed rows when governance
/// is off (a cheap no-op). Returns the count expired for log surfacing + the recurring-job result.</para>
/// </summary>
public sealed record ExpireStaleToolApprovalsCommand : ICommand<ExpireStaleToolApprovalsResponse>;

/// <summary>Count of undecided approvals the reaper durably flipped to Expired this tick (the ledger-CAS winners).</summary>
public sealed record ExpireStaleToolApprovalsResponse
{
    public required int Expired { get; init; }
}
