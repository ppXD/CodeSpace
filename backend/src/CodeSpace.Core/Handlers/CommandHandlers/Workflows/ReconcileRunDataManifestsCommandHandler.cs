using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>Rule 16 — thin handler. The bounded sweep lives in <see cref="IRunDataManifestReconciler"/>.</summary>
public sealed class ReconcileRunDataManifestsCommandHandler : IRequestHandler<ReconcileRunDataManifestsCommand, ReconcileRunDataManifestsResponse>
{
    /// <summary>Bounded per pass: every candidate costs its own contained unit of work, and oldest-first means a deployment still converges on un-stating everything abandoned.</summary>
    internal const int BatchSize = 100;

    private readonly IRunDataManifestReconciler _reconciler;

    public ReconcileRunDataManifestsCommandHandler(IRunDataManifestReconciler reconciler) { _reconciler = reconciler; }

    public async Task<ReconcileRunDataManifestsResponse> Handle(ReconcileRunDataManifestsCommand request, CancellationToken cancellationToken)
    {
        var reconciliation = await _reconciler.ReconcileUnattributedShortfallsAsync(BatchSize, cancellationToken).ConfigureAwait(false);

        return new ReconcileRunDataManifestsResponse
        {
            Examined = reconciliation.Examined, Unstated = reconciliation.Unstated, Unchanged = reconciliation.Unchanged,
        };
    }
}
