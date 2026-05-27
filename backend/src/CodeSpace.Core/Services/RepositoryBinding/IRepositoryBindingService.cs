using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Dtos.Repositories;
using MediatR;

namespace CodeSpace.Core.Services.RepositoryBinding;

public interface IRepositoryBindingService
{
    Task<Repository> BindAsync(BindRepositoryRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// All-or-nothing bulk bind: iterates <paramref name="request"/>.ProjectIdentifiers and
    /// calls <see cref="BindAsync"/> per item. Any single failure throws and the surrounding
    /// <c>TransactionalBehavior</c> rolls back every prior bind in this batch.
    ///
    /// <para>The returned <see cref="BulkBindResult"/> always reports <c>FailureCount == 0</c>
    /// and every <c>Items[i].Error == null</c> — under the documented contract a partial
    /// success can never reach the caller. The fields exist on the wire shape for forward
    /// compatibility with a future partial-success endpoint that may live alongside this one.</para>
    /// </summary>
    Task<BulkBindResult> BindManyAsync(BindRepositoriesBulkRequest request, CancellationToken cancellationToken);

    Task<Unit> UnbindAsync(Guid repositoryId, CancellationToken cancellationToken);
    Task<CredentialProbeResult> TestAsync(Guid repositoryId, CancellationToken cancellationToken);
}
