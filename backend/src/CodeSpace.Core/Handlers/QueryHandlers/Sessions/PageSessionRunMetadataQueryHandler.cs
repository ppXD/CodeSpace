using System.ComponentModel.DataAnnotations;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Sessions.Exceptions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

internal sealed class PageSessionRunMetadataQueryHandler : IRequestHandler<PageSessionRunMetadataQuery, SessionRunMetadataPage?>
{
    private readonly ISessionRunMetadataPageReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public PageSessionRunMetadataQueryHandler(ISessionRunMetadataPageReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public Task<SessionRunMetadataPage?> Handle(PageSessionRunMetadataQuery request, CancellationToken cancellationToken)
    {
        EnsureValid(request);
        var selector = request.SessionId.HasValue
            ? new SessionRunMetadataSelector { Kind = SessionRunMetadataSelectorKind.Session, SessionId = request.SessionId }
            : new SessionRunMetadataSelector { Kind = SessionRunMetadataSelectorKind.RunAnchor, RunAnchorId = request.RunAnchorId };
        return _reader.ReadAsync(new SessionRunMetadataPageRequest
        {
            TeamId = _currentTeam.Id!.Value,
            Selector = selector,
            Direction = request.Direction,
            Cursor = request.Cursor,
            Limit = request.Limit,
        }, cancellationToken);
    }

    private static void EnsureValid(PageSessionRunMetadataQuery request)
    {
        var errors = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true)) return;
        throw new SessionRunMetadataPageRequestException(errors.Select(error => error.ErrorMessage ?? "Invalid value.").ToList());
    }
}
