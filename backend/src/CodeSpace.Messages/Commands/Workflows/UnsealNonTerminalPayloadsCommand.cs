using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// The pre-rollback step for workers: every queued agent run and every workflow run that is not yet terminal and still
/// holds a sealed payload (0247) gets its exact payload back in the public column and the ciphertext cleared — the row
/// the previous build wrote and the only one a previous-version worker can execute; left sealed, that worker runs the
/// mask.
///
/// <para>Order for a worker rollback: stop every current worker and wait until none is live — its servers gone from the
/// Hangfire server list, or two minutes without a heartbeat — and 30 seconds more, one fleet-gate snapshot; send this
/// from a current API pod; then start the previous-version workers. From the end of that wait, the fleet gate holds
/// sealing back (first no worker is live, then a previous-version one is), so current pods write every new hand-off
/// unsealed — an agent task, a child's payload, a replay or rerun fork of a sealed run — and this only has to catch the
/// rows sealed before. Terminal rows stay sealed: nothing executes them again, and a fork of one is written unsealed by
/// the current API that stages it. A Running agent run stays sealed too: no worker launches it again, and the sealed
/// copy is what a current worker's re-attach rebuilds its redactor from. A previous-version API pod knows no sealed
/// column: a replay or rerun it stages of a sealed run copies the masked column alone and runs with the mask, whichever
/// worker executes it.</para>
///
/// <para>Instance-level and global-admin only: it spans every team and puts secret values back into columns team members
/// can read, exactly as the previous build stored them. NOT transactional (<see cref="INonTransactionalCommand"/>):
/// each row is restored by its own compare-and-swap, so a row a worker rewrote meanwhile is left as found and counted,
/// and one unopenable row never undoes the others. Idempotent — a second send finds nothing left to restore.</para>
/// </summary>
public sealed record UnsealNonTerminalPayloadsCommand : ICommand<UnsealNonTerminalPayloadsResponse>, IRequireGlobalAdmin, INonTransactionalCommand;

/// <summary>What the pass restored, and what it had to leave sealed: <see cref="Raced"/> rows changed under it (send again once the workers are stopped), <see cref="Unopenable"/> rows whose ciphertext this pod's keys cannot open.</summary>
public sealed record UnsealNonTerminalPayloadsResponse
{
    public required int AgentTasks { get; init; }
    public required int RunPayloads { get; init; }
    public required int Raced { get; init; }
    public required int Unopenable { get; init; }
}
