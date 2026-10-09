using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Events;

namespace CodeSpace.Core.Services.Workflows.RunSources.Admission;

/// <summary>
/// The two questions a pull-request trigger an outsider can cause has to answer after its matcher said yes and before
/// a run starts: may THIS author — and whoever pushed its new commits — start a run here, and has this activation not
/// just started one for this PR at this head commit.
/// </summary>
public interface IPullRequestTriggerAdmission
{
    /// <summary>
    /// Returns when the run may start — and always for an event no outsider can cause. Throws
    /// <see cref="Exceptions.PullRequestAuthorRefusedException"/> or <see cref="Exceptions.PullRequestTriggerDebouncedException"/>,
    /// each costing only this activation's run. Completes the event's author association on the way, so the run's
    /// payload says who wrote the PR whichever filter the activation chose.
    /// </summary>
    Task EnsureAdmittedAsync(WorkflowActivation activation, NormalizedEvent normalizedEvent, JsonElement activationConfig, CancellationToken cancellationToken);
}
