using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Review;

/// <summary>Internal admission request. The parent token comes from the executing worker, never from task JSON.</summary>
public sealed record AgentReviewCreation(AgentRunOwnerToken ParentOwner, Guid TeamId, AgentTask Task);
