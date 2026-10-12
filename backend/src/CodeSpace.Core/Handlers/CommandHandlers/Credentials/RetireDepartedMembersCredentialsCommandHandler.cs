using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="ICredentialRevocationService.RetireDepartedAsync"/>.</summary>
public sealed class RetireDepartedMembersCredentialsCommandHandler : IRequestHandler<RetireDepartedMembersCredentialsCommand, int>
{
    private readonly ICredentialRevocationService _revocation;

    public RetireDepartedMembersCredentialsCommandHandler(ICredentialRevocationService revocation)
    {
        _revocation = revocation;
    }

    public async Task<int> Handle(RetireDepartedMembersCredentialsCommand request, CancellationToken cancellationToken)
    {
        return await _revocation.RetireDepartedAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
    }
}
