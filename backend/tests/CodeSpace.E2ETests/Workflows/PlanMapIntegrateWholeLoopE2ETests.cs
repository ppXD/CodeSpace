using System.Text.Json;
using System.Text.RegularExpressions;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE plan-map integrated-candidate whole-loop E2E (P4): the PRODUCTION plan-map-synth graph — real
/// <c>plan.author</c> → real <c>flow.map</c> fan-out → REAL OS-process agents that clone a REAL bare remote,
/// write REAL files, and push REAL per-item branches → the <c>git.integrate_run</c> step REALLY integrates both
/// patches onto one reviewable branch on that remote → the synth narrates → the terminal surfaces the candidate.
/// Until this arm, every repo-bound plan-map E2E ran the integrate step and asserted NOTHING about it — the
/// whole point of the step (ONE reviewable head instead of K fragments) was silently unproven.
///
/// <para>Fidelity (Rule 12) — HIGH: real engine + real Postgres + real projection builder + real
/// <c>AgentRunExecutor</c>/<c>LocalProcessRunner</c> + real git clone/push/apply. Deterministic fakes only at
/// the planner LLM (the work-plan script's default two-item plan), the synth LLM (provider retarget), and the
/// CLI's intelligence (<see cref="FileWritingFakeCli"/> — each item writes its own goal-slugged file, so the
/// integrated tree provably carries BOTH items' work). POSIX-only; skips when git is absent.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class PlanMapIntegrateWholeLoopE2ETests
{
    private const string SeedGoal = "Improve the module across both fronts";

    private readonly PostgresFixture _fixture;

    public PlanMapIntegrateWholeLoopE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_repo_bound_fan_out_integrates_both_items_onto_one_reviewable_branch()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitAvailableAsync()) return;    // real git required for clone/push/integrate

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        using var cli = new FileWritingFakeCli();

        using var remote = new BareRemote();
        await remote.SeedBaseAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var runId = await ProjectAndStartAsync(teamId, userId, plannerRowId, repoId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, $"the whole loop must land Success — error: {run.Error}");

        // Both fan-out items really produced and pushed their own branch (the integrate step's inputs exist).
        var agentManifests = await db.PublishManifest.AsNoTracking()
            .Where(m => m.WorkflowRunId == runId && m.Kind == PublishManifestKind.Agent).ToListAsync();
        agentManifests.Count.ShouldBe(2, "two plan items ⇒ two per-agent pushes");
        agentManifests.ShouldAllBe(m => m.PublishStateValue == PublishState.Pushed);

        // THE CANDIDATE — one reviewable branch on the REAL remote, its tree carrying BOTH items' files.
        var integrationBranch = $"codespace/integration/{runId:N}";
        (await remote.RemoteHasBranchAsync(integrationBranch)).ShouldBeTrue(
            $"the integrate step must push the run's unique integrated candidate; remote branches: [{string.Join(", ", await remote.ListBranchesAsync())}]");

        (await remote.BranchFileContentAsync(integrationBranch, FileWritingFakeCli.FileFor("do the first thing")))
            .ShouldContain("do the first thing", customMessage: "the integrated tree carries item 1's work");
        (await remote.BranchFileContentAsync(integrationBranch, FileWritingFakeCli.FileFor("do the second thing")))
            .ShouldContain("do the second thing", customMessage: "…and item 2's — ONE head, not fragments");

        // The durable candidate fact: the run-level Integration manifest row.
        var candidate = (await db.PublishManifest.AsNoTracking()
            .Where(m => m.WorkflowRunId == runId && m.Kind == PublishManifestKind.Integration).ToListAsync()).ShouldHaveSingleItem();
        candidate.Branch.ShouldBe(integrationBranch);
        candidate.PublishStateValue.ShouldBe(PublishState.Pushed);

        // The run's own outputs surface the candidate beside the narrated reduce.
        var outputs = JsonDocument.Parse(run.OutputsJson!).RootElement;
        outputs.GetProperty("integrationStatus").GetString().ShouldBe("Clean");
        outputs.GetProperty("integratedBranch").GetString().ShouldBe(integrationBranch);
        outputs.GetProperty("combined").GetString().ShouldNotBeNullOrWhiteSpace("the synth still narrates — the code reduce rides beside it, not instead of it");

        // …and what the synth was SHOWN: the integrate node's own account of what landed. The fake synth echoes its
        // prompt, so `combined` is the output of the real node → VariableResolver → llm.complete path, not a re-derivation.
        outputs.GetProperty("combined").GetString()!.ShouldMatch(LandedBoth(integrationBranch) + @"$",
            customMessage: "the reduce is handed what actually landed — which contributions, each beside the subtask number that places it among the results — in one factual sentence");
    }

    /// <summary>
    /// The failure arm of the same loop: ONE item flunks its objective contract while its sibling succeeds. Under
    /// the <c>flow.map</c> schema's <c>terminate</c> default this killed the map — which SKIPPED
    /// <c>git.integrate_run</c> and the reduce, so the surviving item's real, pushed work never became a reviewable
    /// candidate and the run's outputs were empty. With the projection declaring <c>continue</c>, the map finishes,
    /// the integrate step runs over the run's publish ledger, and the reduce narrates with the failure counted.
    ///
    /// <para>Which contributions integrate is a LEDGER question with exactly ONE verdict gate
    /// (<c>RunIntegrationContributions</c>): a unit whose own definition-of-done REJECTED it (or whose verification a
    /// human waived) is withheld from the candidate, while a unit that merely ended badly but captured a diff still
    /// contributes. This test pins the part that was broken — the candidate exists at all, with the SUCCEEDED sibling's
    /// work in its tree and the flunked unit's work off it — and that the reduce is TOLD the flunked unit was withheld,
    /// by name, instead of narrating a whole deliverable over a candidate that lacks it.</para>
    /// </summary>
    [Fact]
    public async Task A_flunked_item_still_leaves_its_siblings_work_on_one_reviewable_candidate()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        // s2 carries an objective acceptance whose command does not exist in the seeded tree → it flunks for real.
        using (var knob = _fixture.BeginScope()) knob.Resolve<WorkPlanPlanScript>().AuthorContract = true;

        try
        {
            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
            var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

            using var cli = new FileWritingFakeCli();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync();
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;

            var runId = await ProjectAndStartAsync(teamId, userId, plannerRowId, repoId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

            run.Status.ShouldBe(WorkflowRunStatus.Success,
                customMessage: $"one flunked item must not sink the whole fan-out — error: {run.Error}");
            run.Outcome.ShouldBe(WorkflowRunOutcomes.PartialFailure, "the surviving branch is delivered, but the run must persist that the result is partial");

            var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();

            // ONE flunked UNIT, not one flunked run: the map body carries the transient-failure retry policy, so the
            // contract item respawns a fresh agent per attempt and leaves a Failed row per attempt (all on the same
            // map#i cell). The sibling is the single Succeeded row.
            agentRuns.Count(r => r.Status == AgentRunStatus.Failed).ShouldBeGreaterThan(0,
                customMessage: "the contract item really flunked — this arm is worthless if both items simply passed");
            agentRuns.Where(r => r.Status == AgentRunStatus.Failed).Select(r => r.IterationKey).Distinct().Count().ShouldBe(1,
                customMessage: "every failed row must be an ATTEMPT of the SAME map branch — a second failed branch would mean the sibling flunked too and the test proves nothing about surviving work");
            agentRuns.Count(r => r.Status == AgentRunStatus.Succeeded).ShouldBe(1,
                customMessage: "the sibling item really ran and succeeded — that is the work terminate-mode used to discard");

            var outputs = JsonDocument.Parse(run.OutputsJson!).RootElement;

            outputs.GetProperty(WorkflowOutputKeys.MapCount).GetInt32().ShouldBe(2);
            outputs.GetProperty(WorkflowOutputKeys.MapFailed).GetInt32().ShouldBe(1, "the run row counts the failure beside the answer it qualifies");

            var integrationBranch = $"codespace/integration/{runId:N}";
            outputs.GetProperty("integratedBranch").GetString().ShouldBe(integrationBranch,
                customMessage: "the integrate step RAN — under terminate the map's failure skipped it entirely and the produced work stayed fragments");

            (await remote.BranchFileContentAsync(integrationBranch, FileWritingFakeCli.FileFor("do the first thing")))
                .ShouldContain("do the first thing", customMessage: "the surviving item's real work is on the candidate — exactly what one sibling's failure used to discard");

            // …and the head invariant, from the other side: the FLUNKED item wrote a real file and pushed it, but its
            // own definition-of-done rejected it, so its work must NOT be on the branch a human reviews as the run's
            // candidate (RunIntegrationContributions withholds a Failed/Waived manifest row — the same rule the deep
            // lane's merge/resolver/publish doors enforce). Under terminate this could never be asserted, because
            // integrate never ran after a failure.
            (await remote.BranchHasFileAsync(integrationBranch, FileWritingFakeCli.FileFor("do the second thing")))
                .ShouldBeFalse(customMessage: "the flunked item's work must be withheld from the candidate — continue-on-error must not turn 'keep the siblings' into 'ship the rejected work'");

            var combined = outputs.GetProperty("combined").GetString();

            combined.ShouldNotBeNullOrWhiteSpace("the reduce ran too — the run narrates instead of dying at the map");

            // What the synth was SHOWN: the flunked unit never reached the integrator, so the integration outcome alone
            // would read as a clean, complete candidate. The integrate node names it as withheld — the label is the unit
            // id of the failed attempts' shared (node, iteration) cell, read off the ledger rather than assumed.
            var flunked = agentRuns.First(r => r.Status == AgentRunStatus.Failed);
            var flunkedLabel = Core.Services.Agents.AgentAcceptanceContract.UnitId(flunked.NodeId, flunked.IterationKey ?? "");

            flunkedLabel.ShouldBe("agent#map#1", "fixture check: the contract belongs to the plan's second item, so the subtask number below is its 1-based position");
            combined.ShouldContain($"Integration outcome:\nIntegration: 1 contribution(s) landed on {integrationBranch}: subtask 1 (agent#map#0). Withheld before integration: subtask 2 ({flunkedLabel}) — acceptance Failed.",
                customMessage: "the reduce must be told the flunked unit was withheld from the candidate — without it the candidate reads as the whole deliverable");
        }
        finally
        {
            using var reset = _fixture.BeginScope();
            reset.Resolve<WorkPlanPlanScript>().Reset();
        }
    }

    /// <summary>
    /// The OUTPUT review's half of the head invariant, through the same whole loop: the agent profile configures a Gate
    /// review, and the honest critic flags the item whose change carries the planted hack. The executor pushes that
    /// item's branch BEFORE the review runs, and its manifest row was written before the review too — so it reads Pushed
    /// with no failed verdict of its own, and <c>git.integrate_run</c> integrated it onto the reviewable candidate while
    /// the supervisor lane withheld the very same unit (PROBE_R3). The flagged item's work must not be on the candidate;
    /// the approved sibling's must.
    /// </summary>
    [Fact]
    public async Task A_flagged_item_is_withheld_from_the_reviewable_candidate()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        const string clean = "do the first thing";
        const string flagged = "do the second thing " + DeterministicCriticLlmClient.RejectMarker;

        using (var knob = _fixture.BeginScope()) knob.Resolve<WorkPlanPlanScript>().Instructions = new[] { clean, flagged };

        try
        {
            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
            var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);
            var (_, criticRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "critic-model", provider: DeterministicCriticLlmClient.ProviderTag);

            using var cli = new FileWritingFakeCli();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync();
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;

            var runId = await ProjectAndStartAsync(teamId, userId, plannerRowId, repoId, review: (ReviewMode.Gate, criticRowId));

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(WorkflowRunStatus.Success, $"one flagged item must not sink the fan-out — error: {run.Error}");

            var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId && !r.IterationKey.EndsWith("#review")).ToListAsync();
            var results = agentRuns.Where(r => r.ResultJson != null).Select(r => JsonSerializer.Deserialize<AgentRunResult>(r.ResultJson!, Core.Services.Agents.AgentJson.Options)!).ToList();

            results.ShouldContain(r => r.ExitReason == "output-flagged" && r.ProducedBranch != null, "fixture check: the critic really flagged an item whose branch was already pushed");
            results.ShouldContain(r => r.OutputReview == OutputReviewState.Approved, "fixture check: the sibling was really reviewed and approved");

            var integrationBranch = $"codespace/integration/{runId:N}";
            JsonDocument.Parse(run.OutputsJson!).RootElement.GetProperty("integratedBranch").GetString().ShouldBe(integrationBranch, "the integrate step ran past the flagged item");

            (await remote.BranchFileContentAsync(integrationBranch, FileWritingFakeCli.FileFor(clean))).ShouldContain(clean, customMessage: "the approved item's work is on the candidate");
            (await remote.BranchHasFileAsync(integrationBranch, FileWritingFakeCli.FileFor(flagged)))
                .ShouldBeFalse(customMessage: "the reviewer flagged this item — its pushed branch must not reach the branch a human reviews as the run's candidate");

            // …and what the synth was SHOWN. The flagged item never reached the integrator, so the integration outcome
            // alone would read as a clean, complete candidate: it is named as withheld, in fixed words — the critic's own
            // critique is model-authored text derived from the agent's output and must stay out of the prompt. The label is
            // read off the ledger, not assumed.
            var flaggedRun = agentRuns.First(r => JsonSerializer.Deserialize<AgentRunResult>(r.ResultJson!, Core.Services.Agents.AgentJson.Options)!.ExitReason == "output-flagged");
            var flaggedResult = JsonSerializer.Deserialize<AgentRunResult>(flaggedRun.ResultJson!, Core.Services.Agents.AgentJson.Options)!;

            Core.Services.Agents.AgentAcceptanceContract.UnitId(flaggedRun.NodeId, flaggedRun.IterationKey ?? "").ShouldBe("agent#map#1", "fixture check: the planted hack is the plan's second item");
            flaggedResult.ReviewFeedback.ShouldNotBeNullOrWhiteSpace("fixture check: the critic really wrote a critique, so its absence from the prompt below proves something");

            var combined = JsonDocument.Parse(run.OutputsJson!).RootElement.GetProperty("combined").GetString()!;

            combined.ShouldContain($"Integration outcome:\nIntegration: 1 contribution(s) landed on {integrationBranch}: subtask 1 (agent#map#0). Withheld before integration: subtask 2 (agent#map#1) — output review flagged it.",
                customMessage: "the reduce must be told the flagged item was withheld from the candidate — without it the candidate reads as the whole deliverable");
            combined.ShouldNotContain(flaggedResult.ReviewFeedback!, customMessage: "the critic's critique is model-authored text — the account carries only the fixed reason");
        }
        finally
        {
            using var reset = _fixture.BeginScope();
            reset.Resolve<WorkPlanPlanScript>().Reset();
        }
    }

    [Fact]
    public async Task A_conflicted_candidate_parks_for_review_and_resumes_to_an_honest_finish()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        // Two items steered onto the SAME file with different content — their patches REALLY conflict on apply.
        using (var knob = _fixture.BeginScope())
            knob.Resolve<WorkPlanPlanScript>().Instructions = new[] { "update the alpha side", "update the beta side" };

        try
        {
            using var cli = new ConflictThenResolveFakeCli();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync(ConflictThenResolveFakeCli.SharedFile);
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;

            var runId = await ProjectAndStartAsync(teamId, userId, plannerRowId, repoId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var mid = _fixture.BeginScope();
            var db = mid.Resolve<CodeSpaceDbContext>();

            // "conflict ⇒ park": the run is Suspended on a REAL approval wait whose payload names the conflict.
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "a conflicted candidate must park for a human, never narrate past silently");

            var wait = await db.WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval);
            wait.PayloadJson!.ShouldContain(ConflictThenResolveFakeCli.SharedFile, customMessage: "the wait payload names the conflicted file — the review is actionable off the run surface");

            // The human ships the fragments (reject) — the resumed pass re-integrates (still conflicted: nothing
            // changed on the remote) and the run finishes HONESTLY: Success, Conflicted candidate, review trail.
            (await mid.Resolve<Core.Services.Workflows.IWorkflowService>().ApproveRunAsync(runId, teamId, userId, approved: false, comment: "ship the fragments", CancellationToken.None))
                .ShouldBeTrue("the run-level approve verb resolves the integrate node's park");

            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

            run.Status.ShouldBe(WorkflowRunStatus.Success, $"after the review the run finishes — error: {run.Error}");

            var outputs = JsonDocument.Parse(run.OutputsJson!).RootElement;
            outputs.GetProperty("integrationStatus").GetString().ShouldBe("Conflicted", "the candidate stays honestly conflicted — reviewed, never laundered");
            outputs.GetProperty("integratedBranch").ValueKind.ShouldBe(JsonValueKind.Null);

            (await verify.Resolve<CodeSpaceDbContext>().PublishManifest.AsNoTracking()
                .Where(m => m.WorkflowRunId == runId && m.Kind == PublishManifestKind.Integration).AnyAsync())
                .ShouldBeFalse("no clean candidate ⇒ no candidate row");

            (await remote.RemoteHasBranchAsync($"codespace/integration/{runId:N}")).ShouldBeFalse("nothing was pushed for a conflicted set — the fragments stay the only branches");

            // What the synth was SHOWN. The two items run in parallel, so which agent run is created — and therefore
            // applied first — is not fixed, and neither is WHICH unit conflicts. It is read from ground truth instead:
            // the fallback branch the park itself named, mapped back to its unit through the publish ledger.
            var conflictedBranch = JsonDocument.Parse(wait.PayloadJson!).RootElement.GetProperty("fallbackBranches")[0].GetString()!;
            var conflictedLabel = await UnitLabelOfBranchAsync(verify.Resolve<CodeSpaceDbContext>(), runId, conflictedBranch);
            var combined = outputs.GetProperty("combined").GetString()!;

            combined.ShouldContain("Integration outcome:\nIntegration conflicted: no integrated branch was published",
                customMessage: "the reduce is told the candidate conflicted and that nothing landed on an integrated branch");
            var position = int.Parse(conflictedLabel[(conflictedLabel.LastIndexOf('#') + 1)..]) + 1;

            combined.ShouldContain($"Conflicted or held back: subtask {position} ({conflictedLabel}) → {conflictedBranch}",
                customMessage: "…and which contribution conflicted, with the branch that still keeps its work — the fragments the reviewer was asked about");
            combined.ShouldEndWith(" A reviewer rejected the retry.", customMessage: "the human review reaches the reduce as a fixed sentence — this run's reviewer rejected the retry");
            combined.ShouldNotContain("ship the fragments", customMessage: "the reviewer's free-text comment is a person's words on their way into a model's prompt — the account states only the verdict");
            combined.ShouldNotContain($"landed on codespace/integration/{runId:N}",
                customMessage: "a conflicted set published no branch, so the reduce must never be handed one as where the work landed");
        }
        finally
        {
            using var reset = _fixture.BeginScope();
            reset.Resolve<WorkPlanPlanScript>().Reset();
        }
    }

    /// <summary>
    /// The failure a clean integration hides: ONE subtask edits its file and then DIES (an agent that crashed or timed
    /// out after editing). Its run is a real <c>Failed</c> one, yet the executor still captured its diff and wrote its
    /// manifest row; with no acceptance contract it is ungraded, so it is never withheld — and the integration comes out
    /// Clean with that partial, unverified diff ON the candidate. The synth saw <c>[A, {"error": ...}]</c> and "2 landed"
    /// and called the failed subtask missing. It must be told WHICH subtask landed although its own run failed.
    /// </summary>
    [Fact]
    public async Task A_subtask_that_dies_after_editing_lands_on_the_candidate_flagged_as_partial_and_unverified()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var crashing = $"do the second thing then {PartialWorkFakeCli.CrashMarker}";

        using (var knob = _fixture.BeginScope())
            knob.Resolve<WorkPlanPlanScript>().Instructions = new[] { "do the first thing", crashing };

        try
        {
            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
            var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

            using var cli = new PartialWorkFakeCli();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync();
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;

            var runId = await ProjectAndStartAsync(teamId, userId, plannerRowId, repoId, immediateRetries: true);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(WorkflowRunStatus.Success, $"one dead subtask must not sink the fan-out — error: {run.Error}");

            var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
            var failed = agentRuns.Where(r => r.Status == AgentRunStatus.Failed).ToList();

            failed.ShouldNotBeEmpty("fixture check: the crashing subtask's agent run really failed — otherwise this arm proves nothing");
            failed.Select(r => r.IterationKey).Distinct().ShouldHaveSingleItem("every failed row is an attempt of the SAME subtask");

            var failedLabel = Core.Services.Agents.AgentAcceptanceContract.UnitId(failed[0].NodeId, failed[0].IterationKey ?? "");
            failedLabel.ShouldBe("agent#map#1", "fixture check: the crashing instruction is the plan's second item");

            var outputs = JsonDocument.Parse(run.OutputsJson!).RootElement;
            var integrationBranch = $"codespace/integration/{runId:N}";

            outputs.GetProperty("integrationStatus").GetString().ShouldBe("Clean", "an ungraded failed subtask is never withheld — the integration comes out Clean");
            (await remote.BranchHasFileAsync(integrationBranch, FileWritingFakeCli.FileFor(crashing)))
                .ShouldBeTrue("fixture check: the dead subtask's partial diff really is ON the candidate — the honest account has to say so");

            outputs.GetProperty("combined").GetString()!.ShouldMatch(
                LandedBoth(integrationBranch) + Regex.Escape(" Landed although its subtask failed (partial, unverified): subtask 2 (agent#map#1).") + "$",
                customMessage: "the reduce must be told WHICH subtask landed although its own run failed — else the failed subtask reads as missing while its partial diff is on the branch");
        }
        finally
        {
            using var reset = _fixture.BeginScope();
            reset.Resolve<WorkPlanPlanScript>().Reset();
        }
    }

    /// <summary>
    /// What the synth is shown when BOTH of the plan's two items landed, up to the clause after it: the contributions are
    /// applied in agent-run creation order, and two parallel branches are created in no fixed order — so the assertion pins
    /// WHICH landed (each beside the subtask number that places it), not the order they are listed in.
    /// </summary>
    private static string LandedBoth(string integrationBranch)
    {
        const string first = @"subtask 1 \(agent#map#0\)";
        const string second = @"subtask 2 \(agent#map#1\)";

        return $@"Integration outcome:\nIntegration: 2 contribution\(s\) landed on {Regex.Escape(integrationBranch)}: ({first}, {second}|{second}, {first})\.";
    }

    /// <summary>The unit label the integration outcome names a contribution by, found from ground truth: the agent attempt that pushed <paramref name="branch"/> (the run's publish ledger), then that agent run's (node, iteration) cell.</summary>
    private static async Task<string> UnitLabelOfBranchAsync(CodeSpaceDbContext db, Guid runId, string branch)
    {
        var manifest = await db.PublishManifest.AsNoTracking().SingleAsync(m => m.WorkflowRunId == runId && m.Kind == PublishManifestKind.Agent && m.Branch == branch);
        var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == manifest.AgentRunId);

        return Core.Services.Agents.AgentAcceptanceContract.UnitId(agentRun.NodeId, agentRun.IterationKey ?? "");
    }

    // ─── Projection (the production builder, planner pinned to the work-plan fake, synth retargeted) ───

    private async Task<Guid> ProjectAndStartAsync(Guid teamId, Guid userId, Guid plannerRowId, Guid repoId, bool immediateRetries = false, (ReviewMode Mode, Guid ReviewerRowId)? review = null)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            Route = new RoutePlan { RecipeKind = TaskRecipeKinds.MapFanout, ProjectionKind = TaskProjectionKinds.PlanMapSynth, Caps = new RouteCaps() },
            // Standard, not Confined: each item WRITES a file for the integration to merge, and a Confined agent's workspace is mounted read-only wherever the sandbox confines.
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Standard", RepositoryId = repoId, OutputReviewMode = review?.Mode ?? ReviewMode.None, ReviewerModelId = review?.ReviewerRowId },
            PlannerModelRowId = plannerRowId,
        };

        var built = RetargetSynth(scope.Resolve<ITaskProjectionRegistry>().Resolve(context.Route.ProjectionKind).Build(context));
        var definition = immediateRetries ? WithoutRetryBackoff(built) : built;

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    /// <summary>The retry POLICY stays — a crashed subtask is respawned, each attempt leaving its own row, which is the multi-attempt shape the partial-work arm must survive — but the engine WAITS the 30 s backoff for real, twice per dying subtask, so the arm zeroes the wait and nothing else.</summary>
    private static WorkflowDefinition WithoutRetryBackoff(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(n => n.Retry is { } retry ? n with { Retry = retry with { BackoffSeconds = 0 } } : n).ToList(),
    };

    private static WorkflowDefinition RetargetSynth(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(n => n.Id == "synth" ? RetargetProvider(n, DeterministicSynthLlmClient.ProviderTag) : n).ToList(),
    };

    private static NodeDefinition RetargetProvider(NodeDefinition node, string providerTag)
    {
        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["provider"] = JsonSerializer.SerializeToElement(providerTag);

        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }

    // ─── Seeding / plumbing (the repo-bound whole-loop recipe) ───

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "agent-clone-token" })), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-planmap-integrate-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;
        private readonly CodeSpace.E2ETests.Infrastructure.GitTestRemoteServer _server;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _server = new CodeSpace.E2ETests.Infrastructure.GitTestRemoteServer(_root);
        }

        public string Url => _server.Url;

        public async Task SeedBaseAsync(string? extraFile = null)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            await Git(_root, "--git-dir", _bare, "config", "http.receivepack", "true");

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "base.txt"), "base\n");
            if (extraFile is not null) await File.WriteAllTextAsync(Path.Combine(seed, extraFile), "base\n");
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Contains(branch);

        public async Task<IReadOnlyList<string>> ListBranchesAsync() =>
            (await Git(_root, "--git-dir", _bare, "branch", "--format=%(refname:short)"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public async Task<string> BranchFileContentAsync(string branch, string file) =>
            await Git(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        /// <summary>Whether a branch's tree carries a path — <c>ls-tree</c> rather than <c>show</c>, because the absence of a file is a legitimate ASSERTION here and <c>git show branch:missing</c> exits non-zero (which the Git helper turns into a test error instead of a clean false).</summary>
        public async Task<bool> BranchHasFileAsync(string branch, string file) =>
            (await Git(_root, "--git-dir", _bare, "ls-tree", "--name-only", branch, "--", file)).Contains(file);

        private static Task<string> Git(string cwd, params string[] args) =>
            CodeSpace.E2ETests.Infrastructure.GitTestRemoteServer.RunFixtureGitAsync(cwd, args);

        public void Dispose()
        {
            _server.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
