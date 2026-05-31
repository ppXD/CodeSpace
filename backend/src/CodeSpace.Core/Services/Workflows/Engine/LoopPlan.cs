using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The resolved runaway guard for one <c>flow.loop</c> execution — the engine's clamped view of the
/// author's <see cref="LoopConfig.MaxIterations"/> plus two hard ceilings that bound a loop even if
/// its termination condition never fires: a wall-clock budget and a total body-node-execution budget
/// (iterations × body size). Mirrors <c>RetryPlan</c>: a pure value with all clamping in one place so
/// the engine never trusts an unbounded config value.
/// </summary>
public readonly record struct LoopPlan(int MaxIterations, TimeSpan WallClock, int NodeBudget, LoopErrorHandling ErrorHandling)
{
    /// <summary>Absolute cap on iterations regardless of config — the primary runaway guard.</summary>
    public const int MaxIterationsCeiling = 1000;

    /// <summary>Wall-clock ceiling for the whole loop (all iterations). Exceeding it fails the node.</summary>
    public static readonly TimeSpan WallClockBudget = TimeSpan.FromMinutes(10);

    /// <summary>Ceiling on total body-node executions across all iterations (guards a big body × many passes).</summary>
    public const int NodeExecutionBudget = 10_000;

    /// <summary>Max loop-in-loop nesting depth (stack + runaway guard; mirrors the sub-workflow depth cap).</summary>
    public const int MaxNestingDepth = 8;

    /// <summary>Clamp the author's config into a safe plan. A missing/zero/negative max becomes 1; anything over the ceiling is capped.</summary>
    public static LoopPlan From(LoopConfig config) =>
        new(Math.Clamp(config.MaxIterations <= 0 ? 1 : config.MaxIterations, 1, MaxIterationsCeiling), WallClockBudget, NodeExecutionBudget, ParseErrorHandling(config.ErrorHandling));

    /// <summary>Lenient parse — only an explicit "continue" opts into continue-on-error; anything else (null, empty, typo) is the safe default Terminate.</summary>
    private static LoopErrorHandling ParseErrorHandling(string? raw) =>
        string.Equals(raw, "continue", StringComparison.OrdinalIgnoreCase) ? LoopErrorHandling.Continue : LoopErrorHandling.Terminate;
}
