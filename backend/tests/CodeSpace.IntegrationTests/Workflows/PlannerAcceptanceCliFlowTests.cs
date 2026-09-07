using System.Text.Json;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>Component proof: typed model-shaped input reaches the actual CLI and filesystem oracles. No model is invoked and this does not enable the executor's repository-free TestsPass lane.</summary>
[Trait("Category", "Integration")]
public sealed class PlannerAcceptanceCliFlowTests
{
    [Fact]
    public async Task Typed_argv_and_file_paths_reach_independent_real_oracles_without_changing_the_final_check()
    {
        File.Exists("/bin/sh").ShouldBeTrue("the real CLI oracle is a required dependency, never a skipped assertion");
        var directory = Path.Combine(Path.GetTempPath(), $"cs-planner-oracle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var reportPath = Path.Combine(directory, "report.txt");
            var dataPath = Path.Combine(directory, "data.json");
            await File.WriteAllTextAsync(reportPath, "accepted");
            await File.WriteAllTextAsync(dataPath, "{\"status\":\"accepted\"}");
            var argv = new[] { "/bin/sh", "-c", "test \"$1\" = \"\" && test \"$2\" = \"  \" && test \"$(cat report.txt)\" = \"accepted\"", "argv-proof", "", "  " };
            var json = JsonSerializer.SerializeToElement(new
            {
                goal = "verify the report", subtasks = new object[]
                {
                    new { id = "behavior", title = "Verify", instruction = "verify exact content", kind = "research", acceptance = new { formatVersion = 2, kind = "TestsPass", argv } },
                    // P2.6: a bare ArtifactPresent from the planner is self-certifying and dropped — declared +
                    // paired with a schema companion promotes it to ArtifactSchema instead, so the REAL registry
                    // dispatch (never a directly-constructed grader) must land on ArtifactSchemaGrader — which,
                    // unlike ArtifactPresentGrader, also parses the file as JSON, hence the valid-JSON fixture.
                    new { id = "file", title = "Presence", instruction = "require the file", kind = "code", acceptance = new { formatVersion = 2, kind = "ArtifactPresent", artifactPaths = new[] { "data.json" }, schema = new { } } },
                },
            });
            var plan = LlmWorkflowPlanner.Deserialize(json, declaredDeliverablePaths: new[] { "data.json" });
            plan.DroppedAcceptances.ShouldBeNull("the file obligation is declared and paired — nothing here should be dropped");
            plan.Subtasks[1].Acceptance!.Kind.ShouldBe(BenchmarkGradingKind.ArtifactSchema, "an empty schema companion promotes ArtifactPresent to ArtifactSchema, never leaves it bare");
            plan.Subtasks[1].Acceptance!.Command.ShouldBe(new[] { "data.json" }, "the promoted spec still names the same deliverable path");
            var runner = new LocalProcessRunner();
            var registry = new BenchmarkGraderRegistry(new IBenchmarkGrader[] { new TestsPassGrader(), new ArtifactPresentGrader(), new ArtifactSchemaGrader() });
            var commandContext = BenchmarkGradingContext.ForAcceptance(plan.Subtasks[0].Acceptance!, Guid.NewGuid(), 15, directory, runner);
            var fileContext = BenchmarkGradingContext.ForAcceptance(plan.Subtasks[1].Acceptance!, Guid.NewGuid(), 15, directory, runner);
            commandContext.Task.TestCommand.ShouldBe(argv);
            var commandGrader = registry.Resolve(plan.Subtasks[0].Acceptance!.Kind!.Value);
            var fileGrader = registry.Resolve(plan.Subtasks[1].Acceptance!.Kind!.Value);
            (await commandGrader.GradeAsync(commandContext, CancellationToken.None)).Passed.ShouldBeTrue();
            (await fileGrader.GradeAsync(fileContext, CancellationToken.None)).Passed.ShouldBeTrue();

            await File.WriteAllTextAsync(reportPath, "incorrect");
            var rejected = await commandGrader.GradeAsync(commandContext, CancellationToken.None);
            rejected.Passed.ShouldBeFalse("file existence must never substitute for the model-authored content check");
            rejected.Class.ShouldBe(GradeFailureClass.Genuine);
            rejected.EvidenceText.ShouldContain("exit=1");
            (await fileGrader.GradeAsync(fileContext, CancellationToken.None)).Passed.ShouldBeTrue("the separately declared presence oracle has a narrower obligation");
            File.Delete(dataPath);
            (await fileGrader.GradeAsync(fileContext, CancellationToken.None)).Passed.ShouldBeFalse();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
