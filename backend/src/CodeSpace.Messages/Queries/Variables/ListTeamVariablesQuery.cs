using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Variables;
using MediatR;

namespace CodeSpace.Messages.Queries.Variables;

/// <summary>
/// Lists every active team-scoped variable. Secret rows have <c>ValuePlain == null</c>
/// — the API surface never returns secret plaintext. Team comes from <c>X-Team-Id</c>.
/// </summary>
public sealed record ListTeamVariablesQuery : IRequest<IReadOnlyList<VariableSummary>>, IRequireTeamMembership
{
}
