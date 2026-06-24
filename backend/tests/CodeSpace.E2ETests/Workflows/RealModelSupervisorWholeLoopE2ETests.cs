using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
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
/// 🟢 THE live-brain whole-loop gate (P0-b.2b) — the headline join: a REAL model drives the REAL durable engine end to
/// end. It is <see cref="SupervisorWholeLoopE2ETests"/> (the deterministic skeleton) with the scripted decider SWAPPED
/// for the production <see cref="CodeSpace.Core.Services.Supervisor.Deciders.LlmSupervisorDecider"/> (via the
/// <see cref="SupervisorDeciderMode"/> seam, P0-b.2a), the brain's credential resolved from a SEEDED, encrypted
/// <c>ModelCredential</c> row (the live decider reads its key from the DB, never in-process). The live model authors
/// plan → spawn → (inspect/retry) → merge → stop on its own; the spawned agents are REAL OS processes that EDIT REAL
/// FILES (<see cref="FileWritingFakeCli"/>), the executor captures REAL patches, the merge integrates them on a bare
/// <c>file://</c> remote, and the terminal stop is graded against a real <c>check.sh</c> acceptance floor (a real clone
/// + real script execution) — so a green verdict means a LIVE brain really drove real agents through
/// plan → spawn → merge → accept → stop against the real durable engine, real git integration, and a real acceptance
/// gate. The ORCHESTRATION is real and live-authored end to end; what is stubbed is the agent's CODING (the fake codex
/// writes a mechanical patch) and therefore the seeded <c>check.sh</c> is a STRUCTURAL green-check (<c>exit 0</c>), not a
/// goal-relevance oracle — the gate certifies the live brain drove the whole arc to a real integrated+accepted head, not
/// that it solved the task (the live decision QUALITY is measured separately by the golden/trajectory decision evals).
///
/// <para>GATING — the HEADLINE drive→accept arc (<c>The_real_model_drives_the_whole_loop_to_an_integrated_accepted_patch</c>)
/// hard-gates on the real-model-DROVE-to-completion criterion: the blessed wire passes ONLY when the live model drove the
/// whole arc to the real integrated+accepted head (<see cref="RealModelOutcome.Drove"/>). A model CAPABILITY MISS (the
/// model RAN but parked short of the accept head) now REDS the blessed wire — it is the criterion, not a footnote — made
/// FLAKE-SAFE by a bounded best-of-N capability-floor (a fresh run per attempt; gates only if EVERY non-infra attempt
/// parks short, ~p^N). A CODE FAULT reds at once; a gateway timeout is non-gating LOUD infra. The two REACTION arcs
/// (observe-a-conflict→resolve, failed-subtask→retry) still gate only on a CODE FAULT and REPORT a capability miss —
/// they assert the model REACTS correctly, a harder/more-variable signal tightened separately. Self-skips when ALL
/// <c>CODESPACE_LLM_*</c> secrets are absent (forks stay green at zero cost, surfaced LOUDLY as NOT EVALUATED — skip ≠
/// pass) but FAILS on a partial config (a rotated/blanked single secret can't silently mask the lane). What is stubbed is
/// the agent's CODING (the fake codex) so the seeded <c>check.sh</c> is a STRUCTURAL exit-0, not a goal-relevance oracle
/// — the gate certifies the live brain drove the whole arc to a real integrated+accepted head; decision QUALITY stays the
/// golden decision-eval.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelSupervisorWholeLoopE2ETests : IDisposable
{
    private const string NodeId = "sup";
    private const string Provider = "Anthropic";   // the blessed brain wire (RealModelGate gates it)

    private readonly PostgresFixture _fixture;
    private readonly string? _laneBefore;
    private readonly string? _integrateBefore;

    public RealModelSupervisorWholeLoopE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _laneBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        _integrateBefore = Environment.GetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar);

        // The only THROWABLE mutation (the DI resolve that flips the decider) runs FIRST, so a ctor throw leaks no
        // process-global; the env-var sets (which cannot throw) follow. Dispose restores all three.
        SetDeciderMode(useLiveModel: true);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _laneBefore);
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, _integrateBefore);

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = false;   // restore the shared-fixture default for siblings
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [Fact]
    public async Task The_real_model_drives_the_whole_loop_to_an_integrated_accepted_patch()
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        // Fail-closed on a PARTIAL secret config: all three absent → honest fork/local skip; some-but-not-all present
        // is a broken/rotated/renamed secret that would otherwise self-skip the BLESSED gate GREEN having driven no live
        // brain at all — so throw to turn that masked-nothing into a RED main run instead of a false green.
        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass: surfaced loudly as NOT EVALUATED
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip the blessed whole-loop gate GREEN proving nothing.");

        if (OperatingSystem.IsWindows()) return;                          // the fake CLI is a /bin/sh script
        if (!await GitReadyAsync()) return;                              // real git is required for clone/capture/integrate

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        // A NON-VACUOUS acceptance floor: the integrated head must actually CONTAIN an agent's work (an agent_*.txt that
        // FileWritingFakeCli writes), so a green grade proves the brain's spawn really integrated — not just that an
        // exit-0 script ran against an empty tree. If the integration carried no agent file, check.sh exits 1 → the stop's
        // objective acceptance FAILS → acceptancePassed=false → the run does not read as Drove.
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        // The supervisor's brain runs on this seeded credential (key encrypted into the DB row the live decider reads).
        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId);

        // STRICT real-model-DROVE-to-completion gate (the real-model whole-loop CONNECTIVITY criterion). The blessed wire
        // passes ONLY when the live model drove the whole arc to the real integrated+accepted head (Drove). A CAPABILITY
        // MISS — the model RAN but parked short of the accept head — now REDS, made flake-safe by a bounded best-of-N
        // capability-floor (a FRESH run per attempt; gates only if EVERY non-infra attempt parks short, ~p^N). A CODE FAULT
        // reds at once (never retried); a gateway timeout is non-gating LOUD infra (doesn't consume an attempt slot). A
        // no-secret skip was already surfaced NOT-EVALUATED above (skip ≠ pass). Decision QUALITY stays the golden eval.
        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            // Clear the shared in-memory job client per best-of-N attempt. SAFE because [Collection(PostgresCollection)]
            // runs every test in this collection SERIALLY — no concurrent sibling has in-flight jobs to drop. (WaitForPendingAsync
            // already drained the prior attempt to empty, so this is a no-op-on-empty between attempts.)
            jobClient.Clear();
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);   // a FRESH run per attempt — re-seed, never reuse a parked-short run

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            var (outcome, note) = await EvaluateAsync(runId, teamId);
            return (outcome, $"{Provider} model '{model}' whole-loop — {note}");
        });
    }

    [Fact]
    public async Task The_real_model_observes_a_real_conflict_and_chooses_to_resolve()
    {
        // Real-scenario coverage A1 — the headline gap the deterministic whole-loop can't reach: a LIVE brain reacting to
        // REAL adverse git state. Every conflict→resolve arc that runs through the real engine today uses the SCRIPTED
        // decider; the only live-brain whole-loop is happy-path. Here the live model is handed a task whose two parallel
        // subtasks edit the SAME file, so their real diffs CONFLICT, and the brain must OBSERVE the conflicted integration
        // in its own SupervisorOutcome context and CHOOSE `resolve` (which the irreversible-HITL floor then gates to an
        // approval card — proving the brain reached the recovery decision; the approval→reconcile→accept tail is proven
        // deterministically by SupervisorWholeLoopE2ETests). INFORMATIONAL (gating:false): a live model may decompose the
        // task without a real conflict, or choose stop/retry — the note records exactly what it did, and this lane never
        // reds main (the blessed intelligence kill-gate is the golden decision-eval).
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) return;
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip this lane green proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new LiveBrainConflictFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var conversationId = await SeedConversationAsync(teamId, userId);   // the surface the irreversible resolve parks its approval on

        // shared.txt is seeded so each agent's edit is a real diff against a common base → a real git conflict when two run.
        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", [LiveBrainConflictFakeCli.SharedFile] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        const string goal = "The file shared.txt needs two improvements developed IN PARALLEL by two separate agents, each editing shared.txt: "
                          + "(1) add input validation, and (2) add error logging. Spawn one agent per improvement, integrate their branches, "
                          + "and if the integration conflicts, resolve it into one reconciled version before finishing.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal, conversationId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            var (outcome, note) = await EvaluateConflictResolveAsync(runId, teamId);
            return (outcome, $"{Provider} model '{model}' conflict→resolve — {note}");
        });
    }

    [Fact]
    public async Task The_real_model_reacts_to_a_failed_subtask_by_retrying()
    {
        // Real-scenario coverage A2 — a LIVE brain reacting to a real agent FAILURE through the real engine. Every
        // failure→retry arc that runs through the real engine today uses the SCRIPTED decider. Here every spawned agent
        // FAILS (LiveBrainFailingFakeCli: exit 1, no patch) — the only way to deterministically present a real failure to
        // a live model, since a retry's revised instruction is brain-authored (no CLI-visible attempt marker to key a
        // "fail-first" CLI on). The brain must OBSERVE the failed subtask in its SupervisorOutcome context and author
        // `retry` (the recovery action), NEVER merging over the failure. INFORMATIONAL (gating:false): a live model may
        // escalate via stop/ask_human instead of retry — both safe; the note records what it did, and this lane never
        // reds main.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) return;
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip this lane green proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new LiveBrainFailingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        const string goal = "Add server-side email-format validation to the signup endpoint, with unit tests. "
                          + "If a subtask's agent reports it could not complete the work, retry that subtask before finishing.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            var (outcome, note) = await EvaluateFailureRetryAsync(runId, teamId);
            return (outcome, $"{Provider} model '{model}' failure→retry — {note}");
        });
    }

    [Fact]
    public async Task The_real_coding_agent_solves_a_goal_relevance_task_authored_by_the_live_model()
    {
        // ITEM #2 LIVE ARM — the deepest 解對任務 proof: a REAL coding-CLI (claude-code) driven by a live model EDITS a
        // real source (solution.sh) and the GOAL-RELEVANCE oracle grades whether it actually SOLVED the task (output
        // equality: sh solution.sh 7 5 == 12), not just that a file integrated. The brain is also live (this lane's
        // default), so the whole arc — brain drives → real agent solves → real merge → goal-relevance accept — is real.
        //
        // REPORT-ONLY for now (legacy AssessLiveAsync three-way): a brand-new live integration (real claude CLI → gateway →
        // real edit) is REPORTED to the job summary (Drove = the real model SOLVED the task; CapabilityMiss = it didn't) and
        // only a CODE FAULT reds. Flip to AssessLiveWholeLoopAsync (strict gate + best-of-N) once the first live run
        // confirms the wiring AND a solve — gating main on an unproven live coding path would violate 穩.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip this lane green proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the real-coding arm needs a harness binary (skip ≠ pass)"); return; }   // honest-skip, NOT a pass

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        // The GOAL-RELEVANCE oracle: the agent must edit solution.sh so `sh solution.sh 7 5` prints 12 — graded by check.sh.
        await remote.SeedBaseAsync(new() { ["check.sh"] = SolutionWritingFakeCli.GoalRelevanceCheckSh, ["solution.sh"] = SolutionWritingFakeCli.SeededStub });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, agentCredId) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        // The spawned agent runs the REAL claude-code CLI (agentCredId → its gateway credential) at Trusted autonomy.
        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId,
            goal: "Edit the file solution.sh so that running `sh solution.sh A B` prints the SUM of the two integer arguments A and B. Keep it a POSIX /bin/sh script. Do not change anything else.",
            agentCredId: agentCredId, agentModel: model);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            var (outcome, note) = await EvaluateAsync(runId, teamId);
            return (outcome, $"{Provider} model '{model}' CODING-agent goal-relevance (Drove = SOLVED the task) — {note}");
        });
    }

    /// <summary>Whether the real <c>claude</c> coding-agent CLI is on PATH — the live-coding arm self-skips (NOT a pass) when it is absent (fork/local, or a runner without the install step).</summary>
    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>The live brain reacted to the real failure iff it FANNED OUT (spawn), at least one agent really FAILED, and the brain then chose the recovery action `retry`. Classified three-way; reports each signal (and whether it instead escalated via stop) so a non-retrying trajectory is legible, not a bare red.</summary>
    private async Task<(RealModelOutcome Outcome, string Note)> EvaluateFailureRetryAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        await ThrowIfGatewayInfraFailureAsync(db, runId);   // a mid-turn gateway outage is non-gating infra, not a code fault

        var kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence).Select(d => d.DecisionKind).ToListAsync();

        var spawned = kinds.Contains(SupervisorDecisionKinds.Spawn);
        var someAgentFailed = await db.AgentRun.AsNoTracking().AnyAsync(r => r.WorkflowRunId == runId && r.Status == AgentRunStatus.Failed);
        var retried = kinds.Contains(SupervisorDecisionKinds.Retry);

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        var trail = string.Join("→", kinds);

        var drove = spawned && someAgentFailed && retried;
        return (Classify(run.Status, drove), $"status={run.Status}, spawned={spawned}, agent-failed={someAgentFailed}, retried={retried}, trajectory={trail}");
    }

    /// <summary>The live brain reacted to the real conflict iff it FANNED OUT (spawn), the real-git merge genuinely CONFLICTED, and the brain then CHOSE resolve (executed, or gated to the resolve-approval ask_human floor). Classified three-way; reports each signal so a non-resolving trajectory is legible, not a bare red.</summary>
    private async Task<(RealModelOutcome Outcome, string Note)> EvaluateConflictResolveAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        await ThrowIfGatewayInfraFailureAsync(db, runId);   // a mid-turn gateway outage is non-gating infra, not a code fault

        var decisions = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .Select(d => new { d.DecisionKind, d.PayloadJson, d.OutcomeJson })
            .ToListAsync();

        var spawned = decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var conflicted = decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is { IsConflicted: true });
        // The brain chose resolve: either it executed (a Resolve row) or — the common path — the irreversible-HITL floor
        // rewrote it into an ask_human approval card carrying the resolve-approval marker.
        var resolveChosen = decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Resolve)
            || decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman
                                  && d.PayloadJson.Contains(SupervisorApprovalRequest.ApprovalMarker, StringComparison.Ordinal)
                                  && d.PayloadJson.Contains("resolve", StringComparison.OrdinalIgnoreCase));

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        var trail = string.Join("→", decisions.Select(d => d.DecisionKind));

        var drove = spawned && conflicted && resolveChosen;
        return (Classify(run.Status, drove), $"status={run.Status}, spawned={spawned}, merge-conflicted={conflicted}, resolve-chosen={resolveChosen}, trajectory={trail}");
    }

    /// <summary>Seed a team channel the supervisor's irreversible-resolve approval card parks on (so a live brain that chooses resolve parks cleanly rather than erroring on a missing surface).</summary>
    private async Task<Guid> SeedConversationAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var slug = "sup-lb-" + Guid.NewGuid().ToString("N")[..8];
        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, userId, CancellationToken.None);
    }

    // ─── Verdict ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Map a live whole-loop run to the THREE-WAY gate outcome so the blessed wire reds ONLY on a code regression. A
    /// FAULTED run (<see cref="WorkflowRunStatus.Failure"/>) is a <see cref="RealModelOutcome.CodeFault"/> — the engine
    /// could not execute the brain's decisions, a real substrate bug — because every MODEL-side miss now fails closed to
    /// a clean stop (the decider never crashes the run on a non-conformant reply), so a Failure is never a model miss. A
    /// run that drove the arc → <see cref="RealModelOutcome.Drove"/>; any other clean terminal (the brain stopped or
    /// parked short of the arc — a capability shortfall, not a code bug) → <see cref="RealModelOutcome.CapabilityMiss"/>,
    /// which is reported but never gates.
    /// </summary>
    private static RealModelOutcome Classify(WorkflowRunStatus status, bool drove) =>
        status == WorkflowRunStatus.Failure ? RealModelOutcome.CodeFault
        : drove ? RealModelOutcome.Drove
        : RealModelOutcome.CapabilityMiss;

    /// <summary>
    /// A gateway/transport outage DURING a turn is swallowed by the engine into a run Failure (the run-level error is the
    /// generic "Node failed."; the typed <c>LlmApiException</c> detail lives on the node-failed ledger record). If THAT
    /// detail is a gateway infra failure, throw a <see cref="TimeoutException"/> so the live-gate's infra-skip catch
    /// treats it as NON-GATING — honoring the lane-wide "a gateway timeout never gates" guarantee (consistent with the
    /// decision-eval lane), instead of the three-way classifier reading the Failure as a code fault. A genuine engine
    /// fault (any other node-failed error) is left untouched, so it gates as a <see cref="RealModelOutcome.CodeFault"/>.
    /// Called by every evaluator BEFORE it classifies, so the routing is uniform across all three live lanes.
    /// </summary>
    private async Task ThrowIfGatewayInfraFailureAsync(CodeSpaceDbContext db, Guid runId)
    {
        var nodeFailure = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed && r.NodeId == NodeId)
            .OrderByDescending(r => r.Sequence).Select(r => r.PayloadJson).FirstOrDefaultAsync();

        if (RealModelGate.IsGatewayInfraError(nodeFailure))
            throw new TimeoutException($"the supervisor brain's gateway failed mid-run (NON-GATING infra): {nodeFailure}");
    }

    /// <summary>The live brain drove the whole loop soundly iff the run reached Success, at least one real agent produced a real patch, and the terminal stop's objective acceptance PASSED (a green check.sh against the integrated head). Classified three-way for safe gating + returns a legible note.</summary>
    private async Task<(RealModelOutcome Outcome, string Note)> EvaluateAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        await ThrowIfGatewayInfraFailureAsync(db, runId);   // a mid-turn gateway outage is non-gating infra, not a code fault

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence).Select(d => d.DecisionKind).ToListAsync();

        var patches = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId && r.Status == AgentRunStatus.Succeeded)
            .Select(r => r.ResultJson).ToListAsync();
        var realPatchCount = patches.Count(j => j is not null
            && System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(j!, AgentJson.Options)?.Patch is { Length: > 0 });

        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence).FirstOrDefaultAsync();
        var acceptancePassed = stop is not null && SupervisorOutcome.ReadAcceptanceGradePassed(stop.OutcomeJson) == true;

        // Structural floor: the live brain must have actually FANNED OUT (spawn) and INTEGRATED (merge), not merely
        // reached a green acceptance. acceptancePassed already implies a merged head today (the grade only runs against a
        // real integrated branch), so this is an EXPLICIT, legible guard that keeps the gate honest if that coupling is
        // ever loosened — and it asserts the trajectory the note already records rather than leaving it unchecked.
        var spawnedAndMerged = kinds.Contains(SupervisorDecisionKinds.Spawn) && kinds.Contains(SupervisorDecisionKinds.Merge);

        var trail = string.Join("→", kinds);
        var drove = run.Status == WorkflowRunStatus.Success && realPatchCount >= 1 && acceptancePassed && spawnedAndMerged;
        return (Classify(run.Status, drove), $"status={run.Status}, realPatches={realPatchCount}, acceptancePassed={acceptancePassed}, spawnedAndMerged={spawnedAndMerged}, trajectory={trail}");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Anthropic's client appends <c>/v1/messages</c> to the host base — pass the gateway host as-is.</summary>
    private static string BaseUrlFor(string baseUrl) => baseUrl.TrimEnd('/');

    private void SetDeciderMode(bool useLiveModel)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = useLiveModel;
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

    /// <summary>Seed a KEYED, structured-capable credentialed-model row for the supervisor brain (the live decider reads its key + base url from this row). Returns the row id → the supervisor's <c>supervisorModelId</c>.</summary>
    private async Task<(Guid RowId, Guid CredId)> SeedBrainModelAsync(Guid teamId, string baseUrl, string apiKey, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "live brain cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, SupportsStructuredOutput = true, Enabled = true });

        await db.SaveChangesAsync();
        return (rowId, credId);   // RowId = the brain's supervisorModelId; CredId = the credential the AGENT profile authenticates with
    }

    private async Task<Guid> CreateWholeLoopWorkflowAsync(Guid teamId, Guid userId, Guid repoId, Guid brainModelId, string? goal = null, Guid? conversationId = null, Guid? agentCredId = null, string? agentModel = null)
    {
        // When an agent credential is supplied, the spawned agents run a REAL coding-CLI harness (claude-code) against the
        // gateway (its credential decrypted just-in-time + projected onto ANTHROPIC_BASE_URL/AUTH_TOKEN by the harness), at
        // Trusted autonomy so they get network egress to reach the gateway. Absent → the byte-identical fake-agent profile.
        var realAgentFields = agentCredId is { } ac
            ? $$""", "harness": "claude-code", "modelCredentialId": "{{ac}}", "model": "{{agentModel}}", "autonomyLevel": "Trusted" """
            : "";
        // The live brain (supervisorModelId) authors the arc; its agents clone repoId + push branches, the merge
        // integrates them, and the operator acceptance floor (check.sh) gates the terminal stop. The SCRIPTED skeleton
        // converges in ~4 rounds (plan→spawn→merge→stop), but a REAL model is less efficient — plan → spawn → inspect →
        // (retry) → merge → stop is already ~5-6 turns with zero slack — so maxRounds:12 gives a real model a FAIR budget
        // to drive to the accept head (a tight 6 starved it: the strict gate is real-model-drove-to-completion, so a budget
        // tuned for the scripted skeleton would red a capable-but-deliberate model). 12 rounds still BOUNDS the wall-clock
        // well under this lane's job timeout; a per-call timeout still self-skips as non-gating infra.
        // A conversationId (when set) is the surface the irreversible `resolve` gate parks its human-approval card on.
        var effectiveGoal = goal ?? "Add server-side email-format validation to the signup endpoint, with unit tests.";
        var conversationLine = conversationId is { } cid ? $",\n              \"conversationId\": \"{cid}\"" : "";
        var supConfig = $$"""
            {
              "goal": "{{effectiveGoal}}",
              "supervisorModelId": "{{brainModelId}}",
              "maxRounds": 12,
              "agentProfile": { "repositoryId": "{{repoId}}", "pushBranch": true, "integrateBranches": true{{realAgentFields}} },
              "acceptanceChecks": ["sh", "check.sh"]{{conversationLine}}
            }
            """;

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-livewholeloop-" + Guid.NewGuid().ToString("N")[..6],
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

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    /// <summary>A bare local repo standing in for the remote — base-seeding + best-effort cleanup.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-livewholeloop-" + Guid.NewGuid().ToString("N"));
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
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
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
