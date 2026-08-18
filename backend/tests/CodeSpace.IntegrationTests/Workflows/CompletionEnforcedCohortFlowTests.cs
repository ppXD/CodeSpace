using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
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
/// terminalizing — the exact protocol the isolated canary cohort exists to exercise — while a definition without
/// the opt-in behaves byte-identically to before (Shadow pass-through), and an unreadable opt-in never stores.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CompletionEnforcedCohortFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionEnforcedCohortFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_enforced_definitions_unbacked_success_parks_instead_of_terminalizing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionEnforcementMode.ShouldBe("Enforced", "the definition's opt-in must reach the run row through the real RunStarter");
        run.Status.ShouldBe(WorkflowRunStatus.Suspended, "an Enforced claim nothing qualified must park, never terminalize");
        run.Error.ShouldNotBeNull();
        run.Error.ShouldContain("completion-authority", customMessage: "the park must name its arbiter — check workflow_run.error for the decision detail");
        // P4: a bare trigger→terminal graph is the GENERIC mode — no registered conformance profile, so the
        // authority now parks it at the mode gate (before the zero-staked compose even runs), naming the mode.
        run.Error.ShouldContain("mode 'generic'", customMessage: "the park reason must name the unregistered operating mode");
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

        run.CompletionEnforcementMode.ShouldBe("Shadow", "no opt-in inherits the platform default");
        run.Status.ShouldBe(WorkflowRunStatus.Success, "outside the cohort, behavior is byte-identical to before the flip existed");
    }

    [Fact]
    public async Task A_snapshot_run_of_an_enforced_definition_stamps_and_parks_too()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
                Definition(WorkflowDefinition.CompletionModeEnforced), teamId, userId,
                launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
        }

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionEnforcementMode.ShouldBe("Enforced", "the snapshot lane resolves the same opt-in from its frozen definition json");
        run.Status.ShouldBe(WorkflowRunStatus.Suspended);
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

        var enforcedRunId = await RunEnforcedToCleanSuccessAsync(teamId, userId);

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
        var runId = await RunEnforcedToCleanSuccessAsync(teamId, userId);
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

        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced, """{"answer":"{{team.API_KEY}}"}"""));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);

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
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);

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
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced, DeclaredOutputs));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);
        await StampProjectionKindAsync(runId, CodeSpace.Messages.Tasks.TaskProjectionKinds.Supervisor);

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

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>The Terminal's declared output mapping — a run that reaches it produces <c>{"answer":"42"}</c>, the value the terminal row owes every reader of <c>workflow_run.outputs_jsonb</c>.</summary>
    private const string DeclaredOutputs = """{"answer":"42"}""";

    /// <summary>
    /// An Enforced run whose contract world is answered BEFORE its first walk — staked acceptance/delivery/output,
    /// a graded merged supervisor tape, a pushed manifest on a live repository — so the engine's clean Success is
    /// arbitrated CleanSuccess and terminalizes through the arbitrated compare-and-swap on the FIRST pass, with no
    /// park in between. Returns the run id.
    /// </summary>
    private async Task<Guid> RunEnforcedToCleanSuccessAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced, DeclaredOutputs));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);
        await StampProjectionKindAsync(runId, CodeSpace.Messages.Tasks.TaskProjectionKinds.Supervisor);

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

    private async Task StampProjectionKindAsync(Guid runId, string kind)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.ProjectionKind = kind;
        await db.SaveChangesAsync();
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

    private async Task SeedManifestAsync(Guid teamId, Guid agentRunId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
            RepositoryAlias = "primary", Branch = "codespace/agent/s1", BaseSha = "b1", CommitSha = "c1",
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
