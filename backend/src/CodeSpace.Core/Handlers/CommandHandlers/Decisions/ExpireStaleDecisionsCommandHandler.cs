using CodeSpace.Core.Services.Decisions;
using CodeSpace.Messages.Commands.Decisions;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Decisions;

/// <summary>Rule 16 — thin handler. Reads the clock and hands off to <see cref="IDecisionExpiryService"/>, which owns the
/// default-answer + signal + card-mirror orchestration. The clock read lives here so the service stays a pure, time-injected unit.</summary>
public sealed class ExpireStaleDecisionsCommandHandler : IRequestHandler<ExpireStaleDecisionsCommand, ExpireStaleDecisionsResponse>
{
    private readonly IDecisionExpiryService _expiry;

    public ExpireStaleDecisionsCommandHandler(IDecisionExpiryService expiry) { _expiry = expiry; }

    public async Task<ExpireStaleDecisionsResponse> Handle(ExpireStaleDecisionsCommand request, CancellationToken cancellationToken)
    {
        var defaulted = await _expiry.ExpireDueAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        return new ExpireStaleDecisionsResponse { Defaulted = defaulted };
    }
}
