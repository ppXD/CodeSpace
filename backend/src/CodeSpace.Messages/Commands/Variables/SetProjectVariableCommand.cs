using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

/// <summary>
/// Upsert a project-scoped variable. New (project, name) → creates a row; existing → rotates
/// the value in place. Referenced from workflow definitions as <c>project.{slug}.{name}</c>.
/// Team comes from <c>X-Team-Id</c> header; project is identified by id (route).
/// </summary>
public sealed record SetProjectVariableCommand : IRequest<Unit>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }
    public required VariableValueType ValueType { get; init; }
    public required JsonElement Value { get; init; }
    public string? Description { get; init; }
}
