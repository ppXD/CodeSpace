using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Phases;
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
    private readonly string? _integrateBefore;

    public RealModelSupervisorWholeLoopE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _integrateBefore = Environment.GetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar);

        // The only THROWABLE mutation (the DI resolve that flips the decider) runs FIRST, so a ctor throw leaks no
        // process-global; the env-var set (which cannot throw) follows. Dispose restores both.
        SetDeciderMode(useLiveModel: true);
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, "1");
    }

    public void Dispose()
    {
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

            var (outcome, note) = await EvaluateAsync(runId, teamId, deterministicFakeAgents: true);   // headline arc = FileWritingFakeCli (always patches on success)
            return (outcome, $"{Provider} model '{model}' whole-loop — {note}");
        });
    }

    [Fact]
    public async Task The_real_model_authors_heterogeneous_per_agent_dispatch_when_the_goal_invites_distinct_roles()
    {
        // L4 ARC B — the model-authored DIVISION OF LABOUR proof: given a goal that invites two DISTINCT roles, does a live
        // model AUTHOR a heterogeneous agents[] dispatch (each subtask its own role) rather than fan out homogeneous agents?
        // The schema + executor + clamps for agents[] are already gated deterministically (SupervisorSpawnFlowTests); this
        // OBSERVES whether the REAL brain uses the option now that the prompt surfaces it. REPORT-ONLY (gating:false): a model
        // may legitimately decline to differentiate, so a homogeneous spawn is a reported ⚠️, never a red — exactly the
        // first-rollout tier the real-coding arm uses. Dispatch authorship is read from the STAGED agents' goals, so it does
        // NOT depend on the run reaching a terminal Success (an unrelated merge/accept failure can't false-red this arm).
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        // A goal that explicitly invites a per-agent agents[] dispatch with two DISTINCT roles (the model is free to decline).
        const string dispatchGoal =
            "Harden the signup endpoint, splitting the work across TWO agents with DISTINCT roles working in parallel: a "
          + "'backend implementer' that adds the server-side validation, and a separate 'test author' that writes the unit "
          + "tests. When you spawn, author a per-agent agents[] dispatch that gives each agent its own role.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal: dispatchGoal);

        // REPORT-ONLY: ✅ = the live model authored heterogeneous per-agent dispatch (≥2 agents with distinct, role-prefixed
        // goals — the executor renders an authored role as "As the <role>, …"); ⚠️ = it fanned out homogeneous agents
        // (reported, never gating). A gateway outage is a non-gating infra skip.
        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            // Read the AUTHORITATIVE signal — the model's own spawn DECISION payload, not the rendered agent goal: did it
            // author a per-agent agents[] dispatch with ≥2 DISTINCT roles? Keying on SupervisorSpawnPayload.Agents proves
            // the MODEL authored heterogeneous dispatch (a rendered-goal substring could coincide on a plain fan-out).
            var spawnPayloads = await db.SupervisorDecisionRecord.AsNoTracking()
                .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Spawn)
                .OrderBy(d => d.Sequence).Select(d => d.PayloadJson).ToListAsync();

            var authoredAgents = spawnPayloads
                .SelectMany(p => System.Text.Json.JsonSerializer.Deserialize<Messages.Agents.SupervisorSpawnPayload>(p, AgentJson.Options)?.Agents
                                 ?? Enumerable.Empty<Messages.Agents.SupervisorAgentDispatch>())
                .ToList();
            var distinctRoles = authoredAgents.Where(a => !string.IsNullOrWhiteSpace(a.Role)).Select(a => a.Role!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var authored = authoredAgents.Count >= 2 && distinctRoles.Count >= 2;

            var sample = string.Join(" / ", distinctRoles.Take(3));
            return (authored,
                $"{Provider} '{model}' spawn dispatch: authored agents[]={authoredAgents.Count}, distinct roles={distinctRoles.Count} [{sample}]. "
              + (authored ? "DROVE — the live model authored a heterogeneous per-agent division of labour (agents[] in the spawn decision)." : "did NOT author heterogeneous dispatch — homogeneous fan-out (reported, not gating)."));
        }, gating: false);
    }

    [Fact]
    public async Task The_real_model_authors_semantic_phases_when_the_goal_has_distinct_stages()
    {
        // L4 ARC C — the model-authored SEMANTIC-PHASE proof, completing the L4 authorship trilogy (per-agent dispatch
        // #682, stop-DoD #692, and now phases): given a goal with DISTINCT stages, does a live model GROUP its subtasks
        // into named plan.phases rather than emit a flat list? The schema + executor fold + projection for phases are
        // already gated deterministically (SupervisorPhaseSourceTests / SupervisorPlanFoldFlowTests); this OBSERVES whether
        // the REAL brain uses the option now that the prompt surfaces it. REPORT-ONLY (gating:false): a flat plan is a
        // valid model choice (and byte-identical), so a no-phases plan is a reported ⚠️, never a red — exactly the tier
        // the dispatch / DoD authorship arms use. Phase authorship is read from the AUTHORITATIVE plan DECISION payload,
        // so it does NOT depend on the run reaching a terminal Success.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        // A goal with explicitly DISTINCT stages, inviting a phased plan (the model is free to emit a flat plan instead).
        const string phasedGoal =
            "Add rate limiting to the API in THREE distinct stages: first INVESTIGATE the current request-handling path "
          + "and choose an approach, then IMPLEMENT the limiter, then REVIEW it with tests. When you plan, group the "
          + "subtasks into named phases for these stages.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal: phasedGoal);

        // REPORT-ONLY: ✅ = the live model authored ≥2 named plan.phases with distinct titles; ⚠️ = a flat plan
        // (reported, never gating — a flat plan is valid and byte-identical). A gateway outage is a non-gating infra skip.
        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            // Read the AUTHORITATIVE signal — the model's own plan DECISION payload: did it group subtasks into named
            // phases? Keying on SupervisorPlanPayload.Phases proves the MODEL authored the phases (the projected phase
            // view folds the plan OUTCOME; the raw decision payload is the model's own bytes).
            var planPayloads = await db.SupervisorDecisionRecord.AsNoTracking()
                .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Plan)
                .OrderBy(d => d.Sequence).Select(d => d.PayloadJson).ToListAsync();

            var authoredPhases = planPayloads
                .SelectMany(p => System.Text.Json.JsonSerializer.Deserialize<Messages.Agents.SupervisorPlanPayload>(p, AgentJson.Options)?.Phases
                                 ?? Enumerable.Empty<Messages.Agents.SupervisorPlanPhase>())
                .ToList();
            var distinctTitles = authoredPhases.Where(p => !string.IsNullOrWhiteSpace(p.Title)).Select(p => p.Title.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var authored = authoredPhases.Count >= 2 && distinctTitles.Count >= 2;

            var sample = string.Join(" / ", distinctTitles.Take(3));
            return (authored,
                $"{Provider} '{model}' plan phases: authored={authoredPhases.Count}, distinct titles={distinctTitles.Count} [{sample}]. "
              + (authored ? "DROVE — the live model grouped its subtasks into named semantic phases (plan.phases in the plan decision)." : "did NOT author phases — flat subtask plan (reported, not gating)."));
        }, gating: false);
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
        // deterministically by SupervisorWholeLoopE2ETests). GATING best-of-N: the live model must drive spawn→real-git-
        // conflict→a SAFE reaction (resolve, or the prompt-sanctioned stop/escalate), or the blessed wire REDs after a
        // bounded capability-floor (a FRESH run per attempt; reds only if EVERY non-infra attempt parks short, ~p^N). A
        // CodeFault reds at once; a gateway outage is non-gating LOUD infra; a no-secret config skips NOT-EVALUATED
        // (skip ≠ pass). The note records the trajectory (incl. whether it chose resolve) so a miss is legible.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass — a gating arm must surface NOT-EVALUATED, never self-skip green
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

        // GATING best-of-N (N is the job's CODESPACE_REALMODEL_WHOLE_LOOP_ATTEMPTS, uniform with the headline/solve arms):
        // each attempt is a FRESH run (re-seeded inside) so the gate reds only if EVERY non-infra attempt fails to drive
        // spawn→real-git-conflict→a safe reaction. The criterion accepts ANY prompt-sanctioned handling (resolve, stop-for-
        // human, or escalate — see EvaluateConflictResolveAsync), so it reds on genuine mishandling, never a sanctioned verb.
        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            jobClient.Clear();
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);   // a FRESH run per best-of-N attempt

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
        // an ACTIVE recovery, NEVER merging over the failure. GATING best-of-N: the goal + the decider's standing rail both
        // instruct a retry-on-failure, so the live model must take an active recovery (`retry`, or an `ask_human` escalate),
        // or the blessed wire REDs after a bounded capability-floor (a FRESH run per attempt). A CodeFault reds at once; a
        // gateway outage is non-gating LOUD infra; a no-secret config skips NOT-EVALUATED. The perpetual-failure scenario
        // force-STOPs cleanly at the decision budget ("budget exhausted"), never a run Failure, so a model that recovered
        // reads Drove from the ledger and is never mis-gated as a CodeFault.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass — a gating arm must surface NOT-EVALUATED, never self-skip green
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

        // GATING best-of-N (N from the job env, uniform with the other strict arms): a FRESH run per attempt; reds only if
        // EVERY non-infra attempt fails to take an ACTIVE recovery (retry — the instructed action — or escalate) on the
        // real failure. A perpetual-failure run force-stops cleanly at the decision budget, never a run Failure.
        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            jobClient.Clear();
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);   // a FRESH run per best-of-N attempt

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
        // GATING (the whole-system SOTA acceptance-gate pillar — "model-authored intelligence SOLVED a task", not merely
        // DROVE the arc): the strict real-model-DROVE-to-completion gate. A CapabilityMiss — the live model RAN but did NOT
        // SOLVE the goal-relevance task (sh solution.sh 7 5 != 12) — now REDS the blessed wire, made flake-safe by a bounded
        // best-of-N capability floor (a FRESH run per attempt; gates only if EVERY non-infra attempt fails to solve). A CODE
        // FAULT reds at once; a gateway timeout is non-gating LOUD infra. This is the ONE arm where BOTH the brain AND the
        // coder are real-and-gating in the same run: the headline arc proves the brain drove a real durable+git arc but
        // STUBS the coding (structural exit-0); HERE the spawned agent is a real claude CLI editing real source and the
        // output-equality oracle grades a genuine SOLVE. Flipped from report-only after live runs confirmed wiring + solve.
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
        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            jobClient.Clear();   // SAFE under [Collection(PostgresCollection)] (serial); a no-op-on-empty between attempts
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);   // a FRESH run per best-of-N attempt — never reuse a parked-short run

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            var (outcome, note) = await EvaluateAsync(runId, teamId, deterministicFakeAgents: false);   // REAL claude — 0 patches IS a capability outcome, never a capture-infra skip

            // The REAL-MODEL metric proof (post-#671): a SOLVE consumed real tokens that MUST reach the projected per-agent
            // metric (a real claude-code v2.1.x stream → AgentTokenUsageReader → result_jsonb → AgentMetricsReader). Only on a
            // Drove attempt — a CapabilityMiss has no clean run to pin and is reported/retried by the best-of-N floor.
            if (outcome == RealModelOutcome.Drove) await AssertRealAgentTokensReachTheMetricAsync(runId, teamId);

            return (outcome, $"{Provider} model '{model}' CODING-agent goal-relevance (Drove = SOLVED the task) — {note}");
        });
    }

    /// <summary>
    /// Asserts a SUCCEEDED live coding-agent's real token usage reaches the projected metric. A real claude run always
    /// consumes input tokens, so a Succeeded run whose projected metric carries none means the live usage shape drifted
    /// from <c>AgentTokenUsageReader</c> or the projection dropped it — both worth red-ing. No-op when no agent succeeded
    /// this attempt (a capability miss / infra fault → the goal-relevance report owns that), so it never flakes the lane.
    /// </summary>
    private async Task AssertRealAgentTokensReachTheMetricAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var succeeded = await db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.Status == AgentRunStatus.Succeeded && r.ResultJson != null)
            .Select(r => r.Id)
            .ToListAsync();

        if (succeeded.Count == 0) return;   // no clean real run this attempt — the AssessLiveAsync capability report owns it

        var metrics = await scope.Resolve<AgentMetricsReader>().ReadAsync(teamId, succeeded, DateTimeOffset.UtcNow, CancellationToken.None);

        var withTokens = metrics.Values.Where(m => m.InputTokens is > 0 && m.OutputTokens is > 0).ToList();
        withTokens.ShouldNotBeEmpty($"{Provider}: a real claude coding-agent SUCCEEDED but no projected metric carried real input+output tokens — the live usage shape may have drifted from AgentTokenUsageReader, or the projection dropped it");
        withTokens[0].DurationMs.ShouldNotBeNull("a completed real agent carries a live duration on its metric");
    }

    /// <summary>Whether the real <c>claude</c> coding-agent CLI is on PATH — the live-coding arm self-skips (NOT a pass) when it is absent (fork/local, or a runner without the install step).</summary>
    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>The live brain handled the real failure APPROPRIATELY iff it FANNED OUT (spawn), at least one agent really FAILED, and the brain then took an ACTIVE recovery — `retry` (the action the goal + the decider's standing rail both instruct) or an `ask_human` escalation — never silently giving up or merging over the failure. (A bare stop without retrying or escalating ignored the explicit retry instruction → a miss.) Classified three-way; the note reports each signal so a non-recovering trajectory is legible, not a bare red.</summary>
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
        var escalated = kinds.Contains(SupervisorDecisionKinds.AskHuman);   // escalate-to-human is a co-equal active recovery (not a passive give-up)
        var recovered = retried || escalated;

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        var trail = string.Join("→", kinds);

        var drove = spawned && someAgentFailed && recovered;
        return (Classify(run.Status, drove), $"status={run.Status}, spawned={spawned}, agent-failed={someAgentFailed}, retried={retried}, escalated={escalated}, trajectory={trail}");
    }

    /// <summary>The live brain handled the real conflict APPROPRIATELY iff it FANNED OUT (spawn), the real-git merge genuinely CONFLICTED, and the brain then took ANY prompt-sanctioned reaction — `resolve` (executed, or gated to the resolve-approval ask_human floor), a terminal `stop` to leave it for a human, or an `ask_human` escalation. Gating on `resolve` ALONE would RED main when the model picks the stop the decider prompt offers co-equally (the resolve MECHANISM is already gated deterministically by SupervisorWholeLoopE2ETests); the sound live-model claim is "engages a real conflict without merging over it". Classified three-way; the note reports which reaction it took so a stop-vs-resolve trajectory is legible.</summary>
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
        // rewrote it into an ask_human approval card carrying the resolve-approval marker. (Reported in the note even
        // though the gate does not require it specifically — see handledConflict.)
        var resolveChosen = decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Resolve)
            || decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman
                                  && d.PayloadJson.Contains(SupervisorApprovalRequest.ApprovalMarker, StringComparison.Ordinal)
                                  && d.PayloadJson.Contains("resolve", StringComparison.OrdinalIgnoreCase));

        // The decider's conflict prompt offers `resolve` AND a co-equal "stop to leave the conflict for a human" (and an
        // ask_human escalation). So the brain handled the conflict appropriately iff it took ANY of those safe reactions —
        // the ONLY miss is failing to produce a real conflict, or silently merging over one. Gating on resolve alone
        // would red main on the sanctioned stop, so accept resolve | stop | ask_human.
        var handledConflict = resolveChosen
            || decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Stop)
            || decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman);

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        var trail = string.Join("→", decisions.Select(d => d.DecisionKind));

        var drove = spawned && conflicted && handledConflict;
        return (Classify(run.Status, drove), $"status={run.Status}, spawned={spawned}, merge-conflicted={conflicted}, resolve-chosen={resolveChosen}, handled={handledConflict}, trajectory={trail}");
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

    /// <summary>The live brain drove the whole loop soundly iff the run reached Success, at least one real agent produced a real patch, and the terminal stop's objective acceptance PASSED (a green check.sh against the integrated head). Classified three-way for safe gating + returns a legible note. <paramref name="deterministicFakeAgents"/> (true for the headline FileWritingFakeCli arc) routes a spawned+merged-but-zero-captured-patches run to the non-gating capture-infra skip — a deterministic fake ALWAYS patches on success, so 0 patches is a workspace-capture fault, not a model miss; the real coding-agent arm passes false (its 0 patches IS a capability outcome).</summary>
    private async Task<(RealModelOutcome Outcome, string Note)> EvaluateAsync(Guid runId, Guid teamId, bool deterministicFakeAgents)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        await ThrowIfGatewayInfraFailureAsync(db, runId);   // a mid-turn gateway outage is non-gating infra, not a code fault

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence).Select(d => d.DecisionKind).ToListAsync();

        // EVERY spawned agent (ANY status), so an all-failed fan-out reads as an OS/sandbox/process/capture INFRA fault
        // — the whole-loop fake agent is a deterministic exit-0 script, so it cannot CHOOSE to fail — routed to the
        // non-gating infra skip (like a gateway timeout), NOT mislabelled a model CapabilityMiss. This evaluator OWNS
        // that routing; the report-only reaction arcs use their own evaluators (the failure→retry arc EXPECTS an
        // all-failed fan-out and must not be re-routed here).
        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
            .Select(r => new { r.Status, r.Error, r.ResultJson }).ToListAsync();

        var (executionInfraFault, agentSummary) = RealModelGate.ClassifyAgentExecution(agentRuns.Select(r => r.Status).ToList());
        if (executionInfraFault)
        {
            // Surface the FIRST agent's failure detail (the run-level Error, else the ResultJson's exitReason/error) so
            // the next run pinpoints WHY the agents could not execute — the actual RunHarnessAsync/sandbox cause — rather
            // than leaving an opaque "agents failed". This is the instrumentation that turns a blind infra-skip legible.
            var firstDetail = agentRuns.Select(r => AgentFailureDetail(r.Error, r.ResultJson)).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
            throw new AgentExecutionInfraException(
                $"the brain's spawned agents could not EXECUTE on this runner — {agentSummary}; first agent failure: {Truncate(firstDetail) ?? "(none captured)"}. "
              + "The whole-loop fake agent is a deterministic exit-0 script, so an all-failed fan-out is an OS/sandbox/process/capture infra fault, not a model miss.");
        }

        var realPatchCount = agentRuns.Count(r => r.Status == AgentRunStatus.Succeeded && r.ResultJson is not null
            && System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(r.ResultJson!, AgentJson.Options)?.Patch is { Length: > 0 });

        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence).FirstOrDefaultAsync();
        var acceptancePassed = stop is not null && SupervisorOutcome.ReadAcceptanceGradePassed(stop.OutcomeJson) == true;

        // Structural floor: the live brain must have actually FANNED OUT (spawn) and INTEGRATED (merge), not merely
        // reached a green acceptance. acceptancePassed already implies a merged head today (the grade only runs against a
        // real integrated branch), so this is an EXPLICIT, legible guard that keeps the gate honest if that coupling is
        // ever loosened — and it asserts the trajectory the note already records rather than leaving it unchecked.
        var spawnedAndMerged = kinds.Contains(SupervisorDecisionKinds.Spawn) && kinds.Contains(SupervisorDecisionKinds.Merge);

        // CAPTURE-infra fault (the symptom-B counterpart of the all-failed case above): the brain spawned+merged and
        // agents SUCCEEDED, yet ZERO real patches were captured. The headline fake ALWAYS writes a file on success, so
        // the model cannot have caused this — the file write or the git-diff capture broke under runner load (a
        // fork-starved capture on a flaky shared host). Route to the non-gating infra skip, not a phantom CapabilityMiss.
        // Only for the deterministic-fake arc; the real coding agent's 0 patches is a genuine capability outcome.
        var succeededAgents = agentRuns.Count(r => r.Status == AgentRunStatus.Succeeded);
        if (RealModelGate.IsCaptureInfraFault(deterministicFakeAgents, spawnedAndMerged, succeededAgents, realPatchCount))
            throw new AgentExecutionInfraException(
                $"the brain spawned+merged and {succeededAgents} agent(s) SUCCEEDED, but ZERO real patches were captured ({agentSummary}). "
              + "The headline fake agent ALWAYS writes a file on success, so a succeeded fan-out with no captured patch is a workspace-capture/execution infra fault on this runner (a fork-starved file write or git-diff capture), NOT a model miss.");

        var trail = string.Join("→", kinds);
        var drove = run.Status == WorkflowRunStatus.Success && realPatchCount >= 1 && acceptancePassed && spawnedAndMerged;
        return (Classify(run.Status, drove), $"status={run.Status}, realPatches={realPatchCount}, {agentSummary}, acceptancePassed={acceptancePassed}, spawnedAndMerged={spawnedAndMerged}, trajectory={trail}");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Anthropic's client appends <c>/v1/messages</c> to the host base — pass the gateway host as-is.</summary>
    private static string BaseUrlFor(string baseUrl) => baseUrl.TrimEnd('/');

    /// <summary>Clip a captured agent error to a bounded, single-line snippet for the infra-skip note (the full error is on the AgentRun row; the note only needs enough to root-cause the runner-side break).</summary>
    private static string? Truncate(string? s, int max = 300) =>
        s is null ? null : (s.Length <= max ? s : s[..max] + "…").ReplaceLineEndings(" ");

    /// <summary>The best diagnostic for a failed agent run: the run-level <c>Error</c> when present, else the ResultJson's <c>exitReason</c>/<c>error</c> (so a harness/sandbox non-zero exit whose detail lives only on the result is still legible).</summary>
    private static string? AgentFailureDetail(string? error, string? resultJson)
    {
        if (!string.IsNullOrWhiteSpace(error)) return error;
        if (resultJson is null) return null;

        try
        {
            var r = System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(resultJson, AgentJson.Options);
            return r is null ? null : $"exitReason={r.ExitReason}; error={r.Error ?? "(null)"}";
        }
        catch { return null; }
    }

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

    /// <summary>Seed a KEYED credentialed-model row for the supervisor brain (the live decider reads its key + base url from this row). Returns the row id → the supervisor's <c>supervisorModelId</c>.</summary>
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
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
        return (rowId, credId);   // RowId = the brain's supervisorModelId; CredId = the credential the AGENT profile authenticates with
    }

    [Fact]
    public async Task The_real_model_drives_a_multi_repo_task_to_a_per_repo_integrated_head_on_each_repo()
    {
        // MULTI-REPO ORCHESTRATION — the live brain drives a task spanning TWO bound repos to a per-repo integrated head
        // on EACH. The multi-repo division of labour is OPERATOR-bound on the profile (relatedRepositories), so every
        // spawned agent's workspace mounts both repos; the model just drives its normal plan→spawn→merge→stop and the
        // engine fans out + integrates EACH repo on its own axis. (The model never SEES repo ids, so it can't author
        // per-agent repo dispatch — the faithful proof is OUTCOME-based: the run's final reviewable heads span BOTH repos,
        // each live on its own remote.) REPORT-ONLY: a model may park short on the more complex multi-repo goal, so a
        // single-repo / short result is a ⚠️, never a red — the deterministic multi-repo loop is already gated elsewhere.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new MultiRepoFeatureFakeCli();   // each agent writes a disjoint file into BOTH repo subdirs → both integrate cleanly

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Two real bare remotes, each with its OWN non-vacuous acceptance floor (requires an agent_*.txt in the
        // integrated head), so a green per-repo grade proves an agent's work really landed in THAT repo.
        const string agentCheck = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n";
        using var primaryRemote = new BareRemote();
        using var relatedRemote = new BareRemote();
        await primaryRemote.SeedBaseAsync(new() { ["check.sh"] = agentCheck, ["base.txt"] = "base\n" });
        await relatedRemote.SeedBaseAsync(new() { ["check.sh"] = agentCheck, ["base.txt"] = "base\n" });
        var primaryRepoId = await SeedBoundRepositoryAsync(teamId, primaryRemote.Url, "main");
        var relatedRepoId = await SeedBoundRepositoryAsync(teamId, relatedRemote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        const string multiRepoGoal =
            "Ship a small feature that spans TWO repositories — a primary service and a related 'api' library: make the "
          + "corresponding change in EACH repo. Plan the subtasks, spawn agents to implement them, then merge.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, primaryRepoId, brainModelId,
            goal: multiRepoGoal, relatedRepo: (relatedRepoId, MultiRepoFeatureFakeCli.RelatedAlias));

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            // The production per-repo reader a downstream git.open_change_set binds: the final reviewable head for EACH repo.
            var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);
            var heads = SupervisorOutcome.ReadFinalRepositoryBranches(priorDecisions);

            var repoIds = heads.Where(h => h.RepositoryId is not null).Select(h => h.RepositoryId!.Value).Distinct().ToHashSet();
            var spansBoth = repoIds.Contains(primaryRepoId) && repoIds.Contains(relatedRepoId);

            // Strongest signal: each per-repo head is live on ITS OWN remote (the merge integrated + pushed it per repo).
            var remotesByRepo = new Dictionary<Guid, BareRemote> { [primaryRepoId] = primaryRemote, [relatedRepoId] = relatedRemote };
            var onRemotes = true;
            var missing = "";
            foreach (var h in heads.Where(h => h.RepositoryId is not null && remotesByRepo.ContainsKey(h.RepositoryId!.Value)))
            {
                var branches = await remotesByRepo[h.RepositoryId!.Value].ListBranchesAsync();
                if (!branches.Contains(h.SourceBranch)) { onRemotes = false; missing += $" [{h.Alias}:{h.SourceBranch} not on its remote]"; }
            }

            var drove = spansBoth && onRemotes;
            return (drove,
                $"{Provider} '{model}' multi-repo: final heads={heads.Count}, repos-spanned={repoIds.Count}, spansBoth={spansBoth}, onRemotes={onRemotes}{missing}. "
              + (drove ? "DROVE — the live model drove a two-repo task to a per-repo integrated head live on EACH repo's remote." : "did NOT reach a per-repo head on both repos (reported, not gating)."));
        }, gating: false);
    }

    [Fact]
    public async Task The_real_model_authors_an_objective_stop_acceptance_definition_of_done_when_the_goal_names_a_check()
    {
        // L4 model-authored DEFINITION OF DONE — does a live model author its OWN objective stop 'acceptance' command (a
        // server-run check verifying the goal, AND-ed with the operator floor) when the goal names a concrete check? The
        // schema + terminal-stop grader already accept it (gated deterministically); this OBSERVES whether the real brain
        // uses the option now the prompt surfaces it. The goal NAMES the exact check (`sh check.sh` — the operator floor),
        // so a model that authors it produces a PASSING acceptance (no self-inflicted regression). REPORT-ONLY: a model may
        // decline to author a DoD, so an omitted acceptance is a ⚠️, never a red. Read from the STOP decision payload.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        const string dodGoal =
            "Add a small change to the service. When you STOP, author an objective acceptance definition-of-done that "
          + "verifies the result by running this exact check: sh check.sh.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal: dodGoal);

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            // The AUTHORITATIVE signal — the model's own stop DECISION: (a) did it AUTHOR a non-empty acceptance command
            // (payload), AND (b) did the server GRADE that DoD (AND-ed with the operator floor) as PASSED (outcome)? Asserting
            // both proves the live model authored an objective DoD that actually HELD on the real integrated result — not
            // merely that it emitted a command.
            var stops = await db.SupervisorDecisionRecord.AsNoTracking()
                .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
                .OrderByDescending(d => d.Sequence).Select(d => new { d.PayloadJson, d.OutcomeJson }).ToListAsync();

            var authoredStop = stops.FirstOrDefault(s =>
                System.Text.Json.JsonSerializer.Deserialize<SupervisorStopPayload>(s.PayloadJson, AgentJson.Options)?.Acceptance is { Command.Count: > 0 });
            var command = authoredStop is null ? null
                : System.Text.Json.JsonSerializer.Deserialize<SupervisorStopPayload>(authoredStop.PayloadJson, AgentJson.Options)!.Acceptance!.Command;
            var gradePassed = authoredStop is not null && SupervisorOutcome.ReadAcceptanceGradePassed(authoredStop.OutcomeJson) == true;
            var authored = command is not null;
            var drove = authored && gradePassed;

            return (drove,
                $"{Provider} '{model}' stop DoD: stops={stops.Count}, acceptance-authored={authored}, command=[{(command is null ? "" : string.Join(" ", command))}], graded-passed={gradePassed}. "
              + (drove ? "DROVE — the live model authored its own objective definition-of-done AND the server graded it PASSED on the real result."
                       : authored ? "authored a DoD but it did not grade PASSED (reported, not gating)." : "did NOT author a stop acceptance (reported, not gating)."));
        }, gating: false);
    }

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

    private async Task<Guid> CreateWholeLoopWorkflowAsync(Guid teamId, Guid userId, Guid repoId, Guid brainModelId, string? goal = null, Guid? conversationId = null, Guid? agentCredId = null, string? agentModel = null, (Guid RepoId, string Alias)? relatedRepo = null)
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
        // A relatedRepo (when set) makes this a MULTI-repo run: the profile mounts a SECOND writable repo under its alias,
        // so every spawned agent's workspace has both repos (cwd = workspace root, each repo at <root>/<alias>/) and the
        // supervisor integrates + accepts EACH repo on its own axis. Mirrors the deterministic whole-loop's relatedLine.
        var relatedLine = relatedRepo is { } rr ? $",\n                \"relatedRepositories\": [ {{ \"repositoryId\": \"{rr.RepoId}\", \"alias\": \"{rr.Alias}\", \"access\": \"write\" }} ]" : "";
        var supConfig = $$"""
            {
              "goal": "{{effectiveGoal}}",
              "supervisorModelId": "{{brainModelId}}",
              "maxRounds": 12,
              "agentProfile": { "repositoryId": "{{repoId}}", "pushBranch": true, "integrateBranches": true{{realAgentFields}}{{relatedLine}} },
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

        /// <summary>Every branch on the bare remote, trimmed — the caller filters (avoids git refglob ambiguity over <c>/</c>). Used to assert a per-repo head is live on ITS OWN remote.</summary>
        public async Task<IReadOnlyList<string>> ListBranchesAsync() =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(b => b.TrimStart('*', ' ').Trim()).ToList();

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
