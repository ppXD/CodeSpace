using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE whole-loop supervisor E2E — the join the coverage audit found was NEVER tested at any tier: a supervisor
/// driving REAL OS-process agents that PRODUCE A REAL PATCH, through a REAL git merge, gated by REAL objective
/// acceptance. Every prior test proves ONE half — <see cref="SupervisorRealAgentE2ETests"/> runs real agents but the
/// fake CLI writes no files (no patch), and <c>SupervisorMergeIntegrateFlowTests</c> integrates real git but SEEDS the
/// agent results. This test deletes both gaps: the 2 supervisor-spawned agents run through the production
/// <see cref="AgentRunExecutor"/> + real <c>LocalProcessRunner</c>, each EDITS A FILE in its cloned workspace
/// (<see cref="FileWritingFakeCli"/>), the executor's real git-diff captures each as a real
/// <see cref="AgentRunResult.Patch"/> + pushed branch, the supervisor's <c>merge</c> turn really integrates them into
/// one reviewable branch on the bare remote, and the terminal <c>stop</c> grades that integrated branch against a real
/// <c>check.sh</c> acceptance floor before declaring success.
///
/// <para><c>trigger.manual</c> → <c>agent.supervisor</c> (scripted plan→spawn→merge→stop) → terminal. Driven as ONE
/// <c>RunEngineAsync</c> + <c>WaitForPendingAsync</c> drain (the in-memory job client chains every self-advance,
/// executor dispatch, and barrier resume through one FIFO queue).</para>
///
/// <para><b>Fidelity (Rule 12) — HIGH.</b> Real engine, real Postgres, real <see cref="SupervisorTurnService"/> +
/// <see cref="RealSupervisorActionExecutor"/>, real <see cref="AgentRunExecutor"/> + real <c>LocalProcessRunner</c>
/// spawning a real OS process in a real cloned git workspace, real <c>LocalGitBranchIntegrator</c> against a bare
/// <c>file://</c> remote, real <c>SupervisorAcceptanceGrader</c> running <c>check.sh</c>. Two things are faked at honest
/// boundaries: the supervisor's DECISIONS (<see cref="ScriptedSupervisorDecider"/> — this slice is the deterministic
/// skeleton; the live-model brain is the follow-up) and the CLI's INTELLIGENCE (the fake codex writes a deterministic
/// file). A break in ANY seam — spawn → real agent edit → patch capture → merge integrate → acceptance grade → stop —
/// fails this test. POSIX-only (the fake CLI is <c>/bin/sh</c>); skipped on Windows / when git is absent.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class SupervisorWholeLoopE2ETests : IDisposable
{
    private const string NodeId = "sup";

    private readonly PostgresFixture _fixture;
    private readonly string? _integrateBefore;

    public SupervisorWholeLoopE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _integrateBefore = Environment.GetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar);
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, _integrateBefore);

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [Fact]
    public async Task Supervisor_drives_real_agents_to_a_real_patch_a_real_merge_and_a_passing_acceptance_gate()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/integrate

        using var cli = new FileWritingFakeCli();         // each spawned agent EDITS a distinct file in its workspace

        SetDecisionScript(s => s.PlanSpawnMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertRunReachedSuccessAsync(runId);
        await AssertBothAgentsProducedRealPatchesAsync(runId);
        await AssertDecisionLedgerAsync(runId, teamId, SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop);
        await AssertIntegratedBranchOnRemoteAsync(remote, runId);
        await AssertAcceptancePassedOnStopAsync(runId, teamId);
    }

    [Fact]
    public async Task Supervisor_recovers_a_failed_subtask_via_retry_to_a_passing_acceptance_gate()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/integrate/grade

        // The "do beta" subtask FAILS its first run (a real Failed agent, no patch); the supervisor RETRIES it with a
        // revised instruction the fake CLI succeeds on — a real failure→recovery through the real engine, not a replay.
        using var cli = new FailFirstThenSucceedFakeCli();

        SetDecisionScript(s => s.PlanSpawnRetryMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        // A failed agent is a SIGNAL the supervisor recovers from — the run still reaches Success via the retry.
        await AssertRunReachedSuccessAsync(runId);
        await AssertOneAgentFailedThenTheRetrySucceededAsync(runId);
        await AssertDecisionLedgerAsync(runId, teamId, SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop);

        // The merge integrated the recovered (retry) patch alongside alpha's + pushed a reviewable integration branch.
        // (Regression guard: the FAILED first attempt — which recorded a base but no patch — must NOT sink the merge.)
        var allBranches = await remote.ListBranchesAsync();
        allBranches.Any(b => b.Contains($"integration/{runId:N}")).ShouldBeTrue($"the merge must integrate the recovered work past the failed first attempt + push a reviewable branch; remote branches: [{string.Join(", ", allBranches)}]");

        await AssertAcceptancePassedOnStopAsync(runId, teamId);
    }

    [Fact]
    public async Task Supervisor_withholds_the_reviewable_branch_when_the_real_acceptance_floor_fails()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/integrate/grade

        using var cli = new FileWritingFakeCli();

        SetDecisionScript(s => s.PlanSpawnMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        // SAME happy-loop wiring — the ONLY change is the operator's acceptance floor REJECTS the integrated head
        // (check.sh exits 1). The whole real arc (spawn → real patch → real merge) still runs; the difference is the
        // objective gate must catch the broken head and WITHHOLD it.
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 1\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        // The agents still did real work and the merge still integrated — the gate is the LAST thing, not a short-circuit.
        await AssertRunReachedSuccessAsync(runId);
        await AssertBothAgentsProducedRealPatchesAsync(runId);
        await AssertIntegratedBranchOnRemoteAsync(remote, runId);

        // The objective floor FAILED against the real grader → the stop's grade is false, the node reports
        // AcceptanceFailed, and the reviewable branch is WITHHELD (a downstream git.open_pr binds "" → nothing). The
        // safety floor provably stops a broken head from ever reaching a PR — end to end through real git.
        await AssertAcceptanceFailedAndBranchWithheldAsync(runId, teamId);
    }

    [Fact]
    public async Task Supervisor_accepts_an_agent_solution_that_actually_solves_the_task_via_a_goal_relevance_oracle()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/integrate/grade

        // GOAL-RELEVANCE (解對任務, not just "drove the arc"): the agent edits solution.sh to the CORRECT A+B impl, and the
        // seeded check.sh is an OUTPUT-equality oracle (sh solution.sh 7 5 == 12), NOT a structural exit-0. A green grade
        // therefore means the agent's edit actually SOLVED the task — the deepest acceptance signal.
        using var cli = new SolutionWritingFakeCli(SolutionWritingFakeCli.CorrectSolution);

        SetDecisionScript(s => s.PlanSpawnSingleMergeStop());   // ONE agent edits solution.sh → clean single-branch merge

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        // Seed a WRONG solution.sh stub (echo 0) the agent must FIX + the goal-relevance oracle.
        await remote.SeedBaseAsync(new() { ["check.sh"] = SolutionWritingFakeCli.GoalRelevanceCheckSh, ["solution.sh"] = SolutionWritingFakeCli.SeededStub });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertRunReachedSuccessAsync(runId);
        await AssertAgentEditedSolutionAsync(runId, "$1 + $2");   // the agent wrote the CORRECT (adding) solution
        await AssertDecisionLedgerAsync(runId, teamId, SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop);
        await AssertIntegratedBranchOnRemoteAsync(remote, runId);
        await AssertAcceptancePassedOnStopAsync(runId, teamId);   // the oracle RAN the agent's solution → 12 → PASS
    }

    [Fact]
    public async Task Supervisor_withholds_an_agent_solution_that_integrates_but_computes_the_WRONG_answer()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        // The TEETH that distinguishes "解對任務" from "drove the arc": the agent DOES edit solution.sh + the merge DOES
        // integrate it (a real patch, a real head) — but to a plausible-but-WRONG impl (subtracts: 7 5 → 2 ≠ 12). A
        // STRUCTURAL check ("a file integrated") would green this; the goal-relevance oracle catches it and WITHHOLDS the head.
        using var cli = new SolutionWritingFakeCli(SolutionWritingFakeCli.WrongSolution);

        SetDecisionScript(s => s.PlanSpawnSingleMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = SolutionWritingFakeCli.GoalRelevanceCheckSh, ["solution.sh"] = SolutionWritingFakeCli.SeededStub });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertRunReachedSuccessAsync(runId);
        await AssertAgentEditedSolutionAsync(runId, "$1 - $2");       // the agent REALLY wrote the WRONG (subtracting) solution — not a no-op
        await AssertIntegratedBranchOnRemoteAsync(remote, runId);     // the merge REALLY integrated it
        await AssertAcceptanceFailedAndBranchWithheldAsync(runId, teamId);   // the oracle caught the wrong ANSWER → withheld
    }

    [Fact]
    public async Task Supervisor_gates_a_real_conflict_resolution_behind_the_irreversible_human_approval_floor()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/conflict

        // Both agents edit the SAME file with conflicting content → a REAL git merge conflict. The supervisor's resolve
        // attempt then hits the un-bypassable safety floor: re-merging a conflict is an IRREVERSIBLE side effect, so it
        // is NOT executed silently — it is gated behind a human APPROVAL card. Proves the conflict + the safety gate
        // end-to-end through the real engine. (The full approve→reconcile→accept loop is the follow-up slice.)
        using var cli = new ConflictThenResolveFakeCli();

        SetDecisionScript(s => s.PlanSpawnMergeResolveMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", [ConflictThenResolveFakeCli.SharedFile] = "base content\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertFirstMergeConflictedAsync(runId, teamId);
        await AssertResolveWasGatedToAnApprovalCardAsync(runId, teamId);
    }

    [Fact]
    public async Task Supervisor_reconciles_a_real_conflict_after_human_approval_to_a_passing_acceptance_gate()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/conflict/resolve

        // THE CROWN JEWEL — the full conflict-recovery loop through the real engine + real git, INCLUDING the human
        // approval of the irreversible re-merge: two agents conflict on one file → merge CONFLICTS → resolve parks for
        // human approval → [HUMAN APPROVES] → the resolver agent reconciles + verifies → the next merge accepts the
        // verified resolution → stop accepts the reconciled head.
        using var cli = new ConflictThenResolveFakeCli();

        SetDecisionScript(s => s.PlanSpawnMergeResolveApprovedMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var conversationId = await SeedConversationAsync(teamId, userId);   // the approval surface the resolve gate parks on

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", [ConflictThenResolveFakeCli.SharedFile] = "base content\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, conversationId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Drive to the resolve-approval park: plan → spawn(conflict) → merge(CONFLICTED) → resolve→ask_human (parks).
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        var token = await AssertParkedOnResolveApprovalAsync(runId, teamId);

        // The human APPROVES the irreversible re-merge → the supervisor re-emits resolve → it EXECUTES → the resolver
        // agent reconciles + RESOLUTION_VERIFIED → the next merge accepts the verified head → stop accepts it.
        await AnswerAsync(token, "approve", userId, teamId);
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertRunReachedSuccessAsync(runId);
        await AssertResolutionVerifiedAndAcceptedAsync(runId, teamId, remote);
    }

    [Fact]
    public async Task Supervisor_reconciles_a_multi_repo_conflict_after_human_approval_accepting_each_repos_head()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/per-repo conflict/resolve

        // THE MULTI-REPO CROWN JEWEL — the conflict-recovery loop across TWO repositories, each integrated/resolved/
        // accepted on its OWN axis. The two agents touch BOTH repos: they add DISJOINT files in the PRIMARY repo (→ it
        // integrates CLEANLY) and write conflicting content to one shared file in the RELATED repo (→ a REAL git conflict
        // on THAT axis only). The supervisor's per-repo resolve reconciles ONLY the conflicted repo after human approval;
        // the terminal stop grades + accepts EACH repo's reconciled head against ITS OWN remote's check.sh. Drives the
        // SAME PlanSpawnMergeResolveApprovedMergeStop script as the single-repo loop — the engine routes multi-repo purely
        // by the agents' data shape (per-repo RepositoryResults), so the decision sequence is identical.
        using var cli = new MultiRepoConflictFakeCli();

        SetDecisionScript(s => s.PlanSpawnMergeResolveApprovedMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var conversationId = await SeedConversationAsync(teamId, userId);

        // Two real bare remotes — the primary integrates cleanly, the related conflicts on its shared file (seeded so each
        // agent's edit is a real diff against a common base). Each carries its own check.sh acceptance floor.
        using var primaryRemote = new BareRemote();
        using var relatedRemote = new BareRemote();
        await primaryRemote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n" });
        await relatedRemote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", [MultiRepoConflictFakeCli.SharedFile] = "base content\n" });
        var primaryRepoId = await SeedBoundRepositoryAsync(teamId, primaryRemote.Url, "main");
        var relatedRepoId = await SeedBoundRepositoryAsync(teamId, relatedRemote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, primaryRepoId, conversationId, (relatedRepoId, MultiRepoConflictFakeCli.RelatedAlias));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Drive to the resolve-approval park: plan → spawn(both, per-repo conflict) → merge (related repo CONFLICTED,
        // primary CLEAN) → resolve→ask_human (parks).
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertOnlyTheRelatedRepoConflictedAsync(runId, teamId, relatedRepoId);
        var token = await AssertParkedOnResolveApprovalAsync(runId, teamId);

        // The human APPROVES → the per-repo resolver reconciles ONLY the conflicted repo + RESOLUTION_VERIFIED → the next
        // merge accepts the resolved repo's head + re-integrates the clean repo → stop grades BOTH heads.
        await AnswerAsync(token, "approve", userId, teamId);
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertRunReachedSuccessAsync(runId);
        await AssertEachRepoHeadAcceptedAndOnItsRemoteAsync(runId, teamId, new Dictionary<Guid, BareRemote> { [primaryRepoId] = primaryRemote, [relatedRepoId] = relatedRemote });
    }

    [Fact]
    public async Task Supervisor_withholds_an_unverified_resolution_that_objectively_fails_its_tests()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/conflict/grade

        // THE SAFETY FLOOR — a resolver that CLAIMS success but objectively FAILS its tests must NEVER ship. Two agents
        // conflict on one file → merge CONFLICTS → resolve parks → [HUMAN APPROVES] → the resolver reconciles BUT drops
        // one agent's work (writes alpha-only) while STILL emitting RESOLUTION_VERIFIED (the lie). The supervisor's
        // objective resolve grade clones the resolver's branch + runs check.sh (which requires BOTH sides) → it FAILS →
        // the resolution is graded Unverified, and the substrate surfaces NO reviewable head: the lying branch is
        // withheld, not accepted. The objective grade can only TIGHTEN the self-report marker, never trust it.
        using var cli = new ConflictThenLyingResolveFakeCli();

        SetDecisionScript(s => s.PlanSpawnMergeResolveApprovedMergeStop());   // SAME script — the CLI controls verification, not the decider

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var conversationId = await SeedConversationAsync(teamId, userId);

        // check.sh requires BOTH sides to survive — the resolver recipe's "do not discard either agent's intent" guardrail
        // made objective. The lying resolver's alpha-only reconciliation fails it.
        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new()
        {
            ["check.sh"] = "#!/bin/sh\nif grep -q alpha shared.txt && grep -q beta shared.txt; then exit 0; else exit 1; fi\n",
            [ConflictThenLyingResolveFakeCli.SharedFile] = "base content\n",
        });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, conversationId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Drive to the resolve-approval park.
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        var token = await AssertParkedOnResolveApprovalAsync(runId, teamId);

        // Approve → the lying resolver runs → its branch fails check.sh → the resolution is graded Unverified → withheld.
        await AnswerAsync(token, "approve", userId, teamId);
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertUnverifiedResolutionWasWithheldAsync(runId, teamId, remote);
    }

    [Fact]
    public async Task Supervisor_withholds_a_multi_repo_resolution_that_lies_about_passing_at_the_stop_backstop()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/per-repo conflict/grade

        // THE MULTI-REPO SAFETY BACKSTOP — the multi-repo resolve verdict is currently MARKER-ONLY (the objective resolve
        // grade is single-repo only), so a multi-repo resolver that LIES (emits RESOLUTION_VERIFIED but drops one side in
        // the conflicted repo) is graded Verified AT THE RESOLVE STEP and its branch is surfaced as a head. The TERMINAL
        // STOP is the independent backstop: it objectively grades EVERY per-repo head (clone + run check.sh) and, when the
        // related repo's alpha-only head fails, withholds ALL per-repo branches from the node output — so the lie never
        // reaches a downstream PR-open. Defence-in-depth, proven end-to-end (the resolve-grade skip is a legibility gap,
        // not a shipping hole, BECAUSE this backstop holds).
        using var cli = new MultiRepoLyingResolveFakeCli();

        SetDecisionScript(s => s.PlanSpawnMergeResolveApprovedMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var conversationId = await SeedConversationAsync(teamId, userId);

        // The primary repo's clean disjoint integration passes its own check.sh; the related repo's check.sh requires
        // BOTH sides survived, which the lying alpha-only reconciliation fails.
        using var primaryRemote = new BareRemote();
        using var relatedRemote = new BareRemote();
        await primaryRemote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n" });
        await relatedRemote.SeedBaseAsync(new()
        {
            ["check.sh"] = "#!/bin/sh\nif grep -q alpha shared.txt && grep -q beta shared.txt; then exit 0; else exit 1; fi\n",
            [MultiRepoLyingResolveFakeCli.SharedFile] = "base content\n",
        });
        var primaryRepoId = await SeedBoundRepositoryAsync(teamId, primaryRemote.Url, "main");
        var relatedRepoId = await SeedBoundRepositoryAsync(teamId, relatedRemote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, primaryRepoId, conversationId, (relatedRepoId, MultiRepoLyingResolveFakeCli.RelatedAlias));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Drive to the resolve-approval park.
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        var token = await AssertParkedOnResolveApprovalAsync(runId, teamId);

        // Approve → the lying multi-repo resolver runs (marker-Verified) → its api head is surfaced → the stop grades each
        // head → the api head fails → the whole per-repo head set is withheld.
        await AnswerAsync(token, "approve", userId, teamId);
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertMultiRepoLieCaughtAtStopBackstopAsync(runId, teamId);
    }

    [Fact]
    public async Task LiveBrainConflictFakeCli_produces_a_real_merge_conflict_under_the_scripted_decider()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        // Deterministic TEETH for the real-model A1 lane (RealModelSupervisorWholeLoopE2ETests): pin that
        // LiveBrainConflictFakeCli — which keys the two conflicting sides on each agent's OWN (brain-authored) goal text
        // rather than the scripted "alpha"/"beta", so it works with a free-form live brain — STILL produces a REAL git
        // conflict here under the scripted decider. Without this pin, the live gate's "no conflict observed" could be a
        // silent CLI bug rather than the brain's choice. The live lane then measures whether the BRAIN chooses to resolve.
        using var cli = new LiveBrainConflictFakeCli();

        SetDecisionScript(s => s.PlanSpawnMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", [LiveBrainConflictFakeCli.SharedFile] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertFirstMergeConflictedAsync(runId, teamId);
    }

    [Fact]
    public async Task LiveBrainFailingFakeCli_fails_every_agent_under_the_scripted_decider()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        // Deterministic TEETH for the real-model A2 lane: pin that LiveBrainFailingFakeCli — which fails EVERY agent
        // unconditionally (so a free-form live brain reliably sees a failure to react to) — really produces Failed agent
        // runs here. Without this pin, the live gate's "agent-failed" signal could be a silent CLI bug. The live lane then
        // measures whether the BRAIN retries / escalates rather than merging over the failure.
        using var cli = new LiveBrainFailingFakeCli();

        SetDecisionScript(s => s.PlanSpawnStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var statuses = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.Status).ToListAsync();

        statuses.Count.ShouldBe(2, "the scripted spawn staged two real agent runs");
        statuses.ShouldAllBe(s => s == AgentRunStatus.Failed, "LiveBrainFailingFakeCli exits non-zero for every agent → every spawned run is a real Failed run");
    }

    // ─── Assertions ──────────────────────────────────────────────────────────────────

    private async Task AssertRunReachedSuccessAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the supervisor→real-agent→patch→merge→acceptance→stop arc must reach Success; if not, inspect the AgentRun.Error + failed WorkflowRunNode rows + the supervisor decision outcomes");
    }

    private async Task AssertBothAgentsProducedRealPatchesAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var results = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
            .Select(r => r.ResultJson).ToListAsync();

        results.Count.ShouldBe(2, "spawn[both] staged exactly 2 real agent runs");

        foreach (var json in results)
        {
            json.ShouldNotBeNull("each real agent persisted a folded AgentRunResult");
            var result = System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(json!, AgentJson.Options)!;
            result.Status.ShouldBe(AgentRunStatus.Succeeded);
            result.Patch.ShouldNotBeNullOrWhiteSpace("the executor's real git-diff captured the file the fake CLI wrote — a real unified diff, not a seeded stand-in");
            result.ProducedBranch.ShouldNotBeNullOrWhiteSpace("the real agent's change was published as its own branch");
            result.ChangedFiles.ShouldContain(f => f.StartsWith(FileWritingFakeCli.FilePrefix), "the captured diff names the agent's written file");
        }
    }

    private async Task AssertAgentEditedSolutionAsync(Guid runId, string expectedBodyMarker)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var results = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
            .Select(r => r.ResultJson).ToListAsync();

        results.Count.ShouldBe(1, "spawn[SubtaskA] staged exactly ONE real agent run");

        var result = System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(results.Single()!, AgentJson.Options)!;
        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.Patch.ShouldNotBeNullOrWhiteSpace("the executor's real git-diff captured the agent's solution.sh edit — a real unified diff");
        result.ChangedFiles.ShouldContain(SolutionWritingFakeCli.SolutionFile, "the captured diff edited the SEEDED source (solution.sh), proving a real agent attempted/solved the TASK — not a marker file (so the wrong-answer teeth isn't a no-op)");
        result.Patch!.ShouldContain(expectedBodyMarker, customMessage: $"the captured diff wrote the EXPECTED solution body ('{expectedBodyMarker}') — pinning WHICH solution the agent produced, not just that it edited the file");
    }

    private async Task AssertDecisionLedgerAsync(Guid runId, Guid teamId, params string[] expectedKinds)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        IReadOnlyList<string> kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .Select(d => d.DecisionKind)
            .ToListAsync();

        kinds.ShouldBe(expectedKinds,
            customMessage: $"the ledger must record {string.Join("/", expectedKinds)} in order — each later turn only advanced because the prior turn's agents completed through the barrier");
    }

    private async Task AssertFirstMergeConflictedAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var firstMerge = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Merge)
            .OrderBy(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();

        System.Text.Json.JsonDocument.Parse(firstMerge).RootElement.GetProperty("integration").GetProperty("status").GetString()
            .ShouldBe("Conflicted", "the two agents edited the SAME file → real git could not auto-combine them (a REAL conflict, not a seeded one)");
    }

    private async Task AssertResolveWasGatedToAnApprovalCardAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // The scripted `resolve` decision is irreversible → the governance rewrites it into an ask_human APPROVAL card
        // (its question carries the approval marker + names the gated action) rather than executing it. So the ledger
        // records NO resolve, and an ask_human whose question is the resolve approval prompt.
        var kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .Select(d => d.DecisionKind).ToListAsync();
        kinds.ShouldNotContain(SupervisorDecisionKinds.Resolve, "the irreversible resolve must NOT have executed silently — it is gated behind approval");

        var askQuestions = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.AskHuman)
            .Select(d => d.PayloadJson).ToListAsync();

        askQuestions.Any(p => p.Contains(SupervisorApprovalRequest.ApprovalMarker, StringComparison.Ordinal) && p.Contains("resolve", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue("the conflict's resolve attempt surfaced a human approval card for the irreversible re-merge (the un-bypassable safety floor) — it was not auto-resolved");
    }

    /// <summary>The run is Suspended on the resolve-approval Action wait (the irreversible-HITL floor parked it); returns the wait token the human answers.</summary>
    private async Task<string> AssertParkedOnResolveApprovalAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "the irreversible resolve parks the run on a human-approval Action wait — not a self-advance");

        var wait = await db.WorkflowRunWait.AsNoTracking()
            .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending);
        wait.IterationKey.ShouldEndWith("#ask", customMessage: "the supervisor ask_human approval parks on the per-turn ask Action wait");

        var ask = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.AskHuman)
            .Select(d => d.PayloadJson).SingleAsync();
        ask.ShouldNotBeNull().Contains(SupervisorApprovalRequest.ApprovalMarker).ShouldBeTrue("the parked card is the resolve approval prompt");

        return wait.Token;
    }

    private async Task AssertResolutionVerifiedAndAcceptedAsync(Guid runId, Guid teamId, BareRemote remote)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // The approved resolve EXECUTED (a resolve decision is now in the ledger) and its resolver agent verified.
        var resolve = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Resolve)
            .OrderByDescending(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();
        SupervisorOutcome.ReadResolutionVerdict(resolve).ShouldBe(SupervisorResolutionVerdict.Verified,
            "the human-approved resolver agent reconciled the branches + ended with RESOLUTION_VERIFIED → the resolution is objectively VERIFIED");

        // The terminal stop accepted the reconciled head (the operator floor graded the resolved branch + passed).
        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();
        SupervisorOutcome.ReadAcceptanceGradePassed(stop).ShouldBe(true, "the stop graded the accepted resolution's head against check.sh and it PASSED");

        // The reconciled head a downstream git.open_change_set would target — read with the SAME production reader the
        // node output uses (ReadFinalIntegratedBranch). For an accepted resolution that head IS the verified resolver's
        // own pushed branch (a re-integration over the original conflicting branches would just re-conflict), surfaced by
        // the accepting merge as its integration.integratedBranch. Asserting THAT exact branch is live on the remote
        // proves the loop shipped a real, reviewable reconciled head — not merely that some agent branch survives.
        var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);
        var reconciledHead = SupervisorOutcome.ReadFinalIntegratedBranch(priorDecisions);
        reconciledHead.ShouldNotBeNullOrWhiteSpace(
            "the accepted resolution must surface a final reviewable head (the verified resolver's reconciled branch) for a downstream PR-open step");

        var branches = await remote.ListBranchesAsync();
        branches.ShouldContain(reconciledHead!,
            customMessage: $"the reconciled head git.open_change_set would target must be live on the remote; remote: [{string.Join(", ", branches)}]");
    }

    /// <summary>The first merge conflicted on EXACTLY the related repo (its shared file) and the primary integrated cleanly — proving the per-repo axes are isolated, off the SAME production reader (<see cref="SupervisorOutcome.ReadConflictedRepos"/>) the resolve loop routes on.</summary>
    private async Task AssertOnlyTheRelatedRepoConflictedAsync(Guid runId, Guid teamId, Guid relatedRepoId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);
        var conflicted = SupervisorOutcome.ReadConflictedRepos(priorDecisions);

        conflicted.Count.ShouldBe(1, "exactly ONE repo conflicted — the related repo (both agents wrote the same file); the primary repo's disjoint files integrated cleanly on their own axis");
        conflicted[0].RepositoryId.ShouldBe(relatedRepoId, "the conflicted repo is the related one the two agents both edited");
        conflicted[0].ConflictedFiles.ShouldContain(MultiRepoConflictFakeCli.SharedFile, "the real per-repo conflict was on the shared file the two agents both wrote");
    }

    /// <summary>The verified per-repo resolution accepted, the stop graded both heads, and EACH repo's final reviewable head (read with the production <see cref="SupervisorOutcome.ReadFinalRepositoryBranches"/> a downstream git.open_change_set binds) is live on ITS OWN remote.</summary>
    private async Task AssertEachRepoHeadAcceptedAndOnItsRemoteAsync(Guid runId, Guid teamId, Dictionary<Guid, BareRemote> remotesByRepo)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // The approved multi-repo resolve executed + verified (the resolver reconciled the conflicted repo + RESOLUTION_VERIFIED).
        var resolve = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Resolve)
            .OrderByDescending(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();
        SupervisorOutcome.ReadResolutionVerdict(resolve).ShouldBe(SupervisorResolutionVerdict.Verified,
            "the human-approved multi-repo resolver reconciled the conflicted repo + ended with RESOLUTION_VERIFIED → the resolution is objectively VERIFIED");

        // The terminal stop graded EVERY per-repo head (operator floor → model) against each repo's check.sh and all passed.
        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();
        SupervisorOutcome.ReadAcceptanceGradePassed(stop).ShouldBe(true, "the stop graded all per-repo reconciled heads against their own check.sh and they PASSED");

        // Each repo's final reviewable head — read with the SAME per-repo production reader git.open_change_set binds —
        // is live on ITS OWN remote (the resolved repo surfaces the resolver's branch, the clean repo its integration branch).
        var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);
        var heads = SupervisorOutcome.ReadFinalRepositoryBranches(priorDecisions);

        heads.Count.ShouldBe(remotesByRepo.Count, $"every repo in scope must surface a final reviewable head; got [{string.Join(", ", heads.Select(h => h.Alias + ":" + h.SourceBranch))}]");

        foreach (var head in heads)
        {
            head.RepositoryId.ShouldNotBeNull("each per-repo head must name its repository (the PR-open key)");
            remotesByRepo.ShouldContainKey(head.RepositoryId!.Value);

            var branches = await remotesByRepo[head.RepositoryId.Value].ListBranchesAsync();
            branches.ShouldContain(head.SourceBranch,
                customMessage: $"repo '{head.Alias}' head '{head.SourceBranch}' must be live on its own remote; remote: [{string.Join(", ", branches)}]");
        }
    }

    /// <summary>A multi-repo resolver lied (marker-Verified at the resolve step, since the objective resolve grade is single-repo only) but the TERMINAL STOP objectively re-graded each per-repo head, caught the dropped side, and withheld ALL per-repo branches from the node output — the lie never reaches a downstream PR-open.</summary>
    private async Task AssertMultiRepoLieCaughtAtStopBackstopAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // THE GAP, DOCUMENTED: the multi-repo resolve verdict trusts the marker alone (the objective resolve grade is
        // single-repo only), so the lying resolver passes the RESOLVE step as Verified and its api branch is surfaced.
        var resolve = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Resolve)
            .OrderByDescending(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();
        SupervisorOutcome.ReadResolutionVerdict(resolve).ShouldBe(SupervisorResolutionVerdict.Verified,
            "the multi-repo resolve verdict trusts the marker alone (no objective resolve grade yet) — so the lie passes the resolve step; the terminal stop is the backstop");

        // THE BACKSTOP: the terminal stop objectively grades EVERY per-repo head (clone + check.sh). The related repo's
        // alpha-only head fails its grep-both-sides check → acceptancePassed=false (all-or-nothing).
        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();
        SupervisorOutcome.ReadAcceptanceGradePassed(stop).ShouldBe(false,
            "the terminal stop cloned each per-repo head + ran its check.sh; the lying related-repo head dropped beta → the stop grade is FALSE");

        // The heads WERE produced: the merge surfaced both per-repo heads on the ledger (primary clean + the lying
        // resolver's api branch, marker-short-circuited). So the node-output absence below is a genuine WITHHOLD, not a
        // never-produced absence — the substrate had real heads in hand and refused to hand them on.
        var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);
        SupervisorOutcome.ReadFinalRepositoryBranches(priorDecisions).Count.ShouldBe(2,
            "the merge surfaced both per-repo reconciled heads on the ledger (primary + the lying api branch) — they exist to be withheld");

        // THE WITHHOLD: a failed multi-repo stop grade withholds ALL per-repo branches from the node output (the SAME
        // acceptancePassed==false condition that withholds the single-repo integratedBranch), so the node emits NO
        // repositoryBranches — a downstream git.open_change_set bound to {{nodes.sup.outputs.repositoryBranches}} gets
        // nothing, and the lie never ships.
        var supRows = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId && n.NodeId == NodeId).ToListAsync();
        var terminal = supRows
            .Select(n => System.Text.Json.JsonDocument.Parse(n.OutputsJson).RootElement)
            .First(o => o.TryGetProperty("status", out _));

        terminal.GetProperty("status").GetString().ShouldBe("AcceptanceFailed",
            "the supervisor reports the objective definition-of-done was NOT met for the multi-repo heads — not a self-reported Completed");
        terminal.TryGetProperty("repositoryBranches", out _).ShouldBeFalse(
            "the per-repo branches are WITHHELD — a head set where any repo fails the operator floor must never be handed to a downstream per-repo PR-open");
    }

    /// <summary>The resolver lied (claimed RESOLUTION_VERIFIED) but its branch objectively failed check.sh, so the resolution graded Unverified and the substrate WITHHELD its branch — surfacing no reviewable head a downstream PR-open could target.</summary>
    private async Task AssertUnverifiedResolutionWasWithheldAsync(Guid runId, Guid teamId, BareRemote remote)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // The resolver agent really ran + pushed a branch + CLAIMED success (RESOLUTION_VERIFIED) — but the OBJECTIVE
        // grade (clone the branch + run check.sh, which requires both sides) caught that it dropped one, so the verdict
        // is Unverified (the self-report marker can only be tightened by the server grade, never trusted alone).
        var resolve = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Resolve)
            .OrderByDescending(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();

        SupervisorOutcome.ReadResolutionVerdict(resolve).ShouldBe(SupervisorResolutionVerdict.Unverified,
            "the resolver emitted RESOLUTION_VERIFIED but its alpha-only branch FAILED check.sh → the objective grade overrides the self-report → Unverified");

        // It is specifically the OBJECTIVE grade that caught the lie — not an absent marker: the resolve's folded
        // acceptance grade RAN (cloned the resolver branch + ran check.sh) and is FALSE. The CLI provably emits
        // RESOLUTION_VERIFIED (so the self-report marker is true), so a false grade is the only thing forcing Unverified.
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve).ShouldBe(false,
            "the objective resolve grade cloned the resolver's branch + ran check.sh against it and it FAILED (alpha-only dropped beta) — the grade is what overrode the true self-report marker");

        // The lying resolver DID produce + push a real branch — so this proves the substrate ACTIVELY WITHHELD a real
        // branch, not merely that nothing was produced.
        var resolverBranch = SupervisorOutcome.ReadAgentResults(resolve).Select(r => r.ProducedBranch).FirstOrDefault(b => !string.IsNullOrEmpty(b));
        resolverBranch.ShouldNotBeNullOrWhiteSpace("the resolver agent pushed its (unverified) reconciliation branch");
        (await remote.ListBranchesAsync()).ShouldContain(resolverBranch!, customMessage: "the resolver's branch really exists on the remote — it is withheld, not absent");

        // THE SAFETY PROPERTY: the production reader a downstream git.open_change_set uses surfaces NO head — the
        // unverified reconciliation is never offered as a reviewable/acceptable branch, so no PR could open from it.
        var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);
        SupervisorOutcome.ReadFinalIntegratedBranch(priorDecisions).ShouldBeNull(
            "the unverified resolution must NOT be surfaced as the final reviewable head — the safety floor withholds it from any PR-open");

        // The terminal stop accepted nothing (no clean head to grade → no passing acceptance was ever recorded).
        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence).Select(d => d.OutcomeJson).FirstAsync();
        SupervisorOutcome.ReadAcceptanceGradePassed(stop).ShouldNotBe(true,
            "with no clean head the stop graded + accepted nothing — it never reports a passing acceptance of the withheld work");
    }

    /// <summary>Replay every decision row of the run into the <see cref="SupervisorPriorDecision"/> shape the production folders (e.g. <see cref="SupervisorOutcome.ReadFinalIntegratedBranch"/>) consume — so the test reads the run's final head EXACTLY as a downstream node would.</summary>
    private static async Task<IReadOnlyList<SupervisorPriorDecision>> ReadPriorDecisionsAsync(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .Select(d => new SupervisorPriorDecision
            {
                Id = d.Id,
                Sequence = d.Sequence,
                DecisionKind = d.DecisionKind,
                Status = d.Status,
                PayloadJson = d.PayloadJson,
                OutcomeJson = d.OutcomeJson,
                Error = d.Error,
            })
            .ToListAsync();

    /// <summary>Seed a team channel the supervisor's approval card posts into + parks on.</summary>
    private async Task<Guid> SeedConversationAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var slug = "sup-wl-" + Guid.NewGuid().ToString("N")[..8];
        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, userId, CancellationToken.None);
    }

    /// <summary>Answer the supervisor's ask_human approval card via the REAL token-correlated resume path (a human replying "approve").</summary>
    private async Task AnswerAsync(string token, string answer, Guid actorUserId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IWorkflowResumeService>()
            .ResumeByActionTokenAsync(token, RealSupervisorActionExecutor.AnswerActionKey, actorUserId, answer, values: null, teamId, CancellationToken.None);
        result.ShouldBe(ActionResumeResult.Resumed, "the human's approval resolves the supervisor's resolve-approval wait via the existing token-correlated resume path");
    }

    private async Task AssertOneAgentFailedThenTheRetrySucceededAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var statuses = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
            .Select(r => r.Status).ToListAsync();

        // 3 real agent runs: alpha (succeeded), beta's first attempt (FAILED, no patch), beta's retry (succeeded).
        statuses.Count.ShouldBe(3, "spawn[both] staged 2 + the retry staged 1");
        statuses.Count(s => s == AgentRunStatus.Failed).ShouldBe(1, "exactly the first 'do beta' attempt FAILED — a real failed agent run");
        statuses.Count(s => s == AgentRunStatus.Succeeded).ShouldBe(2, "alpha + the beta RETRY both succeeded with real patches");
    }

    private async Task AssertIntegratedBranchOnRemoteAsync(BareRemote remote, Guid runId, int turn = 2)
    {
        // The merge turn's sequence is `turn` → the integrator's reviewable branch is codespace/integration/<run>/turn{N}.
        var branch = $"codespace/integration/{runId:N}/turn{turn}";
        (await remote.RemoteHasBranchAsync(branch)).ShouldBeTrue(
            $"the supervisor's merge really integrated the agents' real patches and pushed {branch} to the bare remote");
    }

    private async Task AssertAcceptancePassedOnStopAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence)
            .FirstAsync();

        SupervisorOutcome.ReadAcceptanceGradePassed(stop.OutcomeJson).ShouldBe(true,
            "the terminal stop graded the integrated branch against the real check.sh operator floor and it PASSED");
    }

    private async Task AssertAcceptanceFailedAndBranchWithheldAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence)
            .FirstAsync();

        SupervisorOutcome.ReadAcceptanceGradePassed(stop.OutcomeJson).ShouldBe(false,
            "the integrated head failed the real check.sh floor → the stop's objective grade is FALSE, not a self-reported success");

        // The terminal supervisor node output: status=AcceptanceFailed + the reviewable branch withheld to "".
        var supRows = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId && n.NodeId == NodeId).ToListAsync();
        var terminal = supRows
            .Select(n => System.Text.Json.JsonDocument.Parse(n.OutputsJson).RootElement)
            .First(o => o.TryGetProperty("status", out _));

        terminal.GetProperty("status").GetString().ShouldBe("AcceptanceFailed",
            "the supervisor reports the objective definition-of-done was NOT met — not a self-reported Completed");
        terminal.GetProperty("integratedBranch").GetString().ShouldBe("",
            "the reviewable branch is WITHHELD — a head that fails the operator floor must never be handed to a downstream PR-open");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    private void SetDecisionScript(Action<SupervisorDecisionScript> set)
    {
        using var scope = _fixture.BeginScope();
        set(scope.Resolve<SupervisorDecisionScript>());
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> CreateWholeLoopWorkflowAsync(Guid teamId, Guid userId, Guid repoId, Guid? conversationId = null, (Guid RepoId, string Alias)? relatedRepo = null)
    {
        // The supervisor's agents clone repoId, push their branches, and the merge integrates them; the operator's
        // acceptance floor (check.sh) gates the terminal stop against the integrated head. A conversationId (when set)
        // is the approval surface the irreversible `resolve` gate posts its human-approval card into + parks on.
        var conversationLine = conversationId is { } cid ? $",\n              \"conversationId\": \"{cid}\"" : "";

        // A relatedRepo (when set) makes this a MULTI-repo run: the agent profile mounts a SECOND writable repo under
        // its alias, so each agent's workspace has both repos (cwd = workspace root, each repo in <root>/<alias>/) and
        // the supervisor integrates / resolves / accepts EACH repo on its own axis.
        var relatedLine = relatedRepo is { } rr ? $",\n                \"relatedRepositories\": [ {{ \"repositoryId\": \"{rr.RepoId}\", \"alias\": \"{rr.Alias}\", \"access\": \"write\" }} ]" : "";
        var supConfig = $$"""
            {
              "goal": "ship the feature",
              "agentProfile": { "repositoryId": "{{repoId}}", "pushBranch": true, "integrateBranches": true{{relatedLine}} },
              "acceptanceChecks": ["sh", "check.sh"]{{conversationLine}}
            }
            """;

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-wholeloop-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

        var serializer = scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "integration-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the remote — base-seeding + ref inspection. GUID-suffixed; best-effort cleaned.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-wholeloop-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Config(seed);
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

        /// <summary>Every branch on the remote, trimmed — the caller filters (avoids git refglob ambiguity over <c>/</c>).</summary>
        public async Task<IReadOnlyList<string>> ListBranchesAsync() =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(b => b.TrimStart('*', ' ').Trim()).ToList();

        private static async Task Config(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
