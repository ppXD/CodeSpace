using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Dtos.Repositories;
using MediatR;

namespace CodeSpace.Core.Services.RepositoryBinding;

public interface IRepositoryBindingService
{
    Task<Repository> BindAsync(BindRepositoryRequest request, CancellationToken cancellationToken);
    Task<Unit> UnbindAsync(Guid repositoryId, CancellationToken cancellationToken);
    Task<CredentialProbeResult> TestAsync(Guid repositoryId, CancellationToken cancellationToken);
}
