using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The one "grade this workspace with the task's oracle" call, shared by every instrument that grades a
/// <see cref="BenchmarkTask"/> against a post-run workspace directory (<c>BenchmarkRunner</c> for a direct-harness
/// cell, <c>TaskLaunch.TaskLaunchBenchmarkCellRunner</c> for a Launch-mode cell) — grading is independent of HOW
/// the workspace got there, so both resolve the SAME grader + sandbox runner through this one seam instead of each
/// duplicating the two resolves.
/// </summary>
internal static class BenchmarkTaskGrading
{
    public static async Task<BenchmarkGrade> GradeAsync(IBenchmarkGraderRegistry graders, Sandbox.ISandboxRunnerRegistry runners, BenchmarkTaskGradingRequest request, CancellationToken cancellationToken)
    {
        var grader = graders.Resolve(request.Task.Grading);

        var context = new BenchmarkGradingContext { Task = request.Task, WorkspaceDirectory = request.WorkspaceDirectory, Runner = runners.Resolve(Sandbox.SandboxKinds.Local), TeamId = request.TeamId, ProducerModel = request.ProducerModel };

        return await grader.GradeAsync(context, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record BenchmarkTaskGradingRequest
{
    public required BenchmarkTask Task { get; init; }
    public required string WorkspaceDirectory { get; init; }
    public Guid? TeamId { get; init; }
    public ReviewModelIdentity? ProducerModel { get; init; }
}
