using System.ComponentModel.DataAnnotations;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class PageToolCallsQueryHandler : IRequestHandler<PageToolCallsQuery, ToolCallPage?>
{
    private readonly IToolCallAuditReader _auditReader;
    private readonly ICurrentTeam _currentTeam;

    public PageToolCallsQueryHandler(IToolCallAuditReader auditReader, ICurrentTeam currentTeam)
    {
        _auditReader = auditReader;
        _currentTeam = currentTeam;
    }

    public async Task<ToolCallPage?> Handle(PageToolCallsQuery request, CancellationToken cancellationToken)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true))
            throw new ToolCallPageRequestException(errors.Select(error => error.ErrorMessage ?? "Invalid value.").ToList());

        return await _auditReader.PageForRunAsync(request, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
    }
}
