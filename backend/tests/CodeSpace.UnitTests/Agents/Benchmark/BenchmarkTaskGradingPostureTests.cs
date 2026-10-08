using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// 🟢 Unit: a benchmark or qualification grade runs the fixture's test command in the workspace the benchmark agent
/// just wrote, so a module the tests import is the agent's code. It runs under the producing agent's posture, as every
/// acceptance grade does, instead of the raw local runner with no memory or CPU ceiling. Driven through the real
/// <see cref="BenchmarkTaskGrading"/> seam both benchmark instruments grade through, with the real
/// <see cref="TestsPassGrader"/>, recording the spec the runner receives.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BenchmarkTaskGradingPostureTests
{
    [Theory]
    [InlineData(AgentAutonomyLevel.Confined)]
    [InlineData(AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted)]
    public async Task The_benchmark_check_runs_under_the_producing_agents_ceilings(AgentAutonomyLevel tier)
    {
        var posture = AcceptanceGradingPosturePolicy.Derive(tier, AgentAutonomyPolicy.Derive(tier), AgentAutonomyLevel.Unleashed, hostMemoryBudgetMb: null);
        var runners = new RecordingRunners(SandboxStatus.Success);

        await GradeAsync(runners, posture);

        var check = runners.Specs.ShouldHaveSingleItem("fixture check: the test command ran once");
        check.MaxMemoryMb.ShouldBe(posture.MaxMemoryMb, "the check imports the agent's code, so it is capped like the agent was");
        check.MaxCpuPercent.ShouldBe(posture.MaxCpuPercent);
        check.AllowNetwork.ShouldBeFalse("the check never asked for the network — unchanged");
        posture.MaxMemoryMb.ShouldBeGreaterThan(0, "fixture check: a zero ceiling would mean unlimited and pass vacuously");
    }

    [Fact]
    public async Task A_benchmark_grade_with_no_producer_posture_runs_under_the_confined_ceilings()
    {
        var runners = new RecordingRunners(SandboxStatus.Success);

        await GradeAsync(runners, posture: null);

        var check = runners.Specs.ShouldHaveSingleItem();
        check.MaxMemoryMb.ShouldBe(AcceptanceGradingPosturePolicy.FailClosed.MaxMemoryMb, "a grade that cannot say whose work it runs gets the least anyone could have had");
        check.MaxCpuPercent.ShouldBe(AcceptanceGradingPosturePolicy.FailClosed.MaxCpuPercent);
    }

    [Fact]
    public async Task A_benchmark_check_killed_at_its_ceiling_is_an_environment_fact()
    {
        var grade = await GradeAsync(new RecordingRunners(SandboxStatus.ResourceExhausted), AcceptanceGradingPosturePolicy.FailClosed);

        grade.Detail.ShouldBe(TestsPassGrader.ResourceExhaustedDetail);
        grade.Class.ShouldBe(GradeFailureClass.Environment, "the ceiling killed it, which says nothing about whether the agent solved the task");
    }

    /// <summary>Grade an empty cell workspace of this test's own: the grade copies the workspace into a grading tree, so it must never be handed a shared directory.</summary>
    private static async Task<BenchmarkGrade> GradeAsync(RecordingRunners runners, AcceptanceGradingPosture? posture)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "cs-bench-posture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            return await BenchmarkTaskGrading.GradeAsync(new BenchmarkGraderRegistry(new IBenchmarkGrader[] { new TestsPassGrader() }), runners, new BenchmarkTaskGradingRequest { Task = FixtureTask(), WorkspaceDirectory = workspace, Posture = posture, FixtureStager = null }, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static BenchmarkTask FixtureTask() => new()
    {
        Id = "posture",
        Description = "the check runs under the producing agent's posture",
        FixtureRef = "inline",
        Goal = "make the check pass",
        Grading = BenchmarkGradingKind.TestsPass,
        TestCommand = new[] { "sh", "check.sh" },
        Harness = "codex-cli",
        Modes = new[] { BenchmarkMode.HarnessCli },
        TimeoutSeconds = 30,
    };

    private sealed class RecordingRunners(SandboxStatus status) : ISandboxRunnerRegistry, ISandboxRunner
    {
        public List<SandboxSpec> Specs { get; } = new();
        public string Kind => SandboxKinds.Local;
        public IReadOnlyList<ISandboxRunner> All => new ISandboxRunner[] { this };
        public ISandboxRunner Resolve(string kind) => this;

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            return Task.FromResult(new SandboxResult { Status = status, ExitCode = status == SandboxStatus.Success ? 0 : 137, Stdout = "", Stderr = "" });
        }
    }
}
