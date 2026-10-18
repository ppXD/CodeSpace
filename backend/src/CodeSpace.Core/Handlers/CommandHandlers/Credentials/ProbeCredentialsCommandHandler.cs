using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="ICredentialHealthService.ProbeBatchAsync"/>.</summary>
public sealed class ProbeCredentialsCommandHandler : IRequestHandler<ProbeCredentialsCommand, int>
{
    private readonly ICredentialHealthService _health;

    public ProbeCredentialsCommandHandler(ICredentialHealthService health)
    {
        _health = health;
    }

    public async Task<int> Handle(ProbeCredentialsCommand request, CancellationToken cancellationToken)
    {
        return await _health.ProbeBatchAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
    }
}
