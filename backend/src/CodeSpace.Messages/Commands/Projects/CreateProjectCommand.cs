using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Projects;

/// <summary>
/// Create a new project under the caller's team. The wire-format contract is
/// <see cref="Name"/> + (optional) <see cref="Description"/> — the slug is derived
/// from the name by <c>ProjectService.SlugifyName</c> so operators never type the
/// identifier directly. On collision the service throws a typed error and the
/// operator picks a different name (we never silently mangle a chosen name into
/// <c>"foo-2"</c> because the slug is part of the variable-path contract).
/// </summary>
public sealed record CreateProjectCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}
