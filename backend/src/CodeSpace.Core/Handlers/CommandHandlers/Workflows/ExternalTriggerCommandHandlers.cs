using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.Messages.Commands.Workflows;
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
        var trigger = await store.FindAsync(request.Slug, team.Id!.Value, cancellationToken).ConfigureAwait(false)
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
        var trigger = await store.FindAsync(request.Slug, team.Id!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("External trigger not found in this team.");
        return await store.RotateKeyAsync(trigger.Id, team.Id.Value, actor.Id!.Value, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RenameExternalTriggerCommandHandler(IExternalTriggerStore store, ICurrentTeam team, ICurrentUser actor)
    : IRequestHandler<RenameExternalTriggerCommand, Unit>
{
    public async Task<Unit> Handle(RenameExternalTriggerCommand request, CancellationToken cancellationToken)
    {
        var trigger = await store.FindAsync(request.Slug, team.Id!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("External trigger not found in this team.");
        await store.RenameAsync(trigger.Id, team.Id.Value, request.DesiredSlug, actor.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
