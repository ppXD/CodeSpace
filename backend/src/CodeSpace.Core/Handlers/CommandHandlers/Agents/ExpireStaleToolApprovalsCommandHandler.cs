using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Reads the clock and hands off to <see cref="IToolApprovalExpiryService"/>,
/// which owns the expire + signal + card-mirror orchestration. The clock read lives here so the service stays a
/// pure, time-injected unit.</summary>
public sealed class ExpireStaleToolApprovalsCommandHandler : IRequestHandler<ExpireStaleToolApprovalsCommand, ExpireStaleToolApprovalsResponse>
{
    private readonly IToolApprovalExpiryService _expiry;

    public ExpireStaleToolApprovalsCommandHandler(IToolApprovalExpiryService expiry) { _expiry = expiry; }

    public async Task<ExpireStaleToolApprovalsResponse> Handle(ExpireStaleToolApprovalsCommand request, CancellationToken cancellationToken)
    {
        var expired = await _expiry.ExpireDueAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        return new ExpireStaleToolApprovalsResponse { Expired = expired };
    }
}
