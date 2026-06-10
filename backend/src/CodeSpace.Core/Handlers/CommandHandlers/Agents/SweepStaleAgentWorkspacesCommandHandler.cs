using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Fans out the sweep across every <see cref="IWorkspaceJanitor"/> and sums what each reclaimed.</summary>
public sealed class SweepStaleAgentWorkspacesCommandHandler : IRequestHandler<SweepStaleAgentWorkspacesCommand, SweepStaleAgentWorkspacesResponse>
{
    private readonly IEnumerable<IWorkspaceJanitor> _janitors;

    public SweepStaleAgentWorkspacesCommandHandler(IEnumerable<IWorkspaceJanitor> janitors) { _janitors = janitors; }

    public async Task<SweepStaleAgentWorkspacesResponse> Handle(SweepStaleAgentWorkspacesCommand request, CancellationToken cancellationToken)
    {
        var reclaimed = 0;

        foreach (var janitor in _janitors)
            reclaimed += await janitor.SweepStaleAsync(cancellationToken).ConfigureAwait(false);

        return new SweepStaleAgentWorkspacesResponse { Reclaimed = reclaimed };
    }
}
