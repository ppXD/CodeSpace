using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

/// <summary>
/// Upsert a project-scoped variable. Workflows reference these as <c>project.{slug}.{name}</c>.
/// The service verifies the project belongs to the caller's current team — wrong-team or
/// phantom project surfaces as <see cref="KeyNotFoundException"/> (404, same conflation as
/// repository / credential). <see cref="ProjectId"/> comes from the URL — controller does
/// <c>command with { ProjectId = routeId, Name = routeName }</c> before dispatch.
/// </summary>
public sealed record SetProjectVariableCommand : ICommand<Unit>, IRequireTeamMembership
{
    public Guid ProjectId { get; init; }
    public required string Name { get; init; }
    public required VariableValueType ValueType { get; init; }
    public required JsonElement Value { get; init; }
    public string? Description { get; init; }
}
