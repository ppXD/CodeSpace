namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// The frozen-after-Phase-1 node contract. Every executable thing in a workflow — triggers,
/// regular steps, terminals, future iterators and branches — implements this interface.
///
/// Design freeze rationale: this is the ONLY surface plugin authors program against. Once
/// shipped, breaking it would invalidate every plugin in the wild. Subsequent capabilities
/// (timeouts, structured outputs, sub-workflows) extend the interface via OPTIONAL new
/// properties or sibling interfaces — never by adding required members to <c>INodeRuntime</c>
/// itself.
///
/// The engine asks ONLY two things of a node:
///   1. "Who are you?" — <see cref="TypeKey"/> + <see cref="Manifest"/>
///   2. "Execute this set of inputs+config and return outputs" — <see cref="RunAsync"/>
///
/// Everything else (validation, scope, persistence) is the engine's responsibility. A node
/// MUST be stateless: the engine may instantiate it once per process or once per call, and
/// concurrent invocations must not collide via instance fields.
/// </summary>
public interface INodeRuntime
{
    /// <summary>Globally unique stable string. Convention: <c>category.action</c> (e.g. "git.fetch_pr_diff", "llm.complete", "trigger.pr.opened").</summary>
    string TypeKey { get; }

    /// <summary>Static descriptor — display metadata + JSON schemas for config/input/output. Built once and cached.</summary>
    NodeManifest Manifest { get; }

    /// <summary>
    /// Run this node against the supplied context. MUST NOT throw for "expected" outcomes
    /// — return <see cref="NodeResult"/> with <c>Status = Failure</c> + a message. Throws
    /// are reserved for infrastructure failures (cancelled token, transport-level errors
    /// the engine should bubble up as run-level failure).
    /// </summary>
    Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken);
}
