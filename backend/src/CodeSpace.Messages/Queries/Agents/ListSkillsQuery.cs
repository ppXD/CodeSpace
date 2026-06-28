using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// All active skills for the caller's team (Level-1 summaries), ordered by slug. Drives the editor's
/// skill-binding picker and any skill palette. Team scope comes from the X-Team-Id header.
/// </summary>
public sealed record ListSkillsQuery : IRequest<IReadOnlyList<SkillDefinitionSummary>>, IRequireTeamMembership;
