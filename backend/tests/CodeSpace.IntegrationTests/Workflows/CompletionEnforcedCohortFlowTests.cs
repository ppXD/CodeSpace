using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High fidelity: the P2b Enforced-cohort canary through the REAL production chain — the definition's own
/// <see cref="WorkflowDefinition.CompletionMode"/> opt-in, the real <c>RunStarter</c>/<c>RunFromSnapshotStarter</c>
/// stamp, the real engine terminal, the real completion authority + composer over real Postgres. The fail-close
/// proof is the point: an Enforced run whose clean engine Success stakes NOTHING parks for a human instead of
/// terminalizing — the exact protocol the isolated canary cohort exists to exercise. C5 made that cohort the
/// DEFAULT wherever it is safe: a supervisor run opting into nothing resolves Enforced from its own mode profile,
/// while a mode below the Enforceable bar (plan-map, single-agent, a generic graph) keeps the Shadow fallback and
/// behaves byte-identically to before. An unreadable opt-in still never stores.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CompletionEnforcedCohortFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionEnforcedCohortFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_enforced_opt_in_for_an_unready_mode_refuses_to_launch()
    {
        // Q3 upgraded this canary: a bare trigger→terminal graph is the GENERIC mode — no conformance story, so
        // the Enforced opt-in no longer stamps-then-parks at the terminal; the REAL RunStarter refuses the launch
        // itself, naming the mode and the standing it lacks (cheaper than burning a run to park, same fail-close).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced));

        var ex = await Should.ThrowAsync<Exception>(() => RunManuallyAsync(teamId, userId, workflowId));

        ex.Message.ShouldContain("mode 'generic'", customMessage: "the refusal must name the operating mode the admission read");
        ex.Message.ShouldContain("Enforced cohort", customMessage: "…and the cohort law it fell short of");
    }

    [Fact]
    public async Task A_definition_without_the_opt_in_stays_shadow_and_passes_through()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(completionMode: null));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionEnforcementMode.ShouldBe("Shadow", "a generic graph has no conformance story — no opt-in keeps the Shadow fallback");
        run.Status.ShouldBe(WorkflowRunStatus.Success, "outside the cohort, behavior is byte-identical to before the flip existed");
    }

    [Theory]
    [InlineData(WorkflowDefinition.CompletionModeEnforced)]   // the explicit opt-in
    [InlineData(null)]                                        // C5: NO opt-in — the supervisor default IS Enforced
    public async Task A_snapshot_run_of_a_supervisor_definition_stamps_enforced_and_its_unbacked_success_parks(string? completionMode)
    {
        // The ADMITTED half of the gate: the tasks lane's supervisor projection holds Enforceable standing, so
        // the snapshot starter stamps Enforced through the real admission — and the park safety net still holds:
        // this run's clean engine Success stakes nothing, so the authority parks it instead of terminalizing.
        // C5 folded the DEFAULT cohort into the same law: a supervisor run that opts into nothing (what every
        // Launch produces — the FE sends no completionMode) resolves Enforced from its own mode profile, so an
        // unverified "completed" stop parks honestly instead of ending the run as Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await StartSupervisorSnapshotAsync(teamId, userId, completionMode);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionEnforcementMode.ShouldBe("Enforced", "supervisor is the Enforceable cohort — by opt-in AND by default");
        run.Status.ShouldBe(WorkflowRunStatus.Suspended, "an Enforced claim nothing staked must still park, never terminalize");
        run.Error.ShouldNotBeNull();
        run.Error.ShouldContain("completion-authority", customMessage: "the park must name its arbiter — check workflow_run.error for the decision detail");

        var terminalRecords = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && new[] { WorkflowRunRecordTypes.RunCompleted, WorkflowRunRecordTypes.RunFailed, WorkflowRunRecordTypes.RunCancelled }.Contains(r.RecordType))
            .ToListAsync();
        terminalRecords.ShouldBeEmpty("a parked run is resumable, so its append-only ledger must not announce a contradictory terminal state");
    }

    [Fact]
    public async Task A_default_supervisor_run_with_an_integrated_head_still_terminalizes_success()
    {
        // The other half of C5's bargain — enforcement by default must not cost an EARNED Success. The same
        // opt-in-less supervisor run, this time with its contract world answered (staked acceptance/delivery/output,
        // a graded merged tape carrying an integrated head, a pushed manifest), terminalizes CleanSuccess on the
        // first pass with no park in between. Without this the default flip would be indistinguishable from
        // "supervisor runs never succeed any more".
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await RunToCleanSuccessAsync(teamId, userId, completionMode: null);

        var run = await ReadRunAsync(runId);

        run.CompletionEnforcementMode.ShouldBe("Enforced", "the default resolved Enforced — this is the arbitrated path, not a Shadow pass-through");
        run.Status.ShouldBe(WorkflowRunStatus.Success, "a fully evidenced completion must still terminalize under the DEFAULT cohort — check workflow_run.error for the park reason if it did not");
        run.CompletionParkedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_default_supervisor_run_that_recovered_via_retry_terminalizes_success()
    {
        // The recovery half of C5's bargain: enforcement must not park a run that legitimately RECOVERED. The unit's
        // first attempt failed its check and produced nothing; the RETRY passed and delivered. The completion
        // dimensions must read the LATEST attempt — the superseded attempt's Failed verdict never reaches the fold
        // (admission drops it as superseded), so verification is Passed and the run terminalizes CleanSuccess.
        // A run that parks here tells an operator to adjudicate work the run itself already verified.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await StartSupervisorSnapshotAsync(teamId, userId, completionMode: null, DeclaredOutputs);

        var retryAttemptId = await SeedRetryRecoveredTapeAsync(runId, teamId);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, retryAttemptId, repositoryId);   // only the RECOVERED attempt ever delivered
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        var run = await ReadRunAsync(runId);

        run.CompletionEnforcementMode.ShouldBe("Enforced", "the supervisor default resolved Enforced — this is the arbitrated path, not a Shadow pass-through");
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: $"a retry-recovered run must terminalize, not park — the retried unit's own PASS is its completion evidence; the authority's refusal reason was: {run.Error}");
        run.CompletionParkedAt.ShouldBeNull("nothing about a recovery is unadjudicated — the run must not carry a park stamp");
    }

    [Fact]
    public async Task A_default_supervisor_run_that_gave_up_with_a_failed_unit_ends_an_honest_failure()
    {
        // The continue-on-error terminal: one unit passed and merged, the other FAILED its own check, and the model
        // stopped `gave_up` rather than claiming completion. That is a decided, honest non-success — the authority
        // must stamp Failure and let the run END. Parking it would hand an operator a decision the run already made,
        // and would make every partially-failing run under the default cohort wait on a human forever.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await StartSupervisorSnapshotAsync(teamId, userId, completionMode: null, DeclaredOutputs);

        var attempts = await SeedPartialFailureTapeAsync(runId, teamId);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attempts[0], repositoryId, "codespace/agent/s1");   // only the PASSING unit ever delivered

        foreach (var unit in new[] { "s1", "s2" })
            await StakeAsync(runId, teamId, $"acceptance:{unit}", ContractKinds.Acceptance);

        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        var run = await ReadRunAsync(runId);

        run.CompletionEnforcementMode.ShouldBe("Enforced", "the supervisor default resolved Enforced — this is the arbitrated path");
        run.Status.ShouldBe(WorkflowRunStatus.Failure,
            customMessage: $"a give-up over a failed unit is a DECIDED outcome — the authority must stamp Failure, not park. It said: {run.Error}");
        run.Error.ShouldNotBeNull();
        // ANCHORED, not merely worded (Rule 8): the real-model gates classify this terminal by the prefix at the
        // START of the error. A future renderer that prepends anything in front of the arbiter's own slot would keep
        // a ShouldContain green while silently reverting every honest failure to a gating CodeFault.
        run.Error!.ShouldStartWith(CompletionTerminalAuthority.HonestFailureReasonPrefix, customMessage: "the terminal must LEAD with the arbitration that produced it, not merely mention it");
        run.CompletionParkedAt.ShouldBeNull("nothing is unadjudicated — a decided failure is not a park");
    }

    [Fact]
    public async Task A_default_plan_map_run_is_untouched_by_the_flip()
    {
        // The cohort line is the mode PROFILE's, not a global switch: plan-map holds ProtocolReadiness.Open, so an
        // opt-in-less plan-map run keeps the Shadow fallback and its terminal is byte-identical to before C5 — the
        // authority never even composes. Flipping the default globally would have parked this run instead.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await StartSupervisorSnapshotAsync(teamId, userId, completionMode: null, projectionKind: TaskProjectionKinds.PlanMapSynth);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        var run = await ReadRunAsync(runId);

        run.CompletionEnforcementMode.ShouldBe("Shadow", "plan-map has not graduated — the default must read its profile, never a blanket Enforced");
        run.Status.ShouldBe(WorkflowRunStatus.Success, "a below-the-bar mode's terminal stays exactly as it was; C5 must not park a cohort that never qualified");
    }

    [Fact]
    public async Task A_snapshot_launch_of_an_enforced_generic_definition_refuses_too()
    {
        // Same admission fold, other lane: the snapshot starter derives the mode from the identical
        // (projection kind, frozen json) pair the row would carry — no projection kind + a bare graph reads
        // generic, and the launch refuses before any row exists.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
            Definition(WorkflowDefinition.CompletionModeEnforced), teamId, userId,
            launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None));

        ex.Message.ShouldContain("mode 'generic'");
    }

    [Fact]
    public async Task An_unknown_completion_mode_never_stores()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var ex = await Should.ThrowAsync<Exception>(() => CreateWorkflowAsync(teamId, userId, Definition("yolo")));

        ex.Message.ShouldContain("Unknown completionMode 'yolo'");
    }

    // ── The terminal row is COMPLETE: an arbitrated stamp carries the declared outputs, exactly as a tracked save does ──

    [Fact]
    public async Task An_enforced_terminals_declared_outputs_persist_exactly_as_a_shadow_runs_do()
    {
        // The regression lock: enforcement decides whether a Success is EARNED, never what a run is recorded as
        // having produced. The same graph on the two modes takes two different terminal writers (the arbitrated
        // compare-and-swap vs. the tracked save), so the only defensible assertion is that both rows carry the
        // SAME declared outputs — a divergence here is silent data loss for every reader of workflow_run.outputs_jsonb.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var enforcedRunId = await RunToCleanSuccessAsync(teamId, userId);

        // Re-drive the enforced side through a compose that mints NOTHING, so the arbitrated stamp is the only
        // writer that could carry the outputs. Without this the first pass's receipt mint incidentally flushes the
        // tracked entity and the comparison below passes on the unfixed engine too — a coverage claim, not a lock.
        await ResetToCrashedBeforeTerminalAsync(enforcedRunId);
        await RunEngineAsync(enforcedRunId);

        var shadowWorkflowId = await CreateWorkflowAsync(teamId, userId, Definition(completionMode: null, DeclaredOutputs));
        var shadowRunId = await RunManuallyAsync(teamId, userId, shadowWorkflowId);
        await ForceEnqueuedAsync(shadowRunId);
        await RunEngineAsync(shadowRunId);

        var enforced = await ReadRunAsync(enforcedRunId);
        var shadow = await ReadRunAsync(shadowRunId);

        enforced.Status.ShouldBe(WorkflowRunStatus.Success, "the seeded contract world answers every staked obligation — the Enforced claim is earned");
        shadow.Status.ShouldBe(WorkflowRunStatus.Success);

        enforced.OutputsJson.ShouldNotBe("{}", "the Terminal declared outputs and the run reached it on a pass that appended no receipt — an empty outputs column means the arbitrated stamp dropped them");
        enforced.OutputsJson.ShouldBe(shadow.OutputsJson, "same graph, same Terminal, same produced value: enforcement mode must never decide what a run is recorded as having produced");
    }

    [Fact]
    public async Task An_arbitrated_stamp_persists_the_outputs_when_the_compose_appends_no_new_receipt()
    {
        // The nondeterminism this pins away. The arbitrated stamp is an ExecuteUpdate, which flushes no tracked
        // state — so before the fix the outputs reached the row only when something ELSE in the same scoped
        // context saved first, and the composer's receipt write-through was that something: a compose minting a
        // NEW receipt incidentally committed them, a compose whose receipts ALL already exist rolls every append
        // back on the exactly-once constraint and commits nothing. Same run, same graph, outputs kept or lost on
        // ledger history alone.
        //
        // Second pass is that all-collide compose: pass one minted the receipts, then the row is put back into
        // the shape a crash between compose and stamp leaves behind (re-drivable, no terminal, no outputs), so
        // the re-driven walk's compose re-mints nothing and the CAS is the only writer left that could carry them.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await RunToCleanSuccessAsync(teamId, userId);
        (await ReadRunAsync(runId)).Status.ShouldBe(WorkflowRunStatus.Success, "pass one must terminalize, or the re-compose runway proves nothing");

        await ResetToCrashedBeforeTerminalAsync(runId);

        await RunEngineAsync(runId);

        var run = await ReadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, "the re-arbitration reads the same answered contract world and stamps the same terminal");
        run.OutputsJson.ShouldNotBe("{}", "no receipt was appended on this pass, so nothing incidentally flushed the outputs — the stamp itself must carry them");
        JsonDocument.Parse(run.OutputsJson).RootElement.GetProperty("answer").GetString().ShouldBe("42");
    }

    [Fact]
    public async Task A_secret_referenced_by_an_enforced_terminal_never_reaches_the_outputs_column()
    {
        // Carrying the outputs into the terminal statement must not route around the secret-leak guard: it fires
        // at Terminal capture, BEFORE any terminal is arbitrated, so the run fails and the value the guard
        // refused was never a candidate for the column on this path either.
        const string Sentinel = "sk-PROD-DO-NOT-LEAK-ABCDEFGH";
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScope())
            await setup.Resolve<IVariableService>().SetAsync(VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret,
                WorkflowsTestSeed.Json(JsonSerializer.Serialize(Sentinel)), null, userId, CancellationToken.None);

        var runId = await StartSupervisorSnapshotAsync(teamId, userId, terminalInputsJson: """{"answer":"{{team.API_KEY}}"}""");

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        var run = await ReadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure, "a Terminal mapping a Secret into the run's outputs is a contract violation, on every enforcement mode");
        run.Error.ShouldNotBeNull();
        run.Error!.ShouldContain("references secret variable", customMessage: "the failure must name the guard, not a generic node error");
        run.OutputsJson.ShouldNotContain(Sentinel, customMessage: "the secret-leak guard exists to keep plaintext credentials out of workflow_run.outputs_jsonb");
    }

    // ── P4: a completion park is DURABLE — the reconciler never re-drives it; Continue is the one channel ──

    [Fact]
    public async Task A_completion_park_is_durable_the_stranded_sweep_never_redrives_it()
    {
        // The parked run wears the stranded sweep's exact shape (Suspended, zero pending waits, past the grace
        // window) — without the park stamp the reconciler would re-dispatch it into a re-walk → re-arbitrate →
        // re-park churn loop forever, each cycle paying a full compose plus a live handoff probe.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await StartSupervisorSnapshotAsync(teamId, userId);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);
        (await ReadRunAsync(runId)).CompletionParkedAt.ShouldNotBeNull("the terminal park must stamp its discriminator");

        await BackdatePastStrandedGraceAsync(runId);

        await ReconcileAsync();

        var run = await ReadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Suspended, "a completion park is deliberate — the stranded sweep must skip it, never re-drive it");
        run.CompletionParkedAt.ShouldNotBeNull("…and the park stamp must survive the sweep");
    }

    [Fact]
    public async Task A_continued_park_re_arbitrates_to_success_once_the_contract_is_answered()
    {
        // THE loop-closer the durable park exists for: park → a human fixes the contract world → Continue →
        // the replayed walk re-arbitrates against the then-current facts and terminalizes CleanSuccess. The runway:
        // a supervisor-stamped Enforced run parks (nothing staked, no tape); the operator's world-fix stakes the
        // full contract and lands the graded merged tape + a pushed manifest; Continue clears the stamp and the
        // re-driven engine stamps Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await StartSupervisorSnapshotAsync(teamId, userId, terminalInputsJson: DeclaredOutputs);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        var parked = await ReadRunAsync(runId);
        parked.Status.ShouldBe(WorkflowRunStatus.Suspended);
        parked.CompletionParkedAt.ShouldNotBeNull();

        var attemptId = await SeedGradedMergedTapeAsync(runId, teamId);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None))
                .ShouldBeTrue("a completion-parked run is exactly what the operator's Continue exists to re-arbitrate");

        (await ReadRunAsync(runId)).CompletionParkedAt.ShouldBeNull("Continue clears the stamp — a re-park must be a fresh decision, never a leftover");

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        var run = await ReadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, "the re-arbitration over the fixed contract world must terminalize CleanSuccess");
        run.CompletionParkedAt.ShouldBeNull();

        // The park committed its outputs through the tracked save; the Continue re-derives them and the arbitrated
        // stamp now writes them too. Pin that the continued terminal still carries the produced value rather than a
        // degraded re-derivation — the one path where the stamp overwrites a column that already held a good value.
        JsonDocument.Parse(run.OutputsJson).RootElement.GetProperty("answer").GetString()
            .ShouldBe("42", "a continued park must terminalize with the value it produced, not with what a re-derivation happened to resolve");
    }

    [Fact]
    public async Task A_parked_run_names_its_reason_in_the_room_and_offers_the_one_verb_that_moves_it()
    {
        // The legibility half of the durable park. The stamp made the park survive the sweep; nothing made it
        // LEGIBLE: the authority's reason lived only in workflow_run.error, the room emitted no block for a
        // Suspended run, the header read "Waiting" (the same word an approval wait shows), and Continue rendered
        // disabled — so the default outcome of an unverified supervisor stop was a run parked forever, silently,
        // with no operable exit. Everything asserted here is the BACKEND's own words and the backend's own gate.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var runId = await RunToUnintegratedParkAsync(teamId, userId, sessionId);

        var parked = await ReadRunAsync(runId);
        parked.Status.ShouldBe(WorkflowRunStatus.Suspended);
        parked.CompletionParkedAt.ShouldNotBeNull("this test is only meaningful over a real completion park");

        var turn = await ProjectTurnAsync(runId, teamId);

        turn.StatusWord.ShouldBe(RoomNarrative.ParkedWord, "a park must not wear the word an approval wait wears — they mean opposite things");

        var card = turn.Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem();
        card.Title.ShouldBe(RoomNarrative.ParkedTitle);
        card.Text.ShouldContain("Continue", customMessage: "the card must state the way out — a park is otherwise indistinguishable from a hang");
        card.RawDetail.ShouldBe(parked.Error, customMessage: "the authority's verbatim refusal must survive to the reader");

        // The refusal's specifics — which required stage holds no evidence — are what an operator acts on. They are
        // the AUTHORITY's words, so assert they survive the projection rather than re-deriving a second account that
        // could quietly disagree with the row.
        card.Text.ShouldContain(nameof(CompletionStage.Integrate), customMessage: $"the card must name the stage the run never evidenced; the authority said: {parked.Error}");
        card.Text.ShouldContain("supervisor", customMessage: "…and the mode whose profile required it");
        card.Text.ShouldNotContain("completion-authority:", customMessage: "the engine prefix is jargon — it belongs behind the raw toggle, not in the reader's sentence");

        var cont = turn.Actions.Single(a => a.Kind == RoomActionKind.Continue);
        cont.Enabled.ShouldBeTrue("the room must offer the one verb that moves a parked run — the reconciler skips a stamped row, so this is its only exit");

        // The offered verb is the real one: it clears the stamp and hands the run back to the engine.
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            (await scope.Resolve<IMediator>().Send(new ContinueRunCommand { RunId = runId })).ShouldBeTrue("a room action must never be offered where the command would refuse it");

        (await ReadRunAsync(runId)).CompletionParkedAt.ShouldBeNull("Continue clears the stamp — the room's exit and the engine's re-arbitration channel are the same door");

        var resumed = await ProjectTurnAsync(runId, teamId);
        resumed.Blocks.OfType<DiagnosticBlock>().ShouldBeEmpty("a continued run is no longer parked — the card must not outlive the stamp it describes");
        resumed.StatusWord.ShouldBeNull("…nor the header word");
    }

    [Fact]
    public async Task Stopping_a_parked_run_ends_the_park_rather_than_leaving_its_label_behind()
    {
        // Stop is the OTHER exit the park card offers, and it must end the park as completely as Continue does. Left
        // behind, the stamp outlives the state it describes: LessonDistiller labels any stamped row "Parked" whatever
        // its status, and ActionableSuspendPredicate reads one as a run still awaiting a person — so a run the operator
        // deliberately ended would keep asking for attention and keep teaching the distiller it was parked.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var runId = await RunToUnintegratedParkAsync(teamId, userId, sessionId);

        (await ReadRunAsync(runId)).CompletionParkedAt.ShouldNotBeNull("this test is only meaningful over a real completion park");

        // The service directly, as every other operator-cancel flow test does: CancelRunAsync opens its own terminal
        // transaction, which cannot nest inside the mediator pipeline's.
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        var stopped = await ReadRunAsync(runId);
        stopped.Status.ShouldBe(WorkflowRunStatus.Cancelled);
        stopped.CompletionParkedAt.ShouldBeNull("a stopped run is not parked — the stamp goes out with the terminal, exactly as Continue and the engine's own terminals clear it");
    }

    private async Task<AssistantTurnBlock> ProjectTurnAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var room = await scope.Resolve<Core.Services.Sessions.Room.IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None);

        room.ShouldNotBeNull();
        return room!.Blocks.OfType<AssistantTurnBlock>().ShouldHaveSingleItem();
    }

    /// <summary>
    /// The park an operator actually meets: a supervisor run that planned, ran its unit to a PASSING check and pushed
    /// its branch — everything a CleanSuccess needs except an integration. The profile requires the Integrate stage,
    /// nothing evidences it, so the authority refuses and names it. One turn of a real session, so the room can
    /// project it.
    /// </summary>
    private async Task<Guid> RunToUnintegratedParkAsync(Guid teamId, Guid userId, Guid sessionId)
    {
        var runId = await StartSupervisorSnapshotAsync(teamId, userId, terminalInputsJson: DeclaredOutputs, session: new SessionAssignment { SessionId = sessionId, TurnIndex = 1 });

        var attemptId = await SeedUnintegratedTapeAsync(runId, teamId);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        return runId;
    }

    /// <summary>The graded tape with its MERGE removed — the unit passed and pushed, but nothing ever integrated it, which is the Integrate stage's only evidence.</summary>
    private async Task<Guid> SeedUnintegratedTapeAsync(Guid runId, Guid teamId)
    {
        var attemptId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, "s1");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { AgentResult(attemptId, "s1", accepted: true) } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Stop, "{}", "{}");
        return attemptId;
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = "Parked turn", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>The Terminal's declared output mapping — a run that reaches it produces <c>{"answer":"42"}</c>, the value the terminal row owes every reader of <c>workflow_run.outputs_jsonb</c>.</summary>
    private const string DeclaredOutputs = """{"answer":"42"}""";

    /// <summary>
    /// An Enforced run whose contract world is answered BEFORE its first walk — staked acceptance/delivery/output,
    /// a graded merged supervisor tape, a pushed manifest on a live repository — so the engine's clean Success is
    /// arbitrated CleanSuccess and terminalizes through the arbitrated compare-and-swap on the FIRST pass, with no
    /// park in between. Returns the run id. <paramref name="completionMode"/> null runs the same arc through C5's
    /// DEFAULT resolution instead of the explicit opt-in.
    /// </summary>
    private async Task<Guid> RunToCleanSuccessAsync(Guid teamId, Guid userId, string? completionMode = WorkflowDefinition.CompletionModeEnforced)
    {
        var runId = await StartSupervisorSnapshotAsync(teamId, userId, completionMode, DeclaredOutputs);

        var attemptId = await SeedGradedMergedTapeAsync(runId, teamId);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        return runId;
    }

    /// <summary>
    /// Put a terminalized run back into the shape a crash BETWEEN the compose and the terminal write leaves
    /// behind: re-drivable (Enqueued), no terminal stamp, and the outputs column still unwritten — while the
    /// receipts that pass already minted stay, which is what makes the next compose a pure re-compose.
    /// </summary>
    private async Task ResetToCrashedBeforeTerminalAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_run SET status = 'Enqueued', completed_at = NULL, outcome = NULL, outputs_jsonb = '{{}}' WHERE id = {runId}");
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "enforced-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> RunManuallyAsync(Guid teamId, Guid userId, Guid workflowId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new RunWorkflowManuallyCommand { WorkflowId = workflowId, Payload = null });
    }

    private async Task<WorkflowRun> ReadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private async Task BackdatePastStrandedGraceAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE workflow_run SET last_modified_date = {0} WHERE id = {1}",
            DateTimeOffset.UtcNow - StuckRunReconcilerService.SuspendedStrandedAfter - TimeSpan.FromMinutes(5), runId);
    }

    private async Task ReconcileAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMediator>().Send(new ReconcileStuckRunsCommand());
    }

    /// <summary>The canonical graded supervisor tape (plan → spawn(passed) → merge → stop) — the same shape <c>CompletionTerminalAuthorityFlowTests</c> seeds, landed AFTER the park as the human's world-fix.</summary>
    private async Task<Guid> SeedGradedMergedTapeAsync(Guid runId, Guid teamId)
    {
        var attemptId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = true, acceptanceDetail = (string?)null, acceptanceEvidenceId = (Guid?)Guid.NewGuid(), producedBranch = "codespace/agent/s1" } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Merge,
            """{"branches":["codespace/agent/s1"]}""",
            $$$"""{"integration":{"status":"integrated","integratedBranch":"codespace/integration/{{{runId:N}}}"}}""");
        await SeedDecisionAsync(runId, teamId, 4, SupervisorDecisionKinds.Stop, "{}", "{}");
        return attemptId;
    }

    /// <summary>
    /// A RECOVERED supervisor tape (plan → spawn(REJECTED, produced nothing) → retry(passed, produced a branch) →
    /// merge → stop): the unit's only passing verdict lives on its SECOND attempt, and its first attempt's Failed
    /// verdict is on the tape to be superseded — the fold must read the latest attempt, never fold the two. Returns
    /// the RETRY attempt's id (the one that has a manifest to deliver).
    /// </summary>
    private async Task<Guid> SeedRetryRecoveredTapeAsync(Guid runId, Guid teamId)
    {
        var firstAttemptId = Guid.NewGuid();
        var retryAttemptId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = firstAttemptId, status = "Failed", acceptancePassed = false, acceptanceDetail = "check exited 1", acceptanceEvidenceId = (Guid?)Guid.NewGuid(), producedBranch = (string?)null } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Retry,
            """{"subtaskId":"s1","revisedInstruction":"fix it, this time honouring the check"}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = retryAttemptId, status = "Succeeded", acceptancePassed = true, acceptanceDetail = (string?)null, acceptanceEvidenceId = (Guid?)Guid.NewGuid(), producedBranch = "codespace/agent/s1" } } }));
        await SeedDecisionAsync(runId, teamId, 4, SupervisorDecisionKinds.Merge,
            """{"branches":["codespace/agent/s1"]}""",
            $$$"""{"integration":{"status":"integrated","integratedBranch":"codespace/integration/{{{runId:N}}}"}}""");
        await SeedDecisionAsync(runId, teamId, 5, SupervisorDecisionKinds.Stop, "{}", "{}");
        return retryAttemptId;
    }

    /// <summary>
    /// The continue-on-error tape (plan(s1,s2) → spawn(s1 accepted + branch, s2 REJECTED) → merge(integrated) → stop
    /// with the model's own <c>gave_up</c> outcome): the run carried on past a unit that failed its own check, merged
    /// what passed, and reported honestly. Returns both attempt ids, s1 first.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> SeedPartialFailureTapeAsync(Guid runId, Guid teamId)
    {
        var passed = Guid.NewGuid();
        var failed = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, "s1", "s2");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1","s2"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { AgentResult(passed, "s1", accepted: true), AgentResult(failed, "s2", accepted: false) } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Merge,
            """{"branches":["codespace/agent/s1"]}""",
            $$$"""{"integration":{"status":"integrated","integratedBranch":"codespace/integration/{{{runId:N}}}"}}""");
        await SeedDecisionAsync(runId, teamId, 4, SupervisorDecisionKinds.Stop, "{}",
            $$"""{"outcome":"{{SupervisorStopPayload.GaveUpOutcome}}","summary":"s2 could not be made to pass its check"}""");
        return new[] { passed, failed };
    }

    /// <summary>The authorized plan every staking tape needs — its recorded <c>workPlanId</c> is what the fold reads a unit's obligations under.</summary>
    private async Task SeedPlanAsync(Guid runId, Guid teamId, params string[] unitIds) =>
        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            JsonSerializer.Serialize(new { subtasks = unitIds.Select(id => new { id, title = "T", instruction = "fix it" }) }),
            $$"""{"planned":[],"count":{{unitIds.Length}},"workPlanId":"{{Guid.NewGuid()}}","workPlanVersion":1}""");

    /// <summary>One folded agent result — <paramref name="accepted"/> false is the objective REJECTION every door to the head withholds on.</summary>
    private static object AgentResult(Guid agentRunId, string unitId, bool accepted) => new
    {
        agentRunId,
        status = accepted ? "Succeeded" : "Failed",
        acceptancePassed = accepted,
        acceptanceDetail = accepted ? null : "check exited 1",
        acceptanceEvidenceId = (Guid?)Guid.NewGuid(),
        producedBranch = accepted ? $"codespace/agent/{unitId}" : null,
    };

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

    private async Task SeedManifestAsync(Guid teamId, Guid agentRunId, Guid repositoryId, string branch = "codespace/agent/s1")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
            RepositoryAlias = "primary", Branch = branch, BaseSha = "b1", CommitSha = "c1",
            PublishStateValue = PublishState.Pushed,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A live team-bound repository — the handoff probe's reachability target.</summary>
    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = "enc",
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = $"repo-{suffix}", FullPath = $"acme/repo-{suffix}",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"https://git.local/acme/repo-{suffix}", Status = RepositoryStatus.Active,
        };

        db.ProviderInstance.Add(instance);
        db.Repository.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }

    private async Task StakeAsync(Guid runId, Guid teamId, string requirementRef, string kind)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Completion.ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = requirementRef, Kind = kind, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);
    }

    /// <summary>The tasks lane's admitted shape: launched with the SUPERVISOR projection kind — the Enforceable cohort — through the real snapshot starter, optionally declaring terminal outputs. A null <paramref name="completionMode"/> is the LAUNCH-REALISTIC shape (the FE sends none), which C5 resolves to Enforced from the mode profile; <paramref name="projectionKind"/> picks the cohort under test.</summary>
    private async Task<Guid> StartSupervisorSnapshotAsync(Guid teamId, Guid userId, string? completionMode = WorkflowDefinition.CompletionModeEnforced, string? terminalInputsJson = null, string projectionKind = TaskProjectionKinds.Supervisor, SessionAssignment? session = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
            Definition(completionMode, terminalInputsJson), teamId, userId,
            launchPayloadJson: null, scopeRepositoryIds: null,
            projectionKind: projectionKind, session, CancellationToken.None);
    }

    /// <summary>Tests run the engine inline (no Hangfire worker), so the dispatcher's Pending→Enqueued CAS is mirrored directly — same discipline as <c>ErrorRoutingFlowTests.ReEnqueueAsync</c>.</summary>
    private async Task ForceEnqueuedAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET status = 'Enqueued' WHERE id = {runId}");
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // start → end: the smallest legal graph — the run's clean Success stakes nothing, which is exactly the claim
    // the authority must refuse to terminalize for the Enforced cohort. terminalInputsJson declares the Terminal's
    // output mapping, which is what the run is recorded as having PRODUCED; the default declares nothing.
    private static WorkflowDefinition Definition(string? completionMode, string? terminalInputsJson = null) => new()
    {
        SchemaVersion = 1,
        CompletionMode = completionMode,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = terminalInputsJson is null ? WorkflowsTestSeed.EmptyJson() : WorkflowsTestSeed.Json(terminalInputsJson) },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };
}
