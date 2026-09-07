using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>Production executor/container, real PostgreSQL, shell and artifact bytes. Only the agent harness is scripted; live Claude/Codex stop-hook qualification remains separate.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LocalAcceptanceExecutorFlowTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task A_repository_free_agent_is_graded_in_its_actual_workspace(bool explicitDirectory, bool accepted)
    {
        var directory = NewDirectory();
        try
        {
            var task = TaskFor(explicitDirectory ? directory : null) with { Acceptance = new SupervisorAcceptanceSpec { Command = ["/bin/sh", "-c", "test \"$1\" = \"\" && test \"$2\" = \"  \" && test \"$(cat report.txt)\" = accepted", "oracle", "", "  "] } };
            var harness = new ShellHarness(accepted ? "printf accepted > report.txt; echo produced" : "printf wrong > report.txt; echo produced");
            var (runId, teamId) = await ExecuteAsync(task, harness);
            using var scope = fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            run.Status.ShouldBe(accepted ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, result.Error);
            result.AcceptancePassed.ShouldBe(accepted);
            result.AcceptanceDetail.ShouldBe(accepted ? "tests-passed" : "tests-failed-exit-1");
            result.AcceptanceFailureClass.ShouldBe(accepted ? null : GradeFailureClass.Genuine);
            result.AcceptanceEvidenceId.ShouldNotBeNull();
            (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, result.AcceptanceEvidenceId.Value, CancellationToken.None)).ShouldNotBeNull();
            result.ProducedBranch.ShouldBeNull();
            harness.Directory.ShouldNotBeNull();
            if (explicitDirectory)
            {
                Directory.Exists(directory).ShouldBeTrue("an explicit directory is borrowed, never disposed as owned scratch");
                result.UndeclaredArtifactCount.ShouldBe(0, "an explicit directory must not silently enable a recursive artifact walk");
            }
            else Directory.Exists(harness.Directory).ShouldBeFalse("the executor still owns scratch cleanup");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("empty", GradeFailureClass.SpecIncomplete)]
    [InlineData("pathspec", GradeFailureClass.SpecIncomplete)]
    [InlineData("missing-oracle", GradeFailureClass.Environment)]
    public async Task A_known_unverifiable_contract_fails_typed_before_any_native_launch(string invalid, GradeFailureClass failureClass)
    {
        var directory = NewDirectory();
        try
        {
            var spec = new SupervisorAcceptanceSpec { Command = invalid == "empty" ? [] : ["/bin/sh", "-c", "exit 0"], ProtectedPaths = invalid == "pathspec" ? ["*.sh"] : null, OraclePaths = invalid == "missing-oracle" ? ["absent.sh"] : null };
            var harness = new ShellHarness("touch native-started; echo produced");
            var (runId, teamId) = await ExecuteAsync(TaskFor(directory) with { Acceptance = spec, MaxReviseRounds = 1 }, harness);
            using var scope = fixture.BeginScope();
            var runs = scope.Resolve<IAgentRunService>();
            var run = await runs.GetAsync(runId, CancellationToken.None);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            run.Status.ShouldBe(AgentRunStatus.Failed);
            result.AcceptancePassed.ShouldBe(false);
            result.AcceptanceFailureClass.ShouldBe(failureClass);
            result.ReviseRounds.ShouldBe(0);
            File.Exists(Path.Combine(directory, "native-started")).ShouldBeFalse("a known preparation failure must never bill a CLI invocation");
            (await runs.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).ShouldBeEmpty();
            run.RunnerHandleJson.ShouldBeNull();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task A_rewritten_explicit_judge_cannot_buy_a_pass_or_a_revise_round()
    {
        var directory = NewDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "judge.sh"), "exit 7\n");
            var task = TaskFor(directory) with { Acceptance = new SupervisorAcceptanceSpec { Command = ["/bin/sh", "judge.sh"], OraclePaths = ["judge.sh"] }, MaxReviseRounds = 1 };
            var harness = new ShellHarness("printf 'exit 0\\n' > judge.sh; echo produced");
            var (runId, _) = await ExecuteAsync(task, harness);
            using var scope = fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            run.Status.ShouldBe(AgentRunStatus.Failed);
            result.AcceptanceDetail.ShouldBe("grade-error: oracle-integrity-changed");
            result.AcceptanceFailureClass.ShouldBe(GradeFailureClass.GraderFault);
            result.AcceptanceEvidenceId.ShouldBeNull("the changed oracle was never run");
            harness.Launches.ShouldBe(1);
            (await File.ReadAllTextAsync(Path.Combine(directory, "judge.sh"))).ShouldBe("exit 0\n");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Explicit_file_obligations_capture_exact_bytes_without_sweeping_unrelated_files()
    {
        var directory = NewDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.txt"), "not declared");
            var task = TaskFor(directory) with { Acceptance = new SupervisorAcceptanceSpec { Kind = BenchmarkGradingKind.ArtifactPresent, Command = ["report.txt"] } };
            var (runId, teamId) = await ExecuteAsync(task, new ShellHarness("printf accepted > report.txt; echo produced"));
            using var scope = fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.AcceptancePassed.ShouldBe(true);
            result.CapturedArtifactCount.ShouldBe(1);
            result.UndeclaredArtifactCount.ShouldBe(0);
            var rows = await scope.Resolve<IArtifactManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None);
            rows.ShouldHaveSingleItem().LogicalPath.ShouldBe("report.txt");
            var bytes = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, rows[0].ContentArtifactId, CancellationToken.None);
            System.Text.Encoding.UTF8.GetString(bytes.ShouldNotBeNull().Bytes).ShouldBe("accepted");
            File.Exists(Path.Combine(directory, "unrelated.txt")).ShouldBeTrue();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_directory_or_changed_post_capture_bytes_cannot_satisfy_a_file_delivery(bool changedBySetup)
    {
        var directory = NewDirectory();
        try
        {
            var spec = new SupervisorAcceptanceSpec { Kind = BenchmarkGradingKind.ArtifactPresent, Command = ["report.txt"], SetupCommand = changedBySetup ? ["/bin/sh", "-c", "printf changed > report.txt"] : null };
            var script = changedBySetup ? "printf original > report.txt; echo produced" : "mkdir report.txt; echo produced";
            var (runId, _) = await ExecuteAsync(TaskFor(directory) with { Acceptance = spec, MaxReviseRounds = 1 }, new ShellHarness(script));
            using var scope = fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            run.Status.ShouldBe(AgentRunStatus.Failed);
            result.AcceptancePassed.ShouldBe(false);
            result.AcceptanceFailureClass.ShouldBe(GradeFailureClass.GraderFault);
            result.AcceptanceDetail.ShouldStartWith("grade-error: declared-deliverable-");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Distinct_logical_paths_can_share_content_and_exact_duplicate_obligations_coalesce()
    {
        var directory = NewDirectory();
        try
        {
            var task = TaskFor(directory) with { Acceptance = new SupervisorAcceptanceSpec { Kind = BenchmarkGradingKind.ArtifactPresent, Command = ["a.txt", "b.txt", "a.txt"] } };
            var (runId, teamId) = await ExecuteAsync(task, new ShellHarness("printf same > a.txt; printf same > b.txt; echo produced"));
            using var scope = fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            run.Status.ShouldBe(AgentRunStatus.Succeeded, result.Error);
            result.AcceptancePassed.ShouldBe(true);
            result.CapturedArtifactCount.ShouldBe(2);
            var rows = await scope.Resolve<IArtifactManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None);
            rows.Select(row => row.LogicalPath).Order().ShouldBe(new[] { "a.txt", "b.txt" });
            rows.Select(row => row.ContentArtifactId).Distinct().ShouldHaveSingleItem("content identity is independent of logical obligation identity");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private async Task<(Guid RunId, Guid TeamId)> ExecuteAsync(AgentTask task, ShellHarness harness)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        Guid runId;
        using (var admission = fixture.BeginScopeAs(userId, teamId))
            runId = (await admission.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, cancellationToken: CancellationToken.None)).Id;
        using var execution = fixture.BeginScope(builder => builder.RegisterInstance(new AgentHarnessRegistry([harness])).As<IAgentHarnessRegistry>());
        await execution.Resolve<AgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);
        return (runId, teamId);
    }

    private static AgentTask TaskFor(string? directory) => new() { Goal = "produce verifiable local work", Harness = "local-acceptance-test", Model = "test-model", WorkspaceDirectory = directory, Autonomy = AgentAutonomyLevel.Trusted, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted), MaxReviseRounds = 0, TimeoutSeconds = 30 };
    private static string NewDirectory() { var directory = Path.Combine(Path.GetTempPath(), "cs-local-executor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }

    private sealed class ShellHarness(string script) : IAgentHarness
    {
        public string Kind => "local-acceptance-test";
        public string Version => "test";
        public IReadOnlyList<string> Models => ["test-model"];
        public string? Directory { get; private set; }
        public int Launches { get; private set; }
        public SandboxSpec BuildInvocation(AgentTask task) { Directory = task.WorkspaceDirectory; Launches++; return new() { Command = "/bin/sh", Args = ["-c", script], WorkingDirectory = Directory, TimeoutSeconds = 30 }; }
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => [new() { Kind = AgentEventKind.AssistantMessage, Text = rawLine }];
        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) => new() { Status = exitCode == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, ExitReason = "script-finished", Summary = fold.LastText });
    }
}
