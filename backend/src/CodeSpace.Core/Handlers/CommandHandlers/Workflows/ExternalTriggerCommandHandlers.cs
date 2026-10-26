using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class RevealExternalTriggerKeyCommandHandler(
    IExternalTriggerStore store, CodeSpaceDbContext db, ExternalTriggerKeySealer sealer,
    ICurrentTeam team, ICurrentUser actor, ILogger<RevealExternalTriggerKeyCommandHandler> logger)
    : IRequestHandler<RevealExternalTriggerKeyCommand, string>
{
    public async Task<string> Handle(RevealExternalTriggerKeyCommand request, CancellationToken cancellationToken)
    {
        var trigger = await store.FindAsync(request.TriggerId, team.Id!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("External trigger not found in this team.");
        var ciphertext = await db.WorkflowExternalTrigger.AsNoTracking()
            .Where(t => t.Id == trigger.Id && t.TeamId == team.Id.Value && t.DeletedDate == null)
            .Select(t => t.KeyCiphertext).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("External trigger not found in this team.");
        var key = sealer.Open(ciphertext);
        logger.LogWarning("External trigger {TriggerId} key revealed to user {UserId} in team {TeamId}", trigger.Id, actor.Id, team.Id);
        return key;
    }
}

public sealed class RotateExternalTriggerKeyCommandHandler(IExternalTriggerStore store, ICurrentTeam team, ICurrentUser actor)
    : IRequestHandler<RotateExternalTriggerKeyCommand, string>
{
    public async Task<string> Handle(RotateExternalTriggerKeyCommand request, CancellationToken cancellationToken)
    {
        var trigger = await store.FindAsync(request.TriggerId, team.Id!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("External trigger not found in this team.");
        return await store.RotateKeyAsync(trigger.Id, team.Id.Value, actor.Id!.Value, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ReceiveExternalTriggerCommandHandler(IExternalTriggerIngress ingress)
    : IRequestHandler<ReceiveExternalTriggerCommand, ExternalTriggerDeliveryOutcome>
{
    public async Task<ExternalTriggerDeliveryOutcome> Handle(ReceiveExternalTriggerCommand request, CancellationToken cancellationToken)
    {
        var outcome = await ingress.ReceiveAsync(new ExternalTriggerCall(
                request.TriggerId, request.PresentedKey, request.IdempotencyKey, string.Empty, request.IdempotencyRejected, request.Body),
            cancellationToken).ConfigureAwait(false);

        return new ExternalTriggerDeliveryOutcome
        {
            Kind = outcome.Kind switch
            {
                ExternalTriggerOutcomeKind.Accepted => ExternalTriggerDeliveryKind.Accepted,
                ExternalTriggerOutcomeKind.Deduplicated => ExternalTriggerDeliveryKind.Deduplicated,
                ExternalTriggerOutcomeKind.NotFound => ExternalTriggerDeliveryKind.NotFound,
                ExternalTriggerOutcomeKind.Unauthorized => ExternalTriggerDeliveryKind.Unauthorized,
                ExternalTriggerOutcomeKind.Forbidden => ExternalTriggerDeliveryKind.Forbidden,
                ExternalTriggerOutcomeKind.Unprocessable => ExternalTriggerDeliveryKind.Unprocessable,
                ExternalTriggerOutcomeKind.InvalidPayload => ExternalTriggerDeliveryKind.InvalidPayload,
                ExternalTriggerOutcomeKind.RateLimited => ExternalTriggerDeliveryKind.RateLimited,
                _ => throw new InvalidOperationException($"Unknown external trigger outcome: {outcome.Kind}"),
            },
            RunId = outcome.RunId,
            Errors = outcome.Errors,
            Code = outcome.Code,
        };
    }
}

public sealed class VerifyExternalTriggerQueryHandler(IExternalTriggerIngress ingress)
    : IRequestHandler<VerifyExternalTriggerQuery, ExternalTriggerVerificationOutcome>
{
    public async Task<ExternalTriggerVerificationOutcome> Handle(VerifyExternalTriggerQuery request, CancellationToken cancellationToken)
    {
        var verification = await ingress.VerifyAsync(request.TriggerId, request.PresentedKey, cancellationToken).ConfigureAwait(false);
        var kind = verification.Kind switch
        {
            ExternalTriggerOutcomeKind.Accepted => ExternalTriggerDeliveryKind.Accepted,
            ExternalTriggerOutcomeKind.NotFound => ExternalTriggerDeliveryKind.NotFound,
            ExternalTriggerOutcomeKind.Unauthorized => ExternalTriggerDeliveryKind.Unauthorized,
            _ => throw new InvalidOperationException($"Unexpected external trigger verification outcome: {verification.Kind}"),
        };
        return new ExternalTriggerVerificationOutcome(kind);
    }
}
