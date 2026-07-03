using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/>.<c>RehydrateFromDecisionLogAsync</c> with
/// a fake grader at the one acceptance seam): loopability slice 3 — the PER-UNIT objective acceptance fold. Each spawned
/// unit whose planned subtask authored an acceptance is graded on ITS OWN branch and the verdict folds onto that unit's
/// result. Proves: a FAILED unit folds rejected, is EXCLUDED from the no-progress evidence (the must-fix — proven through
/// the real FoldNoProgressDecisions), and renders the retry signal; a PASSED unit folds accepted; a unit with no contract
/// is byte-identical (never grades); the grade runs EXACTLY ONCE (replay reads the tape, never re-grades); a grader
/// failure fails closed; a multi-repo unit is deferred; and a repo-overridden unit is graded against ITS repo, not the
/// profile default. The grader's real clone+run fidelity is proven in <c>SupervisorAcceptanceFoldFlowTests</c> (same
/// grader); this pins the per-unit FOLD wiring (positional join, evidence discount, repo resolution) over real Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorUnitAcceptanceFoldFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorUnitAcceptanceFoldFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";
    private static readonly string[] Check = { "sh", "check.sh" };

    [Theory]
    [InlineData(true, true, 0)]    // a unit that PASSES its check → accepted, real evidence → streak resets to 0
    [InlineData(false, false, 2)]  // a unit that FAILS → rejected, NOT evidence → plan + spawn both count (the discount)
    public async Task A_unit_is_graded_on_its_branch_and_the_verdict_drives_evidence(bool gradePasses, bool expectedVerdict, int expectedNoProgress)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        var agentId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = gradePasses, Detail = gradePasses ? "tests-passed" : "tests-failed-exit-1" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(1, "the unit's acceptance command runs once against its own branch");
        grader.LastCall!.Value.RepositoryId.ShouldBe(repoId);
        grader.LastCall.Value.Branch.ShouldBe("codespace/agent/s1", "the unit is graded against the branch IT produced");
        grader.LastCall.Value.Command.ShouldBe(Check, "the subtask's authored acceptance command is the graded argv");
        grader.LastCall.Value.Kind.ShouldBe(BenchmarkGradingKind.TestsPass, "a subtask with no authored oracle kind defaults to TestsPass");

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().AcceptancePassed.ShouldBe(expectedVerdict, "the per-unit verdict folds onto the agent result");

        ctx.NoProgressDecisions.ShouldBe(expectedNoProgress, "a rejected unit is discounted from the settled evidence — an acceptance-failing wave is no progress");

        SupervisorOutcome.ReadAgentResults(await LedgerSpawnOutcomeAsync(runId, teamId)).Single().AcceptancePassed
            .ShouldBe(expectedVerdict, "the verdict is PERSISTED on the durable spawn row (replay reads it, never re-grades)");
    }

    [Fact]
    public async Task A_subtask_authored_artifact_present_kind_is_forwarded_to_the_per_unit_grade()
    {
        // T1.1: a NON-coding subtask authors Kind=ArtifactPresent (its deliverable is a FILE, not a passing test) — the
        // per-unit fold must thread THAT oracle to the grade, not the default TestsPass. The stop-path kind-forwarding is
        // pinned in SupervisorAcceptanceFoldFlowTests; this pins the per-unit (per-subtask) path, so regressing the fold to
        // a hardcoded TestsPass is caught.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, ArtifactPlanPayload("s1", new[] { "docs/report.md" }));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "artifacts-present" });
        await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(1, "the unit's deliverable check runs once against its own branch");
        grader.LastCall!.Value.Command.ShouldBe(new[] { "docs/report.md" }, "the subtask's declared deliverable paths are the graded command");
        grader.LastCall.Value.Kind.ShouldBe(BenchmarkGradingKind.ArtifactPresent, "the per-unit fold forwards the subtask's authored oracle kind — not the default TestsPass");
    }

    [Fact]
    public async Task A_unit_without_a_contract_is_never_graded_and_is_byte_identical()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", null)));   // s1 carries NO acceptance
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));
        var before = await LedgerSpawnOutcomeAsync(runId, teamId);

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(0, "a subtask with no acceptance contract is never graded");
        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().AcceptancePassed.ShouldBeNull("no verdict is folded");
        (await LedgerSpawnOutcomeAsync(runId, teamId)).ShouldBe(before, "the durable spawn row is unchanged — no spurious UPDATE on the no-contract path");
    }

    [Fact]
    public async Task The_grade_runs_once_and_replay_reads_the_verdict_without_re_grading()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" }));
        var persistedAfterFirst = await LedgerSpawnOutcomeAsync(runId, teamId);

        // The SECOND rehydrate must NOT re-grade — the once-guard reads the folded verdict off the tape.
        var secondGrader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "WOULD-FLIP-IF-RE-GRADED" });
        var ctx2 = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), secondGrader);

        secondGrader.CallCount.ShouldBe(0, "replay reads the durable verdict — the grade I/O never re-runs (replay-deterministic)");
        ctx2.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson.ShouldBe(persistedAfterFirst, "the verdict is unchanged across replay");
        (await LedgerSpawnOutcomeAsync(runId, teamId)).ShouldBe(persistedAfterFirst, "no redundant UPDATE on replay");
    }

    [Fact]
    public async Task A_grader_failure_fails_closed_without_stranding_the_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), new RecordingGrader(new InvalidOperationException("sandbox exploded")));

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().AcceptancePassed.ShouldBe(false, "an unexpected grade failure folds not-accepted (fail-closed)");
    }

    [Fact]
    public async Task A_unit_with_no_branch_fails_closed_and_is_not_evidence()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        // The agent succeeded but pushed NO branch — there is nothing to grade against → fail-closed, not evidence.
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), producedBranch: null)));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(0, "no branch to clone → the grader is never invoked");
        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .Single().AcceptancePassed.ShouldBe(false, "a contract-bearing unit with no gradeable branch fails closed");
    }

    [Fact]
    public async Task A_multi_repo_unit_is_deferred_and_falls_back_to_no_verdict()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        // A unit whose agent ran MULTI-repo (RepositoryResults present): its top-level branch mirrors only the primary,
        // so the per-agent multi-repo grade is deferred (mirroring the resolve fold) rather than a primary-only false verdict.
        var multiRepoUnit = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "primary",
            RepositoryResults = new[] { new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), ProducedBranch = "web/x", BaseBranch = "main", Access = WorkspaceAccess.Write } },
        };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(multiRepoUnit));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(0, "a multi-repo unit is not graded by the per-unit fold (per-repo grade deferred)");
        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .Single().AcceptancePassed.ShouldBeNull("no verdict is folded onto a multi-repo unit");
    }

    [Fact]
    public async Task A_repo_overridden_unit_is_graded_against_its_own_repo_not_the_profile_default()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var profileRepo = Guid.NewGuid();
        var overrideRepo = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        // The model authored a per-agent dispatch overriding s1's repo — the unit must be graded against THAT repo (its
        // agent wrote there), not the profile default (which would be a false verdict).
        var spawnPayload = $$"""{"subtaskIds":["s1"],"agents":[{"subtaskId":"s1","repositoryId":"{{overrideRepo}}"}]}""";
        await SeedSpawnAsync(runId, teamId, sequence: 2, spawnPayload, SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        await RehydrateAsync(runId, teamId, GoalConfig(profileRepo), grader);

        grader.CallCount.ShouldBe(1);
        grader.LastCall!.Value.RepositoryId.ShouldBe(overrideRepo, "the repo-overridden unit is graded against the repo its agent actually wrote to");
    }

    [Fact]
    public async Task Only_the_contract_bearing_unit_in_a_mixed_wave_is_graded()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        // s1 carries a contract, s2 does not — only s1 is graded; the positional join maps results[0]↔s1, results[1]↔s2.
        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check), ("s2", null)));
        var s1Agent = Guid.NewGuid();
        var s2Agent = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1","s2"]}""",
            SpawnOutcome(Unit(s1Agent, "codespace/agent/s1"), Unit(s2Agent, "codespace/agent/s2")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(1, "only the contract-bearing unit is graded");
        grader.LastCall!.Value.Branch.ShouldBe("codespace/agent/s1", "the positional join grades s1's own branch");

        var results = SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson);
        results[0].AcceptancePassed.ShouldBe(true, "s1 (contract) folds a verdict");
        results[1].AcceptancePassed.ShouldBeNull("s2 (no contract) stays ungraded");
    }

    // ─── Helpers ───

    private static string PlanPayload(params (string Id, string[]? Command)[] subtasks)
    {
        var ordered = subtasks.Select(s => s.Command is null
            ? (object)new { id = s.Id, title = s.Id, instruction = "do " + s.Id }
            : new { id = s.Id, title = s.Id, instruction = "do " + s.Id, acceptance = new { command = s.Command } });

        return JsonSerializer.Serialize(new { goal = Goal, subtasks = ordered }, AgentJson.Options);
    }

    /// <summary>A plan whose single subtask authors a NON-coding acceptance — Kind=ArtifactPresent + the declared deliverable paths in the command slot.</summary>
    private static string ArtifactPlanPayload(string id, string[] paths) =>
        JsonSerializer.Serialize(new { goal = Goal, subtasks = new[] { new { id, title = id, instruction = "do " + id, acceptance = new { command = paths, kind = "ArtifactPresent" } } } }, AgentJson.Options);

    private static SupervisorAgentResult Unit(Guid agentRunId, string? producedBranch) =>
        new() { AgentRunId = agentRunId, Status = "Succeeded", Summary = "did it", ProducedBranch = producedBranch };

    private static string SpawnOutcome(params SupervisorAgentResult[] units) =>
        JsonSerializer.Serialize(new { agentRunIds = units.Select(u => u.AgentRunId).ToArray(), agentCount = units.Length, agentResults = units }, AgentJson.Options);

    private async Task<SupervisorTurnContext> RehydrateAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig, RecordingGrader grader)
    {
        using var scope = _fixture.BeginScope();
        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            scope.Resolve<ISupervisorDecider>(),
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            grader,
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<ILogger<SupervisorTurnService>>());

        return await service.RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig, CancellationToken.None);
    }

    private async Task SeedPlanAsync(Guid runId, Guid teamId, int sequence, string payloadJson) =>
        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.Plan, payloadJson, "{}");

    private async Task SeedSpawnAsync(Guid runId, Guid teamId, int sequence, string payloadJson, string outcomeJson) =>
        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.Spawn, payloadJson, outcomeJson);

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, int sequence, string kind, string payloadJson, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = kind, IdempotencyKey = $"{kind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> LedgerSpawnOutcomeAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Spawn)
            .Select(d => d.OutcomeJson)
            .SingleAsync();
    }

    private static SupervisorGoalConfig GoalConfig(Guid repoId) => new()
    {
        Goal = Goal,
        AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId },
    };

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-unit-accept-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Records each grade call (count + args) and returns a canned grade — or throws (the unexpected-failure path). The one seam the per-unit fold grades at, faked so the test asserts call count + the exact (repo, branch, command) without real git.</summary>
    private sealed class RecordingGrader : ISupervisorAcceptanceGrader
    {
        private readonly BenchmarkGrade _grade;
        private readonly Exception? _throw;

        public RecordingGrader(BenchmarkGrade grade) => _grade = grade;
        public RecordingGrader(Exception toThrow) { _throw = toThrow; _grade = new BenchmarkGrade { Passed = false, Detail = "unused" }; }

        public int CallCount { get; private set; }
        public (Guid RepositoryId, Guid TeamId, string Branch, IReadOnlyList<string> Command, int TimeoutSeconds, BenchmarkGradingKind Kind)? LastCall { get; private set; }

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            CallCount++;
            LastCall = (repositoryId, teamId, branch, spec.Command, timeoutSeconds, spec.Kind ?? BenchmarkGradingKind.TestsPass);
            if (_throw != null) throw _throw;
            return Task.FromResult(_grade);
        }
    }
}
