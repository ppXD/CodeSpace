using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Author a new Agent persona under the caller's team. The wire contract is the persona's editable
/// surface — the @-mention slug is DERIVED from <see cref="Name"/> (operators never type the handle),
/// and <see cref="AgentDefinitionOrigin.Authored"/> is set server-side. On a slug collision the service
/// throws a typed error and the operator picks a different name (we never silently mangle into "foo-2").
/// </summary>
public sealed record CreateAgentDefinitionCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Model { get; init; }
    public string? DefaultAutonomy { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
}
