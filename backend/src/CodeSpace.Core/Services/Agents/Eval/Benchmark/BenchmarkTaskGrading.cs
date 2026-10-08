using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The one "grade this workspace with the task's oracle" call, shared by every instrument that grades a
/// <see cref="BenchmarkTask"/> against a post-run workspace directory (<c>BenchmarkRunner</c> for a direct-harness
/// cell, <c>TaskLaunch.TaskLaunchBenchmarkCellRunner</c> for a Launch-mode cell) — grading is independent of HOW
/// the workspace got there, so both resolve the SAME grader + sandbox runner through this one seam instead of each
/// duplicating the two resolves. The runner is bound to the producing agent's posture
/// (<see cref="AcceptanceGradingPosturePolicy.Bind"/>), exactly as every acceptance grade's is: the check runs bytes
/// that agent wrote, so it never gets more memory, CPU or network than the agent had.
///
/// <para>The check never runs in the tree the agent wrote: it runs in a <see cref="BenchmarkOracleWorld"/> — a copy
/// outside the workspace whose judges come from the frozen fixture — so editing the judge buys nothing and is flagged.</para>
/// </summary>
internal static class BenchmarkTaskGrading
{
    public static async Task<BenchmarkGrade> GradeAsync(IBenchmarkGraderRegistry graders, Sandbox.ISandboxRunnerRegistry runners, BenchmarkTaskGradingRequest request, CancellationToken cancellationToken)
    {
        var grader = graders.Resolve(request.Task.Grading);

        var runner = AcceptanceGradingPosturePolicy.Bind(runners.Resolve(Sandbox.SandboxKinds.Local), request.Posture ?? AcceptanceGradingPosturePolicy.FailClosed);

        using var world = BenchmarkOracleWorld.Prepare(request.Task, request.WorkspaceDirectory, request.FixtureStager);

        var context = new BenchmarkGradingContext { Task = request.Task, WorkspaceDirectory = world.Directory, Runner = runner, TeamId = request.TeamId, ProducerModel = request.ProducerModel, PinnedOraclePaths = world.PinnedPaths };

        return world.Conclude(await grader.GradeAsync(context, cancellationToken).ConfigureAwait(false));
    }
}

internal sealed record BenchmarkTaskGradingRequest
{
    public required BenchmarkTask Task { get; init; }
    public required string WorkspaceDirectory { get; init; }
    public Guid? TeamId { get; init; }
    public ReviewModelIdentity? ProducerModel { get; init; }

    /// <summary>The posture of the agent run whose workspace this grades. The fixture's test command imports that agent's code, so it runs under the same ceilings and network cut. Required so no instrument can forget it; null grades under <see cref="AcceptanceGradingPosturePolicy.FailClosed"/>.</summary>
    public required AcceptanceGradingPosture? Posture { get; init; }

    /// <summary>The stager that staged this cell's fixture — the platform-owned source its judge is restored from before grading. Required so no instrument can forget it; null (a cell staged by hand) restores nothing and the grade says its judge was not platform-owned.</summary>
    public required IBenchmarkFixtureStager? FixtureStager { get; init; }
}
