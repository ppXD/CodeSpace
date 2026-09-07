namespace CodeSpace.Messages.Agents;

/// <summary>A server invocation identity, independent of the observer's owner epoch. Omitted only by the existing spool-key compatibility producer; omission does not invent a native process attempt.</summary>
public sealed record SandboxLaunchIdentity(Guid TeamId, Guid AgentRunId, Guid ExecutionId, Guid AttemptId);

/// <summary>Replays bind the same local slot to the same exact invocation. Cancellation stops waiting; it does not revoke a committed execution.</summary>
public sealed record SandboxLaunchRequest(SandboxSpec Spec, string SpoolKey, SandboxLaunchIdentity? Identity = null);
