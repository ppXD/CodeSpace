using CodeSpace.Core.Services.Workflows.ToolCalls;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class ProjectWorkflowRunToolCallsCommandHandler : IRequestHandler<ProjectWorkflowRunToolCallsCommand, int>
{
    private readonly IWorkflowRunToolCallProjector _projector;
    private readonly ILogger<ProjectWorkflowRunToolCallsCommandHandler> _logger;

    public ProjectWorkflowRunToolCallsCommandHandler(IWorkflowRunToolCallProjector projector, ILogger<ProjectWorkflowRunToolCallsCommandHandler> logger)
    {
        _projector = projector;
        _logger = logger;
    }

    public async Task<int> Handle(ProjectWorkflowRunToolCallsCommand request, CancellationToken cancellationToken)
    {
        var result = await _projector.SweepAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
        if (result.InvalidScopeSourcesObserved + result.InvalidSourceFactsObserved > 0)
            _logger.LogWarning("Bounded non-cumulative workflow tool-call sample observed invalid sources: invalidScope={InvalidScope}, invalidFacts={InvalidFacts}, sample={Sample}",
                result.InvalidScopeSourcesObserved, result.InvalidSourceFactsObserved, result.DiagnosticRowsObserved);
        _logger.LogDebug("Bounded non-cumulative workflow tool-call sample: legacy={Legacy}, decisions={Decisions}, standalone={Standalone}, deferred={Deferred}, sample={Sample}",
            result.LegacyUnorderedSourcesObserved, result.DecisionSourcesObserved, result.StandaloneSourcesObserved,
            result.DeferredLiveSourcesObserved, result.DiagnosticRowsObserved);
        return result.CallsProjected;
    }
}
