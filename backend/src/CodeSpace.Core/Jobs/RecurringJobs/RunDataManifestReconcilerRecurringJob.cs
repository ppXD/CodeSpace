using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Gives the completeness manifest the reconciler every neighbouring plane already had.
///
/// <para>Every quarter hour because the shortfall it closes is not urgent and not growing: the run is already terminal
/// and its facet already reads not-complete, so what a later tick changes is only whether the answer is the honest
/// "nobody established this" instead of a shortfall against an expectation that will never be met. Thin Mediator
/// dispatcher (Rule 14); the work lives in <see cref="Services.RunData.IRunDataManifestReconciler"/>.</para>
/// </summary>
public sealed class RunDataManifestReconcilerRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public RunDataManifestReconcilerRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(RunDataManifestReconcilerRecurringJob);
    public string CronExpression => "*/15 * * * *";   // every 15 minutes

    public async Task Execute() => await _mediator.Send(new ReconcileRunDataManifestsCommand()).ConfigureAwait(false);
}
