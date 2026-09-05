using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using CodeSpace.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit — C1: a terminal STOP with no reviewable head is no longer ungraded.
///
/// <para>The defect: <c>ApplyStopAcceptanceGradeAsync</c> skipped the whole grade whenever the run published no
/// branch, so an analysis-only / un-integrated run terminalized reading "Completed" with its model-authored oracle
/// never run. A deliverable-kind oracle does not need a head — the answer is in the deliverables the units captured,
/// or, failing that, in the stop summary itself. Only a <c>TestsPass</c> gate keeps skipping (its Command is an argv
/// presupposing a code world; running it over a directory of documents is a category error).</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class SupervisorBranchlessStopGradeTests
{
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid UnitId = Guid.NewGuid();

    [Fact]
    public async Task A_deliverable_kind_stop_with_no_head_grades_the_captured_deliverables()
    {
        var grader = new CapturingGrader(new BenchmarkGrade { Passed = true, Detail = "artifact-present" });

        var outcome = await GradeAsync(grader, StopWith(BenchmarkGradingKind.ArtifactPresent));

        grader.CapturedCalls.ShouldBe(new List<Guid> { UnitId }, customMessage: "the run's own unit is the world the branchless gate grades against");
        SupervisorOutcome.ReadAcceptanceGradePassed(outcome).ShouldBe(true, "an accepted deliverable oracle is recorded like the repo path's");
    }

    [Fact]
    public async Task A_failing_deliverable_kind_stop_records_the_failure_named_by_its_gate()
    {
        var grader = new CapturingGrader(new BenchmarkGrade { Passed = false, Detail = "missing: REPORT.md" });

        var outcome = await GradeAsync(grader, StopWith(BenchmarkGradingKind.ArtifactPresent));

        SupervisorOutcome.ReadAcceptanceGradePassed(outcome).ShouldBe(false, "a branchless run whose deliverable never appeared is NOT a clean Completed");
        outcome.ShouldContain("model-check", customMessage: "the detail names the gate that failed, exactly as the repo path does");
        outcome.ShouldContain("missing: REPORT.md");
    }

    [Fact]
    public async Task A_tests_pass_stop_with_no_head_stays_skipped()
    {
        var grader = new CapturingGrader(new BenchmarkGrade { Passed = false, Detail = "unused" });

        var outcome = await GradeAsync(grader, StopWith(kind: null));   // absent kind ⇒ TestsPass

        grader.CapturedCalls.ShouldBeEmpty("running a test argv over a directory of captured documents is a CATEGORY error, not a verdict");
        SupervisorOutcome.ReadAcceptanceGradePassed(outcome).ShouldBeNull("today's skip stands — the outcome is untouched");
    }

    [Fact]
    public async Task An_llm_judge_stop_with_nothing_captured_judges_the_stop_summary()
    {
        var grader = new CapturingGrader(new BenchmarkGrade { Passed = false, Detail = ISupervisorAcceptanceGrader.NoDeliverablesCaptured });
        var judge = new StubRubricJudge(met: true);

        var outcome = await GradeAsync(grader, StopWith(BenchmarkGradingKind.LlmJudge, WithRubric), judge);

        judge.JudgedArtifact.ShouldBe("The two languages differ mainly in their memory model.", customMessage: "an answer that never touched disk is still an answer — the judge reads the stop summary");
        judge.JudgedGoal.ShouldBe("compare the two languages", customMessage: "the run's goal frames the rubric, as it does for a file-backed judge");
        SupervisorOutcome.ReadAcceptanceGradePassed(outcome).ShouldBe(true);
        outcome.ShouldContain("judged the stop summary", customMessage: "the detail says out loud WHAT was judged — a summary grade must never be mistaken for a deliverable grade");
    }

    [Fact]
    public async Task An_llm_judge_stop_whose_summary_misses_the_rubric_fails_closed()
    {
        var grader = new CapturingGrader(new BenchmarkGrade { Passed = false, Detail = ISupervisorAcceptanceGrader.NoDeliverablesCaptured });

        var outcome = await GradeAsync(grader, StopWith(BenchmarkGradingKind.LlmJudge, WithRubric), new StubRubricJudge(met: false));

        SupervisorOutcome.ReadAcceptanceGradePassed(outcome).ShouldBe(false, "the summary is graded, not merely read — an unmet rubric is a real failure");
    }

    [Fact]
    public async Task A_captured_deliverable_that_failed_is_never_second_guessed_by_the_summary()
    {
        // The summary fallback exists ONLY for a world with no file at all. A unit that DID capture something and
        // failed the oracle has a real verdict; re-judging its prose would let a confident summary overturn the file.
        var grader = new CapturingGrader(new BenchmarkGrade { Passed = false, Detail = "rubric 0.33 < 1.00 — not met: [sources]" });
        var judge = new StubRubricJudge(met: true);

        var outcome = await GradeAsync(grader, StopWith(BenchmarkGradingKind.LlmJudge, WithRubric), judge);

        judge.Called.ShouldBeFalse("a captured world produced the verdict — the prose fallback must not overturn it");
        SupervisorOutcome.ReadAcceptanceGradePassed(outcome).ShouldBe(false);
    }

    [Fact]
    public async Task An_llm_judge_stop_with_no_registered_judge_fails_closed()
    {
        var grader = new CapturingGrader(new BenchmarkGrade { Passed = false, Detail = ISupervisorAcceptanceGrader.NoDeliverablesCaptured });

        var outcome = await GradeAsync(grader, StopWith(BenchmarkGradingKind.LlmJudge, WithRubric), rubricJudge: null);

        SupervisorOutcome.ReadAcceptanceGradePassed(outcome).ShouldBe(false, "a configured oracle we cannot RUN is never a silent pass");
        outcome.ShouldContain("no rubric judge");
    }

    [Fact]
    public async Task A_grader_escape_on_the_branchless_path_records_not_accepted()
    {
        var grader = new CapturingGrader(new InvalidOperationException("the artifact store is unreachable"));

        var outcome = await GradeAsync(grader, StopWith(BenchmarkGradingKind.ArtifactPresent));

        SupervisorOutcome.ReadAcceptanceGradePassed(outcome).ShouldBe(false, "an unexpected grader escape can never strand the terminal row");
        outcome.ShouldContain("grade-error");
    }

    // ── fixture ──

    private static readonly AcceptanceRubric WithRubric = new() { Criteria = new[] { new AcceptanceRubricCriterion { Id = "sources", Requirement = "names at least one source" } } };

    /// <summary>Drive the stop grade directly with a branchless context (the fake resolver finds no published branch when the tape carries none).</summary>
    private static async Task<string> GradeAsync(CapturingGrader grader, string stopPayloadJson, StubRubricJudge? rubricJudge = null)
    {
        // Only the stop-grade path's own collaborators are real here: the grader under test, the branch resolver (the
        // source of the branchless world), the manifest store the oracle anchor reads, and the budget ledger the call
        // scope carries. Every other seam is untouched by ApplyStopAcceptanceGradeAsync.
        var service = new SupervisorTurnService(null!, null!, null!, db: Infrastructure.EmptyTestDb.New(), grader, null!, null!, null!, null!,
            null!, null!, new NoManifests(), new FakeSupervisorPublishedBranchResolver(), null!, new AdmitAllBudgetLedger(),
            null!, NullLogger<SupervisorTurnService>.Instance, rubricJudge);

        var context = new SupervisorTurnContext
        {
            SupervisorRunId = Guid.NewGuid(),
            TeamId = TeamId,
            NodeId = "sup",
            Goal = "compare the two languages",
            PriorDecisions = new[] { SpawnDecisionWithUnit() },
        };

        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = stopPayloadJson };
        var execution = await service.ApplyStopAcceptanceGradeAsync(SupervisorExecution.Synchronous("{}"), context, decision, TeamId, CancellationToken.None);

        return execution.OutcomeJson ?? "";
    }

    /// <summary>A spawn whose outcome folded ONE agent result — the unit whose captured world the branchless gate grades.</summary>
    private static SupervisorPriorDecision SpawnDecisionWithUnit() => new()
    {
        Id = Guid.NewGuid(),
        Sequence = 1,
        Status = SupervisorDecisionStatus.Succeeded,
        DecisionKind = SupervisorDecisionKinds.Spawn,
        PayloadJson = """{"subtaskIds":["s1"]}""",
        OutcomeJson = JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = UnitId, status = "Succeeded" } } }),
    };

    private static string StopWith(BenchmarkGradingKind? kind, AcceptanceRubric? rubric = null) => JsonSerializer.Serialize(new SupervisorStopPayload
    {
        Outcome = "completed",
        Summary = "The two languages differ mainly in their memory model.",
        Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "REPORT.md" }, Kind = kind, Rubric = rubric },
    }, AgentJson.Options);

    /// <summary>The oracle-anchor read: this run published nothing, so it has no manifest rows.</summary>
    private sealed class NoManifests : IPublishManifestStore
    {
        public Task<IReadOnlyList<CodeSpace.Core.Persistence.Entities.PublishManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CodeSpace.Core.Persistence.Entities.PublishManifest>>(Array.Empty<CodeSpace.Core.Persistence.Entities.PublishManifest>());

        public Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, long expectedFenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertForIntegrationAsync(PublishManifestUpsert input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StampAcceptanceForAgentRunAsync(Guid agentRunId, PublishAcceptanceState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeSpace.Core.Persistence.Entities.PublishManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CodeSpace.Core.Persistence.Entities.PublishManifest>>> ListForAgentRunsAsync(IReadOnlyCollection<Guid> agentRunIds, Guid teamId, int maxAgentRunIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CodeSpace.Core.Persistence.Entities.PublishManifest>>> ListForWorkflowRunsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Records every captured-world grade the branchless path asks for and answers a fixed verdict (or throws).</summary>
    private sealed class CapturingGrader : ISupervisorAcceptanceGrader
    {
        private readonly BenchmarkGrade _grade;
        private readonly Exception? _throw;

        public CapturingGrader(BenchmarkGrade grade) { _grade = grade; }
        public CapturingGrader(Exception toThrow) { _throw = toThrow; _grade = new BenchmarkGrade { Passed = false, Detail = "unused" }; }

        public List<Guid> CapturedCalls { get; } = new();

        public Task<BenchmarkGrade> GradeCapturedAsync(Guid agentRunId, Guid teamId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            CapturedCalls.Add(agentRunId);
            if (_throw != null) throw _throw;
            return Task.FromResult(_grade);
        }

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Answers every rubric criterion the same way and records what it was asked to read.</summary>
    private sealed class StubRubricJudge : CodeSpace.Core.Services.Review.IRubricJudge
    {
        private readonly bool _met;
        public StubRubricJudge(bool met) { _met = met; }

        public bool Called { get; private set; }
        public string? JudgedArtifact { get; private set; }
        public string? JudgedGoal { get; private set; }

        public Task<RubricJudgeVerdict> JudgeAsync(AcceptanceRubric rubric, string artifact, string? goal, Guid teamId, CancellationToken cancellationToken)
        {
            Called = true;
            JudgedArtifact = artifact;
            JudgedGoal = goal;

            return Task.FromResult(new RubricJudgeVerdict { Criteria = rubric.Criteria.Select(c => new RubricCriterionVerdict { Id = c.Id, Met = _met }).ToList() });
        }
    }
}
