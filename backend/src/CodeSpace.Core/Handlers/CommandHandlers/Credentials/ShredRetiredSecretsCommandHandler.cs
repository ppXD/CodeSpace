using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="IRetiredSecretShredService.ShredAsync"/>.</summary>
public sealed class ShredRetiredSecretsCommandHandler : IRequestHandler<ShredRetiredSecretsCommand, int>
{
    private readonly IRetiredSecretShredService _shred;

    public ShredRetiredSecretsCommandHandler(IRetiredSecretShredService shred)
    {
        _shred = shred;
    }

    public async Task<int> Handle(ShredRetiredSecretsCommand request, CancellationToken cancellationToken)
    {
        return await _shred.ShredAsync(cancellationToken).ConfigureAwait(false);
    }
}
