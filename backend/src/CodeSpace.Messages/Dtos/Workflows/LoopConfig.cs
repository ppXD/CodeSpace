using System.Text.Json;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Static configuration of a <c>flow.loop</c> container node (mirrors Dify's Loop node). The body
/// subgraph (nodes whose <c>ParentId</c> is this node) re-runs once per iteration; the engine seeds
/// a <c>loop.*</c> scope from <see cref="LoopVariables"/>, evaluates <see cref="Termination"/> after
/// each pass, and stops at the first met condition OR when <see cref="MaxIterations"/> is reached.
///
/// <para>Parsed from the node's raw <c>Config</c> JSON — NOT pre-resolved by the engine, because the
/// variable/termination refs must be re-resolved per iteration against the live loop scope.</para>
/// </summary>
public sealed record LoopConfig
{
    /// <summary>Mutable state carried across iterations, exposed to the body as <c>{{loop.&lt;name&gt;}}</c>.</summary>
    public IReadOnlyList<LoopVariable> LoopVariables { get; init; } = Array.Empty<LoopVariable>();

    /// <summary>When met, the loop stops (Dify's "loop termination condition"). Null/empty = rely on the cap alone.</summary>
    public LoopTermination? Termination { get; init; }

    /// <summary>Hard cap on passes — the runaway guard. Engine clamps to <c>[1, MaxIterationsCeiling]</c>. Default 10.</summary>
    public int MaxIterations { get; init; } = 10;

    /// <summary>
    /// Optional cap on how many independent body nodes run concurrently per iteration. Null (default)
    /// inherits the engine-wide setting (the <c>CODESPACE_WORKFLOW_MAX_PARALLELISM</c> env value);
    /// set it to throttle a body that hits a rate-limited resource (e.g. <c>1</c> = fully sequential).
    /// Engine clamps to <c>[1, 64]</c>. Absent from existing configs ⇒ no behaviour or hash change.
    /// </summary>
    public int? MaxParallelism { get; init; }

    /// <summary>
    /// What the loop does when a body node fails and has no <c>error</c> edge of its own. Parsed
    /// leniently by <c>LoopPlan</c> (unknown/empty ⇒ <see cref="LoopErrorHandling.Terminate"/>, the
    /// safe default). Values: <c>"terminate"</c> (Dify's "Terminate on error") | <c>"continue"</c>
    /// (Dify's "Continue on error"). A node that DOES have its own error edge is routed there first,
    /// regardless of this setting — this only governs an otherwise-unhandled body failure.
    /// </summary>
    public string? ErrorHandling { get; init; }
}

/// <summary>
/// How a <c>flow.loop</c> reacts to an unhandled body failure (a body node that fails and has no
/// <c>error</c> edge). <see cref="Terminate"/> fails the whole loop (its own error edge may still
/// catch it); <see cref="Continue"/> abandons just that pass — its loop-variable updates and the
/// termination check are skipped — and moves to the next iteration, so transient/partial failures
/// don't sink the whole run. The loop reports how many passes failed via its <c>failedIterations</c> output.
/// </summary>
public enum LoopErrorHandling { Terminate, Continue }

/// <summary>
/// One loop variable. Initialised once (before the first pass) from EITHER a <see cref="Ref"/>
/// (a <c>{{...}}</c> template resolved against the outer scope — Dify's "Variable" source) OR a
/// constant <see cref="Value"/> (Dify's "Constant" source). Optionally re-evaluated at the end of
/// each pass from <see cref="Update"/> (a <c>{{...}}</c> template against the just-finished body
/// scope), which is how state threads from one iteration to the next.
/// </summary>
public sealed record LoopVariable
{
    public required string Name { get; init; }

    /// <summary>Declared type, display/validation only (String/Number/Boolean/…). Engine treats values structurally.</summary>
    public string Type { get; init; } = "String";

    /// <summary>Init from a variable reference — a <c>{{...}}</c> template. Mutually exclusive with <see cref="Value"/>.</summary>
    public string? Ref { get; init; }

    /// <summary>Init from a constant literal of any JSON type. Mutually exclusive with <see cref="Ref"/>.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>Optional per-iteration update — a <c>{{...}}</c> template re-resolved at pass end to set the next value.</summary>
    public string? Update { get; init; }
}

/// <summary>A set of conditions combined by <see cref="Logic"/>; the loop stops when the set evaluates true.</summary>
public sealed record LoopTermination
{
    /// <summary>How to combine conditions: <c>"and"</c> (all) or <c>"or"</c> (any). Default <c>"and"</c>.</summary>
    public string Logic { get; init; } = "and";

    public IReadOnlyList<LoopCondition> Conditions { get; init; } = Array.Empty<LoopCondition>();
}

/// <summary>
/// One termination row: resolve <see cref="Ref"/> against the loop scope, compare with operator
/// <see cref="Op"/> against <see cref="Value"/>. Operators mirror Dify's dropdown:
/// <c>eq · neq · contains · not_contains · startsWith · endsWith · is_empty · is_not_empty</c>
/// (the last two ignore <see cref="Value"/>).
/// </summary>
public sealed record LoopCondition
{
    public required string Ref { get; init; }
    public required string Op { get; init; }
    public string? Value { get; init; }
}
