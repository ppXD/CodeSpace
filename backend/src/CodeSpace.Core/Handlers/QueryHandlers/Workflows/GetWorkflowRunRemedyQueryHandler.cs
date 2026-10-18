using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>The lean read behind the run page's remediation card: the remedy alone, for the asking user, without the run detail around it.</summary>
public sealed class GetWorkflowRunRemedyQueryHandler : IRequestHandler<GetWorkflowRunRemedyQuery, ProviderRemedy?>
{
    private readonly IProviderRemedyService _remedies;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunRemedyQueryHandler(IProviderRemedyService remedies, ICurrentTeam currentTeam)
    {
        _remedies = remedies;
        _currentTeam = currentTeam;
    }

    public async Task<ProviderRemedy?> Handle(GetWorkflowRunRemedyQuery request, CancellationToken cancellationToken) =>
        await _remedies.GetForRunAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
