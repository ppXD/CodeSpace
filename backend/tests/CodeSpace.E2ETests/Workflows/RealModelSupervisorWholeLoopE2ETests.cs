using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Learning;
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

    public RealModelSupervisorWholeLoopE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;

        // The only THROWABLE mutation (the DI resolve that flips the decider) runs FIRST, so a ctor throw leaks no
        // process-global; the env-var set (which cannot throw) follows. Dispose restores both.
        SetDeciderMode(useLiveModel: true);
    }

    public void Dispose()
    {

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = false;   // restore the shared-fixture default for siblings
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [SkippableFact]
    public async Task The_real_model_drives_the_whole_loop_to_an_integrated_accepted_patch()
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        // Fail-closed on a PARTIAL secret config: all three absent → honest fork/local skip; some-but-not-all present
        // is a broken/rotated/renamed secret that would otherwise self-skip the BLESSED gate GREEN having driven no live
        // brain at all — so throw to turn that masked-nothing into a RED main run instead of a false green.
        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass: surfaced loudly as NOT EVALUATED
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip the blessed whole-loop gate GREEN proving nothing.");

        if (OperatingSystem.IsWindows()) return;                          // the fake CLI is a /bin/sh script
        if (!await GitReadyAsync()) return;                              // real git is required for clone/capture/integrate

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        // The surface an ask PARKS on. Without it every ask degrades to a self-advancing NULL answer that nobody can
        // ever answer, so nine of them walked the run into the no-progress forced stop with acceptance never run — the
        // gate then measured the absence of a human rather than the model's completion (real-model run 33723910434).
        var conversationId = await SeedConversationAsync(teamId, userId);

        using var remote = new BareRemote();
        // A NON-VACUOUS acceptance floor: the integrated head must actually CONTAIN an agent's work (an agent_*.txt that
        // FileWritingFakeCli writes), so a green grade proves the brain's spawn really integrated — not just that an
        // exit-0 script ran against an empty tree. If the integration carried no agent file, check.sh exits 1 → the stop's
        // objective acceptance FAILS → acceptancePassed=false → the run does not read as Drove.
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        // The supervisor's brain runs on this seeded credential (key encrypted into the DB row the live decider reads).
        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        // P2b canary (live-brain leg): the headline arc runs ENFORCED — a Drove verdict now implies the terminal
        // authority arbitrated the live model's own contract ledger to CleanSuccess. C5: the opt-in is deliberately
        // NOT stamped here any more — a supervisor definition resolves Enforced BY DEFAULT, so this arm measures the
        // DEFAULT cohort (what a real operator's run actually gets), not a mode only this test ever asked for.
        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, conversationId: conversationId);

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

            await AssertRanEnforcedAsync(runId);
            await DriveUntilSettledAsync(runId);

            // Play the operator on every card the brain parked on, so the arc can reach its acceptance floor. This is
            // what turns the headline gate back into a measurement of COMPLETION: before it, the criterion
            // (a real patch + a PASSED acceptance grade) could only be met by a model that never asked anything.
            var (answeredAsks, skippedAmendAsks) = await AnswerParkedAsksAsync(runId, teamId, userId);

            var (outcome, note) = await EvaluateAsync(runId, teamId, FileWritingFakeCli.StubbedHarnessKinds);   // headline arc = FileWritingFakeCli (always patches on success — but ONLY on the harness it arms)

            // D3 (Arc D): a Drove round must ALSO clear the north-star floor — the release gate reads the same
            // M-1 number the operator's scorecard renders, through the real reducer chain.
            if (outcome == RealModelOutcome.Drove) await AssertNorthStarClearsFloorAsync(teamId, runId);

            return (outcome, $"{Provider} model '{model}' whole-loop — {note}, answeredAsks={answeredAsks}, skippedAmendAsks={skippedAmendAsks}");
        });
    }

    /// <summary>The literal header <c>LlmSupervisorDecider</c> prints above the injected lesson bullets — the observable proof the treatment reached the prompt, not merely the ledger.</summary>
    private const string LessonPromptHeader = "Lessons distilled from this team's prior failed runs";

    /// <summary>
    /// REPORT-ONLY (non-gating): the first live exercise of the Arc-D lesson A/B <c>injected</c> arm. Every real-model
    /// lane so far seeded no <c>Lesson</c> row, so <c>LessonArms.For</c> saw an empty window and returned
    /// <c>none</c> — outside the experiment. The arm was recorded on every decision row and the injection code path
    /// had never once run against a real brain.
    ///
    /// <para>This case seeds one current lesson, deliberately picks a goal that HASHES into the treatment arm, drives
    /// the loop, and then observes two facts: the run's decision rows read <c>injected</c>, and a turn prompt actually
    /// carried the seeded lesson's own rendered line. It is INFORMATIONAL by design (<c>gating: false</c>) — it
    /// measures whether the treatment reaches the brain, not whether the brain then solves the task, so a live model
    /// that converges some other way must never red main.</para>
    /// </summary>
    [SkippableFact]
    public async Task The_real_model_runs_under_the_injected_lesson_arm_and_its_prompt_carries_the_lesson()
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        // THE intervention: one CURRENT lesson in this team's ledger. Without a row the window is empty, the arm is
        // `none`, and the prompt is byte-identical — which is exactly why the injected path had never run live.
        var lessonLine = await SeedLessonAsync(teamId);

        // The arm is a pure hash of (team, undecorated goal) — no toggle, no randomness — and the team id is fresh
        // per run, so the only deliberate way into the treatment is to pick a goal that hashes there. Asserted
        // BEFORE the run so a mis-picked goal reds as a plumbing fault instead of passing silently at arm=withheld.
        var goal = InjectedArmGoal(teamId);
        LessonArms.Assign(teamId, goal).ShouldBe(LessonArms.Injected, "the case measures the TREATMENT — outside the injected arm it observes nothing");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal);

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await DriveUntilSettledAsync(runId);

            var (ok, note) = await ObserveLessonInjectionAsync(runId, teamId, lessonLine);

            return (ok, $"{Provider} model '{model}' lesson-arm probe — {note}");
        }, gating: false);
    }

    /// <summary>
    /// Seed ONE current lesson for the team and hand back the exact line <c>LessonArms.Line</c> will render for it —
    /// rendered through the production helper, never a hand-copied string, so the prompt check cannot pass against a
    /// format the decider does not actually print. Distinctive wording so the line is unmistakable in a long prompt.
    /// </summary>
    private async Task<string> SeedLessonAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var lesson = new Lesson
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Mode = CodeSpace.Core.Services.Completion.RunModeKeys.Supervisor,
            FailureClass = "invented-acceptance-command",
            WhatFailed = "a subtask authored an acceptance command this repository has no tooling for",
            Why = "the check was invented from the task text instead of read off the repository",
            HowToApply = "author every acceptance check as the repository's own seeded gate",
            SourceRunIds = [Guid.NewGuid()],
            DistilledByModel = "a4-lesson-arm-probe",
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
        };

        db.Lesson.Add(lesson);
        await db.SaveChangesAsync();

        return LessonArms.Line(lesson);
    }

    /// <summary>
    /// A goal that hashes into <see cref="LessonArms.Injected"/> for THIS team. The assignment is a fair coin per
    /// distinct goal string, so a bounded search finds one almost immediately; exhausting it means the hash is
    /// broken, not that the run was unlucky. Every variant keeps the oracle anchor the other arms carry — without
    /// it a live model authors an acceptance command the marker-file fakes can never satisfy, and the run parks
    /// short for a reason that has nothing to do with lessons.
    /// </summary>
    private static string InjectedArmGoal(Guid teamId)
    {
        const string oracleAnchor = " For EVERY subtask, author its acceptance check as exactly the command `sh check.sh` (the repository's own seeded gate) "
                                  + "— this repository has NO other test tooling, so any other acceptance command will fail regardless of the work.";

        for (var variant = 0; variant < 64; variant++)
        {
            var goal = $"Add server-side email-format validation to the signup endpoint (probe {variant})." + oracleAnchor;

            if (LessonArms.Assign(teamId, goal) == LessonArms.Injected) return goal;
        }

        throw new InvalidOperationException("no goal variant hashed into the injected arm in 64 tries — at ~50% per variant that is a broken assignment hash, not bad luck");
    }

    /// <summary>
    /// The two observations: (1) every decision row the run recorded reads the <c>injected</c> arm, and (2) a turn
    /// prompt actually carried the seeded lesson's own rendered line. The second is the one that matters — an arm
    /// recorded on the tape while the prompt stayed byte-identical would be a measurement of nothing, and no test
    /// before this looked at the prompt at all.
    ///
    /// <para>A prompt whose user text was OFFLOADED to the artifact store carries no inline copy; that is reported
    /// as an honest "could not inspect", never quietly counted as a pass.</para>
    /// </summary>
    private async Task<(bool Ok, string Verdict)> ObserveLessonInjectionAsync(Guid runId, Guid teamId, string lessonLine)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var arms = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.TeamId == teamId && d.SupervisorRunId == runId)
            .OrderBy(d => d.Sequence)
            .Select(d => d.LessonArm)
            .ToListAsync();

        if (arms.Count == 0) return (false, "the run recorded no supervisor decision at all — there is no arm to observe");

        var recorded = arms.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList();

        if (recorded.Count != 1 || recorded[0] != LessonArms.Injected)
            return (false, $"decision rows carry arm(s) [{string.Join(", ", arms.Select(a => a ?? "(null)"))}] — every row should read '{LessonArms.Injected}' (the arm is frozen per RUN, not per turn)");

        var payloads = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.InteractionStarted)
            .Select(r => r.PayloadJson)
            .ToListAsync();

        var inlinePrompts = payloads.Select(InlineUserPrompt).Where(p => p is not null).Select(p => p!).ToList();

        if (inlinePrompts.Count == 0)
            return (false, $"arm reads '{LessonArms.Injected}' on {arms.Count} row(s), but every decision prompt's user text was offloaded to the artifact store — the injection itself could not be inspected from the ledger");

        var carrying = inlinePrompts.Count(p => p.Contains(LessonPromptHeader, StringComparison.Ordinal));

        if (carrying == 0)
            return (false, $"arm reads '{LessonArms.Injected}' but NOT ONE of {inlinePrompts.Count} inspectable turn prompts carried the lesson header — the arm is recorded and the treatment never reached the brain");

        if (!inlinePrompts.Any(p => p.Contains(lessonLine, StringComparison.Ordinal)))
            return (false, $"the lesson header rode {carrying} prompt(s) but the seeded lesson's own rendered line did not — the injected evidence is not the ledger's lesson");

        return (true, $"arm='{LessonArms.Injected}' on {arms.Count} decision row(s); {carrying} of {inlinePrompts.Count} inspectable turn prompts carried the seeded lesson line");
    }

    /// <summary>One <c>interaction.started</c> payload's INLINE user prompt, or null when the record offloaded it to the artifact store (a <c>$artifact_id</c> object rather than a string) or is unreadable.</summary>
    private static string? InlineUserPrompt(string payloadJson)
    {
        try
        {
            var root = JsonDocument.Parse(payloadJson).RootElement;

            if (!root.TryGetProperty("prompt", out var prompt) || !prompt.TryGetProperty("user", out var user)) return null;

            return user.ValueKind == JsonValueKind.String ? user.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [SkippableFact]
    public async Task The_real_model_repairs_a_broken_oracle_through_the_cosign_loop()
    {
        // B6 — the run-96695645 re-enactment, the amend-acceptance arc's end-to-end proof: a live brain meets a
        // unit whose acceptance check CANNOT RUN (a missing gate binary — the deterministic broken oracle; exit-127
        // classifies infra since B6's POSIX-code fix, and a local direct-exec start-throw lands grade-error — both
        // infra, so the arm behaves identically under bubblewrap and bare runners), proposes amend_acceptance
        // instead of retrying into it (B3 verb, B4 precondition), the TEST plays the human co-signer on the parked
        // card, and the retry the run then owes (B5 obligation) re-grades under the co-signed fallback and drives
        // the arc to a green accept head. GATING best-of-N — the whole repair loop, not any single rung.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        var conversationId = await SeedConversationAsync(teamId, userId);   // the surface the amend co-sign card parks on

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        // The goal PLANTS the broken oracle (a gate binary this repository does not have) and names the honest
        // repair channel — re-enacting 96695645's shape: the oracle, not the work, is the binding constraint.
        const string reenactGoal =
            "Implement a small feature in ONE subtask. Author its acceptance check as exactly the command `run-quality-gate` "
          + "(the repository's standard gate binary). If the server's grade later reports that the gate itself CANNOT RUN "
          + "(a grade-error or command-not-found failure), the repository's fallback gate is `sh check.sh` — propose an "
          + "acceptance amendment (kind amend_acceptance) switching this subtask's check to the fallback, and never retry "
          + "a unit into a check that cannot run.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, reenactGoal, conversationId);

        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            jobClient.Clear();
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await DriveUntilSettledAsync(runId);

            // The co-sign loop — the test is the human: approve each parked AMEND card (and only amend cards — a
            // content ask is the model converging some other way, which the evaluator below reads honestly).
            // Bounded: a live brain that keeps amending past this is a miss, not a hang.
            for (var round = 0; round < 4 && await ApproveParkedAmendCardAsync(runId, teamId, userId); round++)
                await DrainUntilSettledAsync(runId);   // the co-signed retry can itself park on a plane blip — ride it, don't score it a miss

            var (outcome, note) = await EvaluateOracleRepairAsync(runId, teamId, FileWritingFakeCli.StubbedHarnessKinds);
            return (outcome, $"{Provider} model '{model}' oracle-repair — {note}");
        });
    }

    /// <summary>Approve the run's newest parked ask IFF it is an amend co-sign card (payload carries the structured amend node) — the test playing the human. False = nothing parked, or the parked ask is not an amend card (never blind-approve a content ask).</summary>
    /// <summary>An ask payload whose question carries either stop-gate prefix (I3 publish / DC-2b delivery) — the park-half detector the no-stop arm of the I3 audit keys on.</summary>
    private static bool IsGateCardQuestion(string? payloadJson)
    {
        try
        {
            var question = System.Text.Json.JsonDocument.Parse(payloadJson ?? "{}").RootElement.TryGetProperty("question", out var q) ? q.GetString() : null;

            return question is not null
                && (question.StartsWith(SupervisorPublishGate.QuestionPrefix, StringComparison.Ordinal) || question.StartsWith(SupervisorDeliveryGate.QuestionPrefix, StringComparison.Ordinal));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The UNATTENDED operator surface, bounded. A live brain that asks a question parks the run on a durable card and
    /// waits; in an environment with nobody to answer, every ask self-advances with a null answer, each one increments
    /// the no-progress streak, and the ninth trips <c>SupervisorLane.DefaultMaxNoProgressDecisions</c> into a forced stop
    /// — so acceptance never runs and the headline gate measured the ABSENCE OF A HUMAN, not the model's completion (the
    /// arc <c>plan→spawn→spawn→merge→ask_human×9</c>, three attempts running, real-model run 33723910434). This answers
    /// each parked card the way an operator would, through the SAME production service the Room's answer endpoint calls,
    /// and lets the resume re-dispatch — an answered ask resets the streak, so a converging run is no longer starved.
    /// It stops short of the ONE card family a script must never rule on: an amend co-sign, where the brain proposes to
    /// rewrite or waive the very oracle its acceptance grade is measured against.
    ///
    /// <para>Bounded by <see cref="UnattendedAskResponder.MaxAnsweredAsks"/>: a brain that keeps asking past it is a
    /// genuine capability miss (the evaluator scores whatever terminal the run actually reached), never a hang. An
    /// <c>amend_acceptance</c> co-sign card is never answered — it stays parked for a real human — so the counts come
    /// back as a pair and BOTH ride the verdict line: an attempt that needed nine answers, or that stopped at an oracle
    /// amendment, stays legible even when it passes.</para>
    /// </summary>
    private Task<(int Answered, int LeftForAHuman)> AnswerParkedAsksAsync(Guid runId, Guid teamId, Guid userId) =>
        UnattendedAskResponder.AnswerAllAsync(answer => AnswerParkedAskAsync(runId, teamId, userId, answer), () => DrainUntilSettledAsync(runId));

    /// <summary>Dispose of the run's newest parked ask through the SAME production service the Room's answer endpoint calls, playing the human — EXCEPT an amend co-sign card, which is left parked (this arm must never co-sign the brain's own rewrite of the oracle it is about to be graded against). <see cref="ParkedAskDisposition.NothingParked"/> = no token-bearing unanswered ask, which is how the responder's loop terminates.</summary>
    private async Task<ParkedAskDisposition> AnswerParkedAskAsync(Guid runId, Guid teamId, Guid userId, string answer)
    {
        using var scope = _fixture.BeginScope();

        var newestAsk = await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.AskHuman)
            .OrderByDescending(d => d.Sequence).Select(d => d.PayloadJson).FirstOrDefaultAsync();

        if (UnattendedAskResponder.MustLeaveForAHuman(newestAsk)) return ParkedAskDisposition.LeftForAHuman;

        var outcome = await scope.Resolve<ISupervisorAskAnswerService>().AnswerAsync(runId, teamId, userId, answer, UnattendedAskResponder.ApprovalVerdict, CancellationToken.None);

        return outcome is { Resumed: true } ? ParkedAskDisposition.Answered : ParkedAskDisposition.NothingParked;
    }

    private async Task<bool> ApproveParkedAmendCardAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var lastAsk = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.AskHuman)
            .OrderByDescending(d => d.Sequence).FirstOrDefaultAsync();

        if (lastAsk is null || SupervisorAmendAcceptance.ReadAmend(lastAsk.PayloadJson) is null) return false;

        var outcome = await scope.Resolve<ISupervisorAskAnswerService>()
            .AnswerAsync(runId, teamId, userId, "approve — the fallback gate is the right check", SupervisorAnswerDecision.Approve, CancellationToken.None);

        return outcome is { Resumed: true };
    }

    /// <summary>The oracle-repair verdict: Drove = the model USED the co-sign channel (an approved amend card on the tape) AND the arc still reached the green accept head (the stop floor graded PASSED on the integrated tree). A run that converged without ever amending is a miss for THIS arm (it tests the repair loop, not general convergence).</summary>
    private async Task<(RealModelOutcome Outcome, string Note)> EvaluateOracleRepairAsync(Guid runId, Guid teamId, IReadOnlyList<string> stubbedHarnessKinds)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        await ThrowIfGatewayInfraFailureAsync(db, runId);

        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
            .Select(r => new { r.Status, r.Error, r.ResultJson, r.Harness }).ToListAsync();

        var (lostControl, harnessCensus) = RealModelGate.ClassifyHarnessControl(agentRuns.Select(r => r.Harness).ToList(), stubbedHarnessKinds);
        if (lostControl)
            return (RealModelOutcome.CodeFault, $"the arm lost control of its agents: it arms [{string.Join(", ", stubbedHarnessKinds)}] but the run dispatched {harnessCensus}.");

        var (executionInfraFault, agentSummary) = RealModelGate.ClassifyAgentExecution(agentRuns.Select(r => r.Status).ToList());
        if (executionInfraFault)
        {
            var firstDetail = agentRuns.Select(r => AgentFailureDetail(r.Error, r.ResultJson)).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
            throw new AgentExecutionInfraException($"the spawned agents could not EXECUTE on this runner — {agentSummary}; first agent failure: {Truncate(firstDetail) ?? "(none captured)"}.");
        }

        var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);

        var amendCards = priorDecisions.Count(SupervisorAmendAcceptance.IsAmendCard);
        var approvedAmends = priorDecisions.Count(SupervisorAmendAcceptance.IsApprovedAmendCard);

        var stop = priorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
        var acceptancePassed = stop is not null && SupervisorOutcome.ReadAcceptanceGradePassed(stop.OutcomeJson) == true;

        var kindTrail = string.Join("→", priorDecisions.Select(d => d.DecisionKind));
        var note = $"amendCards={amendCards}, approved={approvedAmends}, acceptancePassed={acceptancePassed}, agents={agentSummary}, trajectory=[{kindTrail}]";

        return (approvedAmends > 0 && acceptancePassed ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss, note);
    }

    [SkippableFact]
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
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

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

            await DriveUntilSettledAsync(runId);

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

    [SkippableFact]
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
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

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

            await DriveUntilSettledAsync(runId);

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

    [SkippableFact]
    public async Task The_real_model_authors_a_dependent_subtask_whose_agent_actually_sees_its_producers_work()
    {
        // S1 HANDOFF — re-enacting run 28fec923 (a dependent subtask's fresh clone of the repository DEFAULT branch
        // never saw its producer's committed work) against a LIVE brain. Does a live model, told the dependsOn field
        // exists, actually AUTHOR it for a genuinely sequential two-step task? REPORT-ONLY on authorship (a model may
        // decline the field — reported, never gating, mirroring the dispatch/phases/DoD authorship arms above). But
        // WHEN the model DOES author a dependency, the handoff MECHANISM is asserted HARD (Shouldly, bypassing the
        // soft report-only gate): a live model correctly declaring the dependency while the dependent's clone still
        // misses the producer's work would be a genuine CODE regression in the S1 staging resolver
        // (RealSupervisorActionExecutor.DependencyStaging.cs), not a model-capability question, so it fails the build
        // outright regardless of this arm's report-only status. The deterministic mechanism itself (0/1/≥2 producers,
        // fail-closed, conflict→resolve) is already proven exhaustively by SupervisorDependencyStagingFlowTests
        // (real Postgres + real git); this arm's job is ONLY to prove a live brain reaches for the feature at all,
        // and that when it does, the real substrate behind it does not regress.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new DependencyHandoffFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nif ls agent_*.txt >/dev/null 2>&1; then exit 0; else exit 1; fi\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        // The ORACLE ANCHOR in the last sentence is load-bearing (P5 finding, run 31230410920): a live model left
        // free authors its own per-subtask acceptance ("run the tests") that the deterministic fake — which only
        // writes marker files — can never satisfy. S1 then grades acceptance-REJECTED, the dependency frontier
        // faithfully reports S2 "blocked (waiting on S1)", and the model rationally retries S1 / replans until the
        // no-progress stop ("Supervisor merged 0 prior agent result(s)" is the fingerprint: succeeded + captured,
        // yet nothing mergeable). The arm exists to test the HANDOFF mechanism, not oracle authorship — anchoring
        // the acceptance to the seeded floor removes the model-luck coin flip that made this arm intermittent.
        const string handoffGoal =
            "Implement a small feature in exactly TWO STRICTLY SEQUENTIAL subtasks: the second subtask BUILDS DIRECTLY "
          + "on the first subtask's committed code and must not start until the first has actually completed. When you "
          + "PLAN, author the second subtask's dependsOn as the first subtask's id, so the platform stages the second "
          + "agent from the first agent's actual produced branch instead of a fresh clone. For EVERY subtask, author "
          + "its acceptance check as exactly the command `sh check.sh` (the repository's own seeded gate) — this "
          + "repository has NO other test tooling, so any other acceptance command will fail regardless of the work.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal: handoffGoal);

        // REPORT-ONLY on authorship (gating:false) — but the mechanism, when exercised, is asserted hard inside the
        // evaluator (see above), so a real regression still fails this test regardless of the outer gate.
        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await DriveUntilSettledAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            // The AUTHORITATIVE signal — the model's own PLAN decision payload: did it declare a dependsOn edge at all?
            // Read the LATEST plan ONLY, because that is the one the runtime rail reads: SupervisorDependencyGate's
            // DependsOnBySubtask walks the prior decisions BACKWARDS and returns at the first Plan it finds. Scanning
            // every plan ever recorded armed this hard assert off an edge a later re-plan had already dropped — the
            // rail disarmed, the assert did not, and the failure text still blamed the staging resolver. A run that
            // re-plans (the live brain re-planned THREE times in run 30775218538) is the common case, not the corner.
            var latestPlanPayload = await db.SupervisorDecisionRecord.AsNoTracking()
                .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Plan)
                .OrderByDescending(d => d.Sequence).Select(d => d.PayloadJson).FirstOrDefaultAsync();

            var authoredDependency = (latestPlanPayload is null
                    ? Enumerable.Empty<Messages.Agents.SupervisorPlannedSubtask>()
                    : System.Text.Json.JsonSerializer.Deserialize<Messages.Agents.SupervisorPlanPayload>(latestPlanPayload, AgentJson.Options)?.Subtasks
                      ?? Enumerable.Empty<Messages.Agents.SupervisorPlannedSubtask>())
                .Any(s => s.DependsOn is { Count: > 0 });

            // The MECHANISM signal: did ANY agent's captured diff include the dependent marker — only ever written by
            // a clone whose workspace ALREADY contained the producer's marker (DependencyHandoffFakeCli's own contract).
            var resultJsons = await db.AgentRun.AsNoTracking()
                .Where(r => r.WorkflowRunId == runId && r.ResultJson != null)
                .Select(r => r.ResultJson!)
                .ToListAsync();

            var handoffWorked = resultJsons
                .Select(j => System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(j, AgentJson.Options))
                .Any(r => r?.ChangedFiles.Contains(DependencyHandoffFakeCli.DependentMarker) == true);

            if (authoredDependency)
                handoffWorked.ShouldBeTrue(
                    $"{Provider} '{model}' authored a dependsOn edge in its plan, but no agent's captured diff shows the S1 handoff marker "
                  + $"('{DependencyHandoffFakeCli.DependentMarker}') — the dependent's clone did not actually see its producer's committed work. "
                  + "This is a CODE regression in the dependency-staging resolver, not a model-capability gap.");

            return (authoredDependency,
                $"{Provider} '{model}' S1 handoff: authored-dependsOn={authoredDependency}, handoff-marker-seen={handoffWorked}. "
              + (authoredDependency ? "DROVE — the live model declared the dependency AND the dependent agent's clone genuinely carried the producer's work."
                                     : "did NOT author a dependsOn edge for this goal (reported, not gating — the staging mechanism itself is proven deterministically by SupervisorDependencyStagingFlowTests)."));
        }, gating: false);
    }

    [SkippableFact]
    public async Task The_real_model_authors_a_read_only_subtask_acceptance_that_grades_not_applicable()
    {
        // S2 READ-ONLY ACCEPTANCE — the model-authored proof that a per-subtask acceptance contract on a subtask
        // expecting NO changes grades NOT-APPLICABLE (a vacuous pass), never the pre-S2 fail-closed "no-branch-or-repo".
        // REPORT-ONLY on whether the live model actually exercises the feature (authors a per-subtask 'acceptance' AND
        // phrases the subtask so the server's verb-based inference — SupervisorSubtaskExpectations.Resolve, the SAME
        // production call GradeUnitAcceptanceAsync's caller uses — resolves to "no changes expected"): a model may
        // decline either half, so a miss is a ⚠️, never a red (mirrors the dispatch/phases/dependsOn/stop-DoD authorship
        // arms above). But WHEN the model DOES exercise it, the MECHANISM is asserted HARD (Shouldly, bypassing the
        // report-only gate): a live model correctly declaring "no changes expected" on a subtask it also gave an
        // acceptance contract to must grade not-applicable, not fail-closed — a miss there is a genuine S2 code
        // regression (SupervisorTurnService.Rehydrate.cs's GradeUnitAcceptanceAsync / NotApplicableOrFailed), not a
        // model-capability question, so it fails the build outright regardless of this arm's report-only status. The
        // deterministic mechanism itself (branch / patch-fallback / not-applicable / fail-closed) is already proven
        // exhaustively by SupervisorUnitAcceptanceFoldFlowTests + SupervisorAcceptanceFoldFlowTests (real Postgres +
        // real git); this arm's job is ONLY to prove a live brain reaches for the feature at all, and that when it
        // does, the real substrate behind it does not regress.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new InvestigateOnlyFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

        using var remote = new BareRemote();
        // The RUN-LEVEL operator floor is deliberately UNCONDITIONAL (exit 0) — this arm's subtask makes no changes
        // by design, so a file-presence floor (as the other arms use) would conflate the run-level stop gate with
        // the per-subtask grade this test actually verifies.
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        const string readOnlyGoal =
            "This task requires ONLY investigation, no code changes at all: analyze the repository and report what "
          + "you find. Plan this as EXACTLY ONE subtask whose instruction begins with the word 'Investigate', and do "
          + "not modify any files. On that subtask, author an 'acceptance' definition-of-done that runs this exact "
          + "command: sh check.sh — so its completion is objectively verified even though it produces no diff.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal: readOnlyGoal);

        // REPORT-ONLY on authorship (gating:false) — but the mechanism, when exercised, is asserted hard inside the
        // evaluator (see above), so a real regression still fails this test regardless of the outer gate.
        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await DriveUntilSettledAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);

            var plan = priorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);
            var subtasks = plan is null ? Array.Empty<Messages.Agents.SupervisorPlannedSubtask>() : SupervisorOutcome.ReadPlanSubtasks(plan.PayloadJson).ToArray();

            // The AUTHORITATIVE compound signal: a subtask that BOTH declared its own acceptance contract AND
            // resolves — via the SAME production call GradeUnitAcceptanceAsync's caller uses — to "no changes expected".
            var readOnlyContractSubtask = subtasks.FirstOrDefault(s => s.Acceptance is not null && !SupervisorSubtaskExpectations.Resolve(s));
            var authored = readOnlyContractSubtask is not null;

            var note = "no subtask authored a read-only acceptance contract";

            if (authored)
            {
                var unitResult = priorDecisions
                    .Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
                    .SelectMany(d => UnitSubtaskIdsFor(d).Zip(SupervisorOutcome.ReadAgentResults(d.OutcomeJson), (id, r) => (SubtaskId: id, Result: r)))
                    .FirstOrDefault(pair => pair.SubtaskId == readOnlyContractSubtask!.Id).Result;

                unitResult.ShouldNotBeNull(
                    $"{Provider} '{model}' authored a read-only acceptance contract on subtask '{readOnlyContractSubtask!.Id}' but no folded agent result carries that subtask id — check the positional spawn/retry join.");

                unitResult!.AcceptancePassed.ShouldBe(true,
                    $"{Provider} '{model}' declared subtask '{readOnlyContractSubtask.Id}' expects no changes AND gave it an acceptance contract, but the fold did not grade it a vacuous pass (detail: {unitResult.AcceptanceDetail}) — a genuine S2 regression, not a model-capability gap.");
                unitResult.AcceptanceDetail.ShouldStartWith("not-applicable:", customMessage:
                    $"{Provider} '{model}': the S2 no-changes-expected fold must fold a not-applicable detail, never the pre-S2 fail-closed reason.");

                note = $"subtask '{readOnlyContractSubtask.Id}' ({Truncate(readOnlyContractSubtask.Instruction, 60)}) → acceptancePassed={unitResult.AcceptancePassed}, detail={unitResult.AcceptanceDetail}";
            }

            return (authored,
                $"{Provider} '{model}' S2 read-only acceptance: {note}. "
              + (authored ? "DROVE — the live model authored a read-only per-subtask acceptance contract AND the S2 fold graded it not-applicable, never fail-closed."
                           : "did NOT author a read-only per-subtask acceptance contract for this goal (reported, not gating — the fold mechanism itself is proven deterministically by SupervisorUnitAcceptanceFoldFlowTests)."));
        }, gating: false);
    }

    /// <summary>The subtask id(s) a spawn (positional, the fan-out order) or a retry (one) ran — a test-local mirror of <c>SupervisorTurnService.Rehydrate.UnitSubtaskIds</c> built purely off the PUBLIC <see cref="SupervisorOutcome"/> accessors, so the test reads the same authoritative signal production folding does.</summary>
    private static IReadOnlyList<string> UnitSubtaskIdsFor(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson)
            : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();

    [SkippableFact]
    public async Task The_real_model_never_reaches_a_terminal_stop_with_accepted_unpublished_work()
    {
        // I3 (publish-or-park) — the STRUCTURAL floor, proven against a REAL live-model run: unlike the dispatch /
        // phases / DoD / S1-handoff / S2 authorship arms above, I3 is NOT something a model opts into — it is
        // enforced server-side on EVERY stop attempt (SupervisorPublishGate), so this arm is GATING, never
        // report-only. Two independent checks, both against the SAME real run: (1) every decision the ledger
        // actually PERSISTED as a genuine `stop` must independently re-validate as null under
        // SupervisorPublishGate.Validate applied to the tape AS IT STOOD immediately before that row — if the gate
        // would have rewritten it (to a forced merge or an ask_human park) but a live Stop landed anyway, I3 broke
        // for real production code, not a hand-crafted fixture. (2) if the run reached Success and ever produced
        // accepted work, the run's final integrated branch must be a REAL branch that actually exists on the real
        // bare remote — the live end-to-end guarantee I3 exists to give an operator (never silently losing work).
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass — a gating arm must surface NOT-EVALUATED, never self-skip green
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip this lane green proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId);

        // GATING best-of-N (uniform with the other strict arms): a FRESH run per attempt; reds only if EVERY
        // non-infra attempt violates I3. A CodeFault reds at once; a gateway outage is non-gating LOUD infra.
        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            jobClient.Clear();
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await DriveUntilSettledAsync(runId);

            var (outcome, note) = await EvaluateI3InvariantAsync(runId, teamId, remote, repoId);
            return (outcome, $"{Provider} model '{model}' I3 publish-or-park — {note}");
        });
    }

    /// <summary>
    /// The I3 hard-gate invariant, checked against a REAL run's REAL decision tape (see the caller for the two
    /// checks). A run with no recorded <c>stop</c> at all did not put I3 to the test this attempt (still spawning,
    /// or parked short on an unrelated ask_human) — reported as a capability miss the best-of-N floor retries,
    /// never a code fault (I3 can only be violated by a stop that actually happened).
    /// </summary>
    private async Task<(RealModelOutcome Outcome, string Note)> EvaluateI3InvariantAsync(Guid runId, Guid teamId, BareRemote remote, Guid repoId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        await ThrowIfGatewayInfraFailureAsync(db, runId);

        var priorDecisions = await ReadPriorDecisionsAsync(db, runId, teamId);
        var stops = priorDecisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.Stop).ToList();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        if (stops.Count == 0)
        {
            // The park half of publish-or-park, made real (the vacuous-Success fix): a run holding on an UNANSWERED
            // gate card with no terminal stop IS the honest outcome for accepted-unpublishable work in a no-surface
            // environment — before the fix this exact shape burned an ask×9 loop into a forced stop and terminalized
            // SUCCESS with zero delivery.
            var lastAsk = priorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman);

            if (lastAsk is not null && SupervisorOutcome.ReadAskHumanAnswer(lastAsk.OutcomeJson) is null && IsGateCardQuestion(lastAsk.PayloadJson))
                return (RealModelOutcome.Drove, $"status={run.Status}, no stop — the run PARKED on the gate card (publish-or-park chose park; the accepted work awaits a human instead of evaporating into a vacuous Success)");

            return (Classify(run.Status, drove: false), $"status={run.Status}, no stop decision was recorded this attempt — I3 was not exercised");
        }

        // Check 1: re-validate EVERY persisted stop against the tape as it stood immediately before that row. A
        // real violation here means the LIVE production gate let an accepted-unpublished stop through for real.
        // A FORCED stop (its payload carries a `reason` — model stops carry outcome/summary instead) re-validates
        // with the SAME requireSummary:false the runtime gate applies to it: a bound authored it, so "no summary"
        // is legal for it by design, and auditing it at the model-stop bar would red a legalized behaviour.
        //
        // The audit context must carry the SAME ledger the live gate's context was folded with: the P0-5
        // ledger-direct shortcut reads PublishedAgentRunIds (rehydrate folds it from the publish manifests), so a
        // run published WITHOUT a tape-visible merge is legal to stop — a bare context indicts the gate for a
        // publication the auditor never looked at (run 31230410920 red exactly this way). Rows are filtered to
        // those CREATED by the stop's own timestamp: an agent's manifest row is created at its completion, so this
        // is what the gate could have seen — folding today's full ledger instead would let a post-stop publication
        // retroactively launder a genuinely bad stop.
        var manifestRows = await db.PublishManifest.AsNoTracking().Where(m => m.WorkflowRunId == runId && m.TeamId == teamId).ToListAsync();
        var stopCreatedBySequence = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .ToDictionaryAsync(d => d.Sequence, d => d.CreatedDate);

        foreach (var stop in stops)
        {
            var priorToThisStop = priorDecisions.Where(d => d.Sequence < stop.Sequence).ToList();
            var isForcedStop = SupervisorOutcome.ReadStopReason(stop.PayloadJson) is not null;
            var ledgerAsOfStop = Core.Services.Supervisor.SupervisorTurnService.FoldPublishedAgentRunIds(
                manifestRows.Where(m => m.CreatedDate <= stopCreatedBySequence[stop.Sequence]).ToList());
            var gateVerdict = SupervisorPublishGate.Validate(new SupervisorTurnContext { PriorDecisions = priorToThisStop, PublishedAgentRunIds = ledgerAsOfStop }, new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = stop.PayloadJson }, requireSummary: !isForcedStop);

            // The ask-once fuse (GateForcedStop, adversarial-scan F1): a FORCED stop whose ask-verdict follows an
            // UNANSWERED gate card already on the tape persists as a stop BY DESIGN — in a no-surface environment
            // (exactly this E2E: every ask degrades to a self-advancing null answer) the card can never be answered,
            // and re-substituting would loop forever. The audit must model the fuse or it indicts the gate for its
            // own designed degraded terminal (run 31247607245 seq-46 red exactly this way — the tape carried the
            // degraded I3 card the fuse keyed on). The exemption is the fuse's EXACT triple: forced stop + AskHuman
            // verdict + a prior unanswered gate-prefixed card — a merge verdict or a MODEL stop still reds, because
            // neither path is fused in production. Whether a fused stop should read as clean Success at all (the
            // accepted-unpublished work silently evaporates) is a product question the completion protocol owns,
            // tracked separately — this audit pins the gate's ACTUAL contract, not that open question.
            var fuseHeld = isForcedStop && gateVerdict?.Kind == SupervisorDecisionKinds.AskHuman
                && Core.Services.Supervisor.SupervisorTurnService.HasUnansweredGateCard(new SupervisorTurnContext { PriorDecisions = priorToThisStop });

            if (fuseHeld) continue;

            gateVerdict.ShouldBeNull($"a Stop decision (sequence {stop.Sequence}) was actually PERSISTED as a genuine stop, but SupervisorPublishGate.Validate says it should have been rewritten to '{gateVerdict?.Kind}' — I3 did not hold for this real run.");
        }

        // Check 2: the strongest, most direct proof — a Success completion with any accepted work must carry a
        // REAL branch that genuinely exists on the real remote, never just a name in result_jsonb.
        var everProducedWork = priorDecisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Any(SupervisorOutcome.ResultShowsWork);

        if (run.Status == WorkflowRunStatus.Success && everProducedWork)
        {
            // The reviewable head has TWO designed sources: the tape's merge/resolve-derived branch, and the DC-3
            // ledger-direct surface (a single accepted contributor's own pushed manifest — no merge decision ever
            // runs, so the tape is silent BY DESIGN and the branch lives on the terminal outputs instead, the exact
            // field Check 3 below already reads its sibling from). Reading only the tape false-reds every
            // ledger-direct run — the first complete supervisor-arcs window (run 29215356358) hit exactly that.
            var integratedBranch = SupervisorOutcome.ReadFinalIntegratedBranch(priorDecisions) ?? ReadTerminalIntegratedBranch(run.OutputsJson);

            var stopTrail = string.Join(" → ", stops.Select(d => SupervisorOutcome.ReadStopReason(d.PayloadJson) is { } reason ? $"forced({reason})" : "model-stop"));
            var kindTrail = string.Join("→", priorDecisions.Select(d => d.DecisionKind));
            integratedBranch.ShouldNotBeNullOrEmpty($"the run completed Success with accepted work, but carries no final integrated branch on EITHER source (tape merge/resolve AND the DC-3 terminal-output surface) — I3 must never let this combination reach Completed silently. stops=[{stopTrail}], decisions=[{kindTrail}], terminalOutputs={run.OutputsJson}");

            var branchesOnRemote = await remote.ListBranchesAsync();
            branchesOnRemote.ShouldContain(integratedBranch!, "the run's own final branch must genuinely exist on the real remote, not just be a name recorded in the ledger");

            // Check 3 (PR-6): the terminal output's repositoryId — the field the Room's Open-PR action resolves
            // integratedBranch's OWNING repository from (a bare branch name carries no repository of its own) — must
            // genuinely flow through the REAL engine's terminal-node binding, not just the hand-seeded tapes the
            // IRoomPullRequestService integration tests exercise. Riding the SAME already-paid-for real-model run
            // rather than spinning up a second one purely to re-prove I3's own branch-exists guarantee.
            var repositoryId = ReadTerminalRepositoryId(run.OutputsJson);
            repositoryId.ShouldBe(repoId, "the run's terminal output must echo the SAME repository the branch was actually pushed to — PR-6's Open-PR action has no other way to resolve it for a single-repo run");
        }

        return (Classify(run.Status, drove: true), $"status={run.Status}, stops={stops.Count}, everProducedWork={everProducedWork}, all I3 checks held");
    }

    [SkippableFact]
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
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass — a gating arm must surface NOT-EVALUATED, never self-skip green
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip this lane green proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new LiveBrainConflictFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
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

            await DriveUntilSettledAsync(runId);

            // The SAME unattended operator surface as the headline arc. This arm already seeds a conversation, so its
            // asks PARKED and the run sat Suspended before ever integrating — every attempt scored
            // merge-conflicted=False, i.e. the conflict the arm exists to present was never reached. Answering lets the
            // arc get there; the criterion below is UNCHANGED (any prompt-sanctioned handling still passes, silently
            // merging over a real conflict still reds).
            var (answeredAsks, skippedAmendAsks) = await AnswerParkedAsksAsync(runId, teamId, userId);

            var (outcome, note) = await EvaluateConflictResolveAsync(runId, teamId);
            return (outcome, $"{Provider} model '{model}' conflict→resolve — {note}, answeredAsks={answeredAsks}, skippedAmendAsks={skippedAmendAsks}");
        });
    }

    [SkippableFact]
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
        // force-STOPs cleanly on a bound (no-progress / total-spawn cap), never a run Failure, so a model that recovered
        // reads Drove from the ledger and is never mis-gated as a CodeFault.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass — a gating arm must surface NOT-EVALUATED, never self-skip green
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip this lane green proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new LiveBrainFailingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        const string goal = "Add server-side email-format validation to the signup endpoint, with unit tests. "
                          + "If a subtask's agent reports it could not complete the work, retry that subtask before finishing.";

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId, goal);

        // GATING best-of-N (N from the job env, uniform with the other strict arms): a FRESH run per attempt; reds only if
        // EVERY non-infra attempt fails to take an ACTIVE recovery (retry — the instructed action — or escalate) on the
        // real failure. A perpetual-failure run force-stops cleanly on a bound (no-progress / total-spawn cap), never a run Failure.
        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            jobClient.Clear();
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);   // a FRESH run per best-of-N attempt

            await DriveUntilSettledAsync(runId);

            var (outcome, note) = await EvaluateFailureRetryAsync(runId, teamId);
            return (outcome, $"{Provider} model '{model}' failure→retry — {note}");
        });
    }

    [SkippableFact]
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
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip this lane green proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;
        if (!await ClaudeReadyAsync()) throw RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the real-coding arm needs a harness binary (skip ≠ pass)");   // honest-skip, NOT a pass

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

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

            await DriveUntilSettledAsync(runId);

            var (outcome, note) = await EvaluateAsync(runId, teamId, Array.Empty<string>());   // REAL claude — 0 patches IS a capability outcome, never a capture-infra skip; no fake to lose control of

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
        // The PRODUCTION conflict reader (Merge OR Spawn), not a merge-only re-implementation: S1 dependency staging
        // records its own un-integrable handoff on the SPAWN row, and both the resolve executor and the decider's
        // action mask route off SupervisorOutcome.FindConflictDecision. A merge-only predicate here scored a genuine
        // staged conflict as "never produced a real conflict" and RED-ed the arm the run had actually handled.
        var conflicted = SupervisorOutcome.FindConflictDecision(await ReadPriorDecisionsAsync(db, runId, teamId)) is not null;
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
    /// FAULTED run (<see cref="WorkflowRunStatus.Failure"/>) is reported as <see cref="RealModelOutcome.CodeFault"/> — the
    /// engine could not execute the brain's decisions.
    ///
    /// <para>NOTE ON WHAT THAT LABEL DOES AND DOES NOT PROVE. This classifier keys on <c>status == Failure</c> and the
    /// gateway-category regex, and nothing else — so it cannot distinguish "a code change broke the engine" from "the
    /// engine refuses a shape the model authored". It once claimed it could, on the premise that every model-side miss
    /// fails closed to a clean stop; that premise was FALSE and this label is how it was found. A model-authored persona
    /// slug the team library did not hold threw out of the spawn executor and failed four real runs (2026-08-19 10:16 to
    /// 2026-08-20 01:12), each reported here as a CodeFault with no code change behind it. That specific hole is closed —
    /// the slug is now pre-resolved and the spawn rejected re-authorably — but the label remains a CLASSIFICATION, not
    /// evidence: read the run's exception before believing it names a regression.</para>
    ///
    /// <para>A
    /// run that drove the arc → <see cref="RealModelOutcome.Drove"/>; any other clean terminal (the brain stopped or
    /// parked short of the arc — a capability shortfall, not a code bug) → <see cref="RealModelOutcome.CapabilityMiss"/>,
    /// which is reported but never gates.
    /// </summary>
    private static RealModelOutcome Classify(WorkflowRunStatus status, bool drove) =>
        status == WorkflowRunStatus.Failure ? RealModelOutcome.CodeFault
        : drove ? RealModelOutcome.Drove
        : RealModelOutcome.CapabilityMiss;

    /// <summary>The run-level terminal output's <c>integratedBranch</c> — the DC-3 ledger-direct surface (a run whose accepted work published via per-unit pushed manifests has NO merge decision on the tape; the branch reaches the terminal binding through <c>SupervisorTurnService.BuildFinalResultAsync</c>). Null on absence/parse failure.</summary>
    private static string? ReadTerminalIntegratedBranch(string outputsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);

            return doc.RootElement.TryGetProperty("integratedBranch", out var prop) && prop.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.GetString())
                ? prop.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>PR-6: the run-level terminal output's <c>repositoryId</c> (echoed from <c>AgentSupervisorNode.Finish</c>'s config, via <c>SupervisorDefinitionBuilder.TerminalInputs</c> → <c>workflow_run.outputs_jsonb</c>). Null on any parse failure or absence — a real regression here reads as a mismatch against the seeded repo id, not a silent pass.</summary>
    private static Guid? ReadTerminalRepositoryId(string outputsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);

            return doc.RootElement.TryGetProperty("repositoryId", out var prop) && prop.ValueKind == JsonValueKind.String && Guid.TryParse(prop.GetString(), out var id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A gateway/transport outage DURING a turn is swallowed by the engine into a run Failure (the run-level error is the
    /// generic "Node failed."; the typed <c>LlmApiException</c> detail lives on the node-failed ledger record). If THAT
    /// detail is a gateway infra failure, throw a <see cref="TimeoutException"/> so the live-gate's infra-skip catch
    /// treats it as NON-GATING — honoring the lane-wide "a gateway timeout never gates" guarantee (consistent with the
    /// decision-eval lane), instead of the three-way classifier reading the Failure as a code fault. A genuine engine
    /// fault (any other node-failed error) is left untouched, so it gates as a <see cref="RealModelOutcome.CodeFault"/>.
    /// Called by every evaluator BEFORE it classifies, so the routing is uniform across all three live lanes.
    ///
    /// <para>The outage does not always reach a run Failure. When the brain-plane fault is a PARKABLE one the node
    /// rides it (park → wake → park), and only a whole exhausted window ends the run — as a clean <c>stop</c> on a
    /// Success walk stamped <c>ModelPlaneUnavailable</c>. That is the same outage wearing the product's own graceful
    /// ending, so it routes to the same non-gating skip. It is admitted ONLY when the attempt got no model turn at all
    /// (the tape holds nothing but that forced stop): an attempt whose model DID decide before the plane went down has
    /// something measured, and keeps today's scoring. DEFENCE IN DEPTH — this arm's own parks are ridden by
    /// <c>InfraParkRide</c>, which gives up ~40s in and never lets the engine's 24h window exhaust inside a ride; see
    /// <c>RealModelGate.IsWholeWindowModelPlaneOutage</c> for the routes that DO reach it.</para>
    /// </summary>
    private async Task ThrowIfGatewayInfraFailureAsync(CodeSpaceDbContext db, Guid runId)
    {
        var nodeFailure = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed && r.NodeId == NodeId)
            .OrderByDescending(r => r.Sequence).Select(r => r.PayloadJson).FirstOrDefaultAsync();

        if (RealModelGate.IsGatewayInfraError(nodeFailure))
            throw new TimeoutException($"the supervisor brain's gateway failed mid-run (NON-GATING infra): {nodeFailure}");

        var decisions = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId)
            .OrderBy(d => d.Sequence).Select(d => d.PayloadJson).ToListAsync();

        if (RealModelGate.IsWholeWindowModelPlaneOutage(decisions))
            throw new TimeoutException($"the supervisor brain's model plane stayed unavailable for the whole park window and the run never took a model turn (NON-GATING infra): {decisions[0]}");
    }

    /// <summary>The live brain drove the whole loop soundly iff the run reached Success, at least one real agent produced a real patch, and the terminal stop's objective acceptance PASSED (a green check.sh against the integrated head). Classified three-way for safe gating + returns a legible note. <paramref name="stubbedHarnessKinds"/> is the set of harness kinds this arm's fake actually arms (the fake's own declaration, e.g. <c>FileWritingFakeCli.StubbedHarnessKinds</c>). It does two things. First it gates control: an agent dispatched onto a harness NOT in that set ran a REAL CLI, so every deterministic-fake premise below is void and the arm returns <see cref="RealModelOutcome.CodeFault"/> rather than a refunded infra skip. Second, being non-empty is what licenses the capture-infra skip — a deterministic fake ALWAYS patches on success, so a spawned+merged-but-zero-captured-patches run is a workspace-capture fault, not a model miss. The real coding-agent arm passes an EMPTY set: it legitimately expects the real claude binary, has no fake to lose control of, and its 0 patches IS a capability outcome that must gate.</summary>
    private async Task<(RealModelOutcome Outcome, string Note)> EvaluateAsync(Guid runId, Guid teamId, IReadOnlyList<string> stubbedHarnessKinds)
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
            .Select(r => new { r.Status, r.Error, r.ResultJson, r.Harness }).ToListAsync();

        // BEFORE either classifier: did this arm still control what its agents ran? Both classifiers reason about
        // THE FAKE, and an agent the brain dispatched onto a harness this fake never armed ran a real CLI instead —
        // which would otherwise launder into the non-gating infra refund and read as ordinary model variance. This is
        // a CodeFault (it gates at once and is never retried), because losing test control is a defect in the arm,
        // not a property of the model. Past this point every remaining run IS a fake run, so the two classifiers'
        // premises hold again.
        var (lostControl, harnessCensus) = RealModelGate.ClassifyHarnessControl(agentRuns.Select(r => r.Harness).ToList(), stubbedHarnessKinds);
        if (lostControl)
            return (RealModelOutcome.CodeFault,
                $"the arm lost control of its agents: it arms [{string.Join(", ", stubbedHarnessKinds)}] but the run dispatched {harnessCensus}. "
              + "An agent on an unarmed harness ran a REAL CLI, so this arm's deterministic-fake premises are void and its verdict would be about something it never controlled.");

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
        if (RealModelGate.IsCaptureInfraFault(stubbedHarnessKinds.Count > 0, spawnedAndMerged, succeededAgents, realPatchCount))
            throw new AgentExecutionInfraException(
                $"the brain spawned+merged and {succeededAgents} agent(s) SUCCEEDED, but ZERO real patches were captured ({agentSummary}). "
              + "The headline fake agent ALWAYS writes a file on success, so a succeeded fan-out with no captured patch is a workspace-capture/execution infra fault on this runner (a fork-starved file write or git-diff capture), NOT a model miss.");

        var trail = string.Join("→", kinds);

        // The honest OUTCOME word, on whatever terminal this attempt actually reached. Checked on a MISS as well as a
        // drive, and deliberately so: the degraded words (a give-up, a forced cut, an abstention) are only ever EARNED
        // on an arc the model did not finish, so gating this on Drove alone would test the one ending that never
        // exercises them. A break here is a code fault regardless of how the model did — the run's own tape and its
        // persisted word disagree.
        if (await HonestOutcomeProbe.FaultAsync(_fixture, runId, teamId) is { } outcomeFault)
            return (RealModelOutcome.CodeFault, $"{outcomeFault} (status={run.Status}, trajectory={trail})");

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

    /// <summary>
    /// Drive the run, drain its jobs, then RIDE any model-plane park to settlement. A node that calls a model parks on a
    /// transient gateway fault instead of failing the run (the shared <c>InfraPark</c> ladder), and the in-memory job
    /// client only RECORDS the park's scheduled deadline — so without the ride a flapping gateway leaves the run merely
    /// Suspended and every evaluator below scores that a park-short CapabilityMiss, red-ing main for a run that behaved
    /// exactly as designed. Riding beats skipping: a parked run is designed to RESUME AND FINISH, so the gate drives it
    /// there and judges the finished run with every assertion intact. A park that outlives the ride's budget throws
    /// <see cref="InfraParkUnresolvedException"/>, which <see cref="RealModelGate"/> routes to its LOUD non-gating infra
    /// skip — honest, never a green pass, and it does not consume a best-of-N capability slot.
    /// </summary>
    private async Task DriveUntilSettledAsync(Guid runId)
    {
        await RunEngineAsync(runId);
        await DrainUntilSettledAsync(runId);
    }

    /// <summary>Drain a resume's re-dispatch, then ride out any park it hit — the human-resume counterpart of <see cref="DriveUntilSettledAsync"/> (the resume drives the engine itself, so this must not re-enter it).</summary>
    private async Task DrainUntilSettledAsync(Guid runId)
    {
        await ResolveJobClient().WaitForPendingAsync();
        await InfraParkRide.RideAsync(_fixture, runId);
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

    [SkippableFact]
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
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new MultiRepoFeatureFakeCli();   // each agent writes a disjoint file into BOTH repo subdirs → both integrate cleanly

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

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

            await DriveUntilSettledAsync(runId);

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

    [SkippableFact]
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
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the arm proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

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

            await DriveUntilSettledAsync(runId);

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

    /// <summary>The Enforced stamp must have HELD before the engine runs — a silent drift back to Shadow would keep the arm green while proving nothing about arbitration. C5: this arm stamps NO opt-in, so what is pinned here is the DEFAULT resolution itself.</summary>
    private async Task AssertRanEnforcedAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.CompletionEnforcementMode.ShouldBe("Enforced", customMessage: "this definition opts into NOTHING — a supervisor run must resolve Enforced by DEFAULT (CompletionPolicy.DefaultModeFor over the supervisor profile's Enforceable standing); Shadow here means the default cohort regressed");
    }

    private async Task<Guid> CreateWholeLoopWorkflowAsync(Guid teamId, Guid userId, Guid repoId, Guid brainModelId, string? goal = null, Guid? conversationId = null, Guid? agentCredId = null, string? agentModel = null, (Guid RepoId, string Alias)? relatedRepo = null, string? completionMode = null)
    {
        // When an agent credential is supplied, the spawned agents run a REAL coding-CLI harness (claude-code) against the
        // gateway (its credential decrypted just-in-time + projected onto ANTHROPIC_BASE_URL/AUTH_TOKEN by the harness), at
        // Trusted autonomy so they get network egress to reach the gateway. Absent → the byte-identical fake-agent profile.
        var realAgentFields = agentCredId is { } ac
            ? $$""", "harness": "claude-code", "modelCredentialId": "{{ac}}", "model": "{{agentModel}}", "autonomyLevel": "Trusted" """
            : "";
        // The live brain (supervisorModelId) authors the arc; its agents clone repoId + push branches, the merge
        // integrates them, and the operator acceptance floor (check.sh) gates the terminal stop. There is NO round cap —
        // the run loops until the acceptance floor drives the terminal stop (a real model is deliberate: plan → spawn →
        // inspect → (retry) → merge → stop, ~5-6 turns), bounded only by the best-effort no-progress guard (each settling
        // spawn resets its streak, so a working run never trips it) and this lane's job timeout; a per-call timeout still
        // self-skips as non-gating infra.
        // A conversationId (when set) is the surface the irreversible `resolve` gate parks its human-approval card on.
        // The default goal carries the SAME oracle anchor as handoffGoal (P5 finding): "with unit tests" invited the
        // live model to author "run the tests" per-subtask acceptance, which the marker-file fakes can never satisfy —
        // every unit graded acceptance-REJECTED, merge withheld them all, the integrated tree carried no agent file,
        // and the stop floor failed (acceptancePassed=false ×3 with 4/4 agents succeeded, run 31247607245). Arms that
        // pass their OWN goal keep their own oracle posture (the stop-DoD arm deliberately leaves authorship free).
        var effectiveGoal = goal ?? ("Add server-side email-format validation to the signup endpoint, with unit tests. "
                                   + "For EVERY subtask, author its acceptance check as exactly the command `sh check.sh` (the repository's own seeded gate) — "
                                   + "this repository has NO other test tooling, so any other acceptance command will fail regardless of the work.");
        var conversationLine = conversationId is { } cid ? $",\n              \"conversationId\": \"{cid}\"" : "";
        // A relatedRepo (when set) makes this a MULTI-repo run: the profile mounts a SECOND writable repo under its alias,
        // so every spawned agent's workspace has both repos (cwd = workspace root, each repo at <root>/<alias>/) and the
        // supervisor integrates + accepts EACH repo on its own axis. Mirrors the deterministic whole-loop's relatedLine.
        var relatedLine = relatedRepo is { } rr ? $",\n                \"relatedRepositories\": [ {{ \"repositoryId\": \"{rr.RepoId}\", \"alias\": \"{rr.Alias}\", \"access\": \"write\" }} ]" : "";
        var supConfig = $$"""
            {
              "goal": "{{effectiveGoal}}",
              "supervisorModelId": "{{brainModelId}}",
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
                CompletionMode = completionMode,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                    // Mirrors the production projection's terminal binding (shared literal) — without it the PR-6
                    // repositoryId-echo check asserted a surface this workflow never wired, so the arm could only
                    // pass on trajectories that skipped the accepted-work branch.
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = CodeSpace.Core.Services.Tasks.Projection.Builders.Supervisor.SupervisorDefinitionBuilder.TerminalInputs() },
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

    /// <summary>
    /// D3 (Arc D) — the north-star IS the release gate: the SAME M-1 reducer the operator's scorecard renders must
    /// read the Drove round's WINNING run as unattended-solved-with-delivery. Per-run, deliberately: best-of-N
    /// seeds a fresh run per attempt on the same team, so a team-level rate would grade the model's earlier
    /// honest misses, not this round's claim.
    /// </summary>
    private async Task AssertNorthStarClearsFloorAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();

        // The scorecard's Solved reads the assessment ledger, which ONLY the shadow sweep writes — an unswept
        // round would read a false negative. Sweep first.
        await scope.Resolve<CodeSpace.Core.Services.Completion.ICompletionShadowService>().SweepAsync(batchSize: 50, CancellationToken.None);

        var scorecard = await scope.Resolve<CodeSpace.Core.Services.Agents.Eval.IUnattendedDeliveryScorecardService>().ComputeAsync(teamId, since: null, CancellationToken.None);
        var run = scorecard.Runs.SingleOrDefault(r => r.WorkflowRunId == runId)
            ?? throw new AgentExecutionInfraException($"the M-1 instrument is sick — the Drove round's run {runId} never entered the scorecard population ({scorecard.Rollup.TotalRuns} scored); fix the population query, never read this as capability");

        run.UnattendedSolvedWithDelivery.ShouldBeTrue(
            $"north-star gate: this arc terminalized an unattended Enforced Success with a real delivery, so the SCORECARD must read exactly that for run {runId} — got solved={run.Solved}, delivered={run.Delivered}, humanTouches={run.HumanTouches}; a false reading means the reducer chain (sweep → assessment → M-1) dropped the round");

        Console.WriteLine($"[m1-gate] run {runId}: unattended solve-with-delivery TRUE (solved={run.Solved}, delivered={run.Delivered}, humanTouches={run.HumanTouches}) — the north-star reducer confirms the round");
    }

}
