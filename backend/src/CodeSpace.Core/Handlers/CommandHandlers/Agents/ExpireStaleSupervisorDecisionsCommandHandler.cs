using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>
/// Rule 16 — thin handler. Reads the clock, derives the stale-Pending cutoff (now − retention window), and hands off to
/// <see cref="ISupervisorDecisionLog.ExpireStalePendingAsync"/>, which owns the per-row CAS sweep. The clock read + the
/// window constant live here so the service stays a pure, time-injected unit (it takes an absolute cutoff).
/// </summary>
public sealed class ExpireStaleSupervisorDecisionsCommandHandler : IRequestHandler<ExpireStaleSupervisorDecisionsCommand, ExpireStaleSupervisorDecisionsResponse>
{
    /// <summary>How long a decision may sit Pending before the reaper sweeps it. Generous (a real decision claims into Running fast); a still-Pending row this old is abandoned, not in flight.</summary>
    public static readonly TimeSpan StalePendingRetention = TimeSpan.FromHours(1);

    private readonly ISupervisorDecisionLog _log;

    public ExpireStaleSupervisorDecisionsCommandHandler(ISupervisorDecisionLog log) { _log = log; }

    public async Task<ExpireStaleSupervisorDecisionsResponse> Handle(ExpireStaleSupervisorDecisionsCommand request, CancellationToken cancellationToken)
    {
        var olderThan = DateTimeOffset.UtcNow - StalePendingRetention;

        var expired = await _log.ExpireStalePendingAsync(olderThan, cancellationToken).ConfigureAwait(false);

        return new ExpireStaleSupervisorDecisionsResponse { Expired = expired };
    }
}
