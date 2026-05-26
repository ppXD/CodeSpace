using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// Returns the canonical list of engine-injected <c>sys.*</c> variables — feeds the
/// editor's read-only System scope panel + the {{}} autocomplete picker. Static per release
/// (sourced from <c>SystemScopeKeys.Descriptors</c>); no team filtering, just an
/// authenticated-user gate so unauthenticated callers can't probe internals.
/// </summary>
public sealed record ListSystemVariablesQuery : IQuery<IReadOnlyList<SystemVariableDto>>, IRequireAuthenticatedUser
{
}
