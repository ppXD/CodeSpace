using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

/// <summary>
/// Upsert a team-scoped variable. New (team, name) → creates a row; existing → rotates
/// the value in place. For <see cref="VariableValueType.Secret"/> the value must be a
/// JSON string and is AES-256-GCM encrypted at storage; for everything else the JSON value
/// is stored verbatim in <c>variable.value_plain</c>.
/// <para>Team comes from <c>X-Team-Id</c> header — not in the body.</para>
/// </summary>
public sealed record SetTeamVariableCommand : IRequest<Unit>, IRequireTeamMembership
{
    public required string Name { get; init; }
    public required VariableValueType ValueType { get; init; }
    public required JsonElement Value { get; init; }
    public string? Description { get; init; }
}
