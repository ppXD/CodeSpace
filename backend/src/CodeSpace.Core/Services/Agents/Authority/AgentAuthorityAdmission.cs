using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Authority;

public sealed record AgentAuthorityAdmission(AgentTask Task, Guid TeamId, Guid AgentRunId, Guid? WorkflowRunId);
