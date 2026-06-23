using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Durably terminalize every side-effecting tool-call row stranded non-terminal by a host crash (the SIGKILL window
/// between the Pending INSERT and the recovery write), so a re-call of the deterministic key gets a clean terminal
/// instead of hanging InFlight forever. Dispatched by the recurring reaper job each minute; can also be sent ad-hoc
/// (admin path / tests).
///
/// <para>NOT tenant-scoped — a system-wide internal sweep with no actor context. Finds no rows when governance is off
/// or nothing crashed (a cheap no-op). Returns the count terminalized for log surfacing + the recurring-job result.</para>
/// </summary>
public sealed record ExpireStaleToolCallsCommand : ICommand<ExpireStaleToolCallsResponse>;

/// <summary>Count of stranded side-effecting calls the reaper durably flipped to Failed this tick (the ledger-CAS winners).</summary>
public sealed record ExpireStaleToolCallsResponse
{
    public required int Failed { get; init; }
}
