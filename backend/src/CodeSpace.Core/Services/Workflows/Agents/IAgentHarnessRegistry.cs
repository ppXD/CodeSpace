namespace CodeSpace.Core.Services.Workflows.Agents;

/// <summary>
/// Resolves an <see cref="IAgentHarness"/> by its <see cref="IAgentHarness.Kind"/> — same shape as
/// <c>ISandboxRunnerRegistry</c> / <c>ILLMClientRegistry</c>. The caller (AgentRunService) picks the
/// kind from the task; this only maps kind → adapter. A new harness becomes resolvable by registering
/// its class — no edit here.
/// </summary>
public interface IAgentHarnessRegistry
{
    /// <summary>Every registered harness — for the "which harnesses + models are available" surface.</summary>
    IReadOnlyList<IAgentHarness> All { get; }

    /// <summary>Resolve the harness for <paramref name="kind"/>. Throws when none is registered for that kind.</summary>
    IAgentHarness Resolve(string kind);
}
