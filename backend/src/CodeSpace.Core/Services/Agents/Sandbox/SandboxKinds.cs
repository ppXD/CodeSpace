namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// The runner-kind keys the sandbox registries resolve by — <see cref="ISandboxRunnerRegistry.Resolve"/> and the
/// matching workspace-provider / branch-integrator registries all index on this vocabulary, and the same string is
/// what a persisted <c>runner_kind</c> column and an authored node config carry.
///
/// <para>Lives at the Sandbox concern root rather than in <c>CodeSpace.Messages</c> deliberately (Rule 18): the
/// registry that gives these keys their meaning lives here, every consumer is a Core service that already sees this
/// namespace, and nothing in Messages needs the VALUE — the Messages records that carry a runner kind
/// (<c>AgentTask</c>, <c>RunCommandRequest</c>, <c>SandboxHandle</c>) declare it as an open string and only mention
/// <c>"local"</c> in prose. Putting it here keeps the key next to the thing that resolves it without adding a
/// dependency edge for anyone.</para>
/// </summary>
public static class SandboxKinds
{
    /// <summary>The in-process local runner (an OS process on the worker) and its matching local-git workspace provider — the only backend registered today, and the fallback when a request pins no kind.</summary>
    public const string Local = "local";
}
