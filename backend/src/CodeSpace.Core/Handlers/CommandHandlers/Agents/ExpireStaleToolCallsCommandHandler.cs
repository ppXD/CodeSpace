using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Reads the clock and hands off to <see cref="IToolCallLedgerService.ExpireStaleToolCallsAsync"/>,
/// which owns the candidate-set + per-row single-winner CAS. The reaper has no post-processing (no waiter to signal —
/// the stranded run is already terminal), so it calls the ledger service directly rather than a forwarding orchestration
/// layer. The clock read lives here so the service stays a pure, time-injected unit (mirrors the approval/decision reapers).</summary>
public sealed class ExpireStaleToolCallsCommandHandler : IRequestHandler<ExpireStaleToolCallsCommand, ExpireStaleToolCallsResponse>
{
    private readonly IToolCallLedgerService _ledger;

    public ExpireStaleToolCallsCommandHandler(IToolCallLedgerService ledger) { _ledger = ledger; }

    public async Task<ExpireStaleToolCallsResponse> Handle(ExpireStaleToolCallsCommand request, CancellationToken cancellationToken)
    {
        var failed = await _ledger.ExpireStaleToolCallsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        return new ExpireStaleToolCallsResponse { Failed = failed };
    }
}
