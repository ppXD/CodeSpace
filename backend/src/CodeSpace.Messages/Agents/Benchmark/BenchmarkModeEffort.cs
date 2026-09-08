using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// P19: the single source of truth mapping a <c>TaskLaunch*</c> <see cref="BenchmarkMode"/> arm onto the
/// <c>TaskLaunchRequest.RequestedEffort</c> string the real Launch entry (<c>ITaskLaunchService</c>) reads — so
/// "which arms are TaskLaunch" and "which effort each requests" live in exactly one place instead of two switches
/// that could drift. Mirrors <see cref="BenchmarkModeLabel"/>'s shape.
/// </summary>
public static class BenchmarkModeEffort
{
    /// <summary>True for the four arms that enter through the real Launch entry rather than a directly-created AgentRun.</summary>
    public static bool IsTaskLaunch(BenchmarkMode mode) =>
        mode is BenchmarkMode.TaskLaunchQuick or BenchmarkMode.TaskLaunchStandard or BenchmarkMode.TaskLaunchDeep or BenchmarkMode.TaskLaunchAuto;

    /// <summary>The <c>RequestedEffort</c> a TaskLaunch arm asks the router for. Null for <see cref="BenchmarkMode.TaskLaunchAuto"/> — the router's own "classify it" contract, never the literal string "auto".</summary>
    public static string? RequestedEffortFor(BenchmarkMode mode) => mode switch
    {
        BenchmarkMode.TaskLaunchQuick => TaskEffortModes.Quick,
        BenchmarkMode.TaskLaunchStandard => TaskEffortModes.Standard,
        BenchmarkMode.TaskLaunchDeep => TaskEffortModes.Deep,
        BenchmarkMode.TaskLaunchAuto => null,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a TaskLaunch arm — call IsTaskLaunch first."),
    };
}
