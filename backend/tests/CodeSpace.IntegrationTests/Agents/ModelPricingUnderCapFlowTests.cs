using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.ModelCredentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.ModelCredentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (D1, high fidelity — real Postgres, real migration columns, real production mediator path, real
/// <see cref="SupervisorTurnService"/>): <b>price every pool model, and fail closed under a cost cap.</b>
///
/// <para>The refutation this file exists to make impossible: a Codex/OpenAI pool run with a $5 cap that spends past
/// it and still terminalizes Success. It could, because an unpriced model's spend summed as <c>?? 0m</c> forever, so
/// <c>RunSpendUsd &gt; MaxCostUsd</c> never became true. Here a capped run on an unpriced model FORCE-STOPS with a
/// reason that names the model; the same model PRICED accounts normally and does not stop.</para>
///
/// <para>Also pinned end to end: the two new <c>model_credential_model</c> columns round-trip through the production
/// add/price/list mediator path (so the migration and the EF mapping agree), the team-scoped price load, and the
/// team bill now including brain-plane spend.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ModelPricingUnderCapFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship it";
    private const string UnpricedPoolModel = "gpt-5.4-codex";   // absent from the built-in price table by design

    private readonly PostgresFixture _fixture;

    public ModelPricingUnderCapFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ── The migration + the production API round-trip ────────────────────────────────

    [Fact]
    public async Task The_two_price_columns_round_trip_through_the_add_and_price_endpoints()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credentialId = await SeedCredentialAsync(teamId, "OpenAI");

        // ADD carries the price, because the editor reconciles a renamed row as remove-then-add — dropping the
        // price there would silently re-break the cap the operator just fixed.
        Guid rowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            rowId = await scope.Resolve<IMediator>().Send(new AddCredentialedModelCommand
            {
                ModelCredentialId = credentialId,
                ModelId = UnpricedPoolModel,
                InputUsdPerMillion = 2m,
                OutputUsdPerMillion = 10m,
            });

        (await ListModelsAsync(userId, teamId, credentialId)).Single(m => m.Id == rowId).ShouldSatisfyAllConditions(
            m => m.InputUsdPerMillion.ShouldBe(2m),
            m => m.OutputUsdPerMillion.ShouldBe(10m));

        // RE-PRICE through the dedicated endpoint (fractional cents must survive the numeric column exactly).
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await scope.Resolve<IMediator>().Send(new SetCredentialedModelPriceCommand
            {
                ModelCredentialId = credentialId,
                ModelRowId = rowId,
                InputUsdPerMillion = 0.075m,
                OutputUsdPerMillion = 0.3m,
            });

        (await ListModelsAsync(userId, teamId, credentialId)).Single(m => m.Id == rowId).ShouldSatisfyAllConditions(
            m => m.InputUsdPerMillion.ShouldBe(0.075m, "an unconstrained numeric keeps fractional-cent precision"),
            m => m.OutputUsdPerMillion.ShouldBe(0.3m));

        // CLEAR (both null) — there is no separate clear verb.
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await scope.Resolve<IMediator>().Send(new SetCredentialedModelPriceCommand { ModelCredentialId = credentialId, ModelRowId = rowId });

        (await ListModelsAsync(userId, teamId, credentialId)).Single(m => m.Id == rowId).ShouldSatisfyAllConditions(
            m => m.InputUsdPerMillion.ShouldBeNull(),
            m => m.OutputUsdPerMillion.ShouldBeNull());
    }

    [Fact]
    public async Task Half_a_price_is_rejected_rather_than_stored()
    {
        // Storing one side would make the row LOOK priced in the UI while a capped run still refuses to spend on
        // it — the operator must see the mistake at the edit, not as a mysterious forced stop an hour later.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credentialId = await SeedCredentialAsync(teamId, "OpenAI");
        var rowId = await SeedModelRowAsync(credentialId, UnpricedPoolModel);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        await Should.ThrowAsync<ArgumentException>(() => scope.Resolve<IMediator>().Send(new SetCredentialedModelPriceCommand
        {
            ModelCredentialId = credentialId,
            ModelRowId = rowId,
            InputUsdPerMillion = 2m,   // output omitted
        }));
    }

    // ── The team-scoped price load ───────────────────────────────────────────────────

    [Fact]
    public async Task The_price_map_is_team_scoped_takes_the_conservative_MAX_and_keeps_a_disabled_rows_price()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var credA1 = await SeedCredentialAsync(teamA, "OpenAI");
        var credA2 = await SeedCredentialAsync(teamA, "OpenAI");
        var credB = await SeedCredentialAsync(teamB, "OpenAI");

        await SeedModelRowAsync(credA1, UnpricedPoolModel, input: 2m, output: 10m);
        await SeedModelRowAsync(credA2, UnpricedPoolModel, input: 3m, output: 9m);          // same id, a dearer key
        await SeedModelRowAsync(credA1, "hidden-model", input: 1m, output: 1m, enabled: false);
        await SeedModelRowAsync(credA1, "half-priced-model", input: 1m, output: null);      // not a price
        await SeedModelRowAsync(credB, "team-b-only", input: 99m, output: 99m);

        using var scope = _fixture.BeginScope();
        var prices = await ModelPriceResolver.LoadAsync(scope.Resolve<CodeSpaceDbContext>(), teamA, CancellationToken.None);

        prices[UnpricedPoolModel].InputPerMillionUsd.ShouldBe(3m, "two credentials price it differently — under a cap the safe direction is the DEARER one");
        prices[UnpricedPoolModel].OutputPerMillionUsd.ShouldBe(10m, "each side takes its own max");
        prices.ShouldContainKey("hidden-model", "a disabled model's price still bills the spend already on the books");
        prices.ShouldNotContainKey("half-priced-model", "one side set is not a price");
        prices.ShouldNotContainKey("team-b-only", "another team's price must never enter this team's map (tenancy fail-closed)");
    }

    [Fact]
    public async Task A_revoked_credentials_prices_leave_the_map()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credentialId = await SeedCredentialAsync(teamId, "OpenAI");
        await SeedModelRowAsync(credentialId, UnpricedPoolModel, input: 2m, output: 10m);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var credential = await db.ModelCredential.SingleAsync(c => c.Id == credentialId);
            credential.Status = CredentialStatus.Revoked;
            credential.DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using var read = _fixture.BeginScope();
        (await ModelPriceResolver.LoadAsync(read.Resolve<CodeSpaceDbContext>(), teamId, CancellationToken.None))
            .ShouldNotContainKey(UnpricedPoolModel, "revoking drops the key, so nothing can spend on its models any more");
    }

    // ── THE CROWN JEWEL: a capped run cannot spend on an unpriced model ──────────────

    [Fact]
    public async Task A_capped_run_that_spent_on_an_UNPRICED_model_force_stops_naming_the_model()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // The tape already carries a settled wave that BURNED 5M input tokens on an unpriced Codex model. Its
        // realized spend therefore sums to $0 — which is precisely why the cost cap alone can never trip.
        await SeedSettledSpawnAsync(runId, teamId, UnpricedPoolModel, inputTokens: 5_000_000, outputTokens: 250_000);

        var context = await RehydrateAsync(runId, teamId, Capped(5m));

        context.RunSpendUsd.ShouldBe(0m, "the defect's mechanism: an unpriced model contributes ?? 0m, so 'spend > cap' reads false forever");
        context.UnpricedSpendModel.ShouldBe(UnpricedPoolModel, "…and this is the signal that makes that $0 legible as a lie");

        var result = await RunSpawningTurnAsync(runId, teamId, Capped(5m));

        result.IsFinished.ShouldBeTrue("the run stops on its own unenforceable cap rather than spending unbounded");
        result.TerminalReason.ShouldBe(SupervisorStopReasons.UnpricedModelUnderCap);
        (await LatestStopDetailAsync(runId, teamId)).ShouldSatisfyAllConditions(
            detail => detail.ShouldContain(UnpricedPoolModel, Case.Sensitive, "an operator cannot act on a stop that doesn't name the model"),
            detail => detail.ShouldContain("model manager", Case.Insensitive, "…nor on one that doesn't name the remedy"));
    }

    [Fact]
    public async Task The_SAME_model_PRICED_accounts_normally_and_does_not_stop()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var credentialId = await SeedCredentialAsync(teamId, "OpenAI");

        // The operator priced it: $2/M in, $10/M out. 5M in + 0.25M out = $10 + $2.50 = $12.50.
        await SeedModelRowAsync(credentialId, UnpricedPoolModel, input: 2m, output: 10m);
        await SeedSettledSpawnAsync(runId, teamId, UnpricedPoolModel, inputTokens: 5_000_000, outputTokens: 250_000);

        var context = await RehydrateAsync(runId, teamId, Capped(50m));

        context.UnpricedSpendModel.ShouldBeNull("priced ⇒ nothing unpriceable is on the books");
        context.RunSpendUsd.ShouldBe(12.5m, "the row price makes a Codex model bill like any other");
        context.ModelPrices.ShouldContainKey(UnpricedPoolModel);

        var result = await RunSpawningTurnAsync(runId, teamId, Capped(50m));

        result.TerminalReason.ShouldNotBe(SupervisorStopReasons.UnpricedModelUnderCap, "there is nothing unpriced left to refuse");
    }

    [Fact]
    public async Task An_UNCAPPED_run_on_the_same_unpriced_model_is_never_stopped_by_this_rule()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSettledSpawnAsync(runId, teamId, UnpricedPoolModel, inputTokens: 5_000_000, outputTokens: 250_000);

        var context = await RehydrateAsync(runId, teamId, new SupervisorGoalConfig { Goal = Goal });

        context.UnpricedSpendModel.ShouldBe(UnpricedPoolModel, "the honesty qualifier is still reported…");

        var result = await RunSpawningTurnAsync(runId, teamId, new SupervisorGoalConfig { Goal = Goal });

        result.TerminalReason.ShouldNotBe(SupervisorStopReasons.UnpricedModelUnderCap, "…but with no cap there is nothing to enforce — byte-identical to before D1");
    }

    [Fact]
    public async Task Brain_plane_spend_is_folded_for_an_UNCAPPED_run_too()
    {
        // It used to be DB-gated on a cap being set, so an uncapped run's reported spend silently omitted every
        // dollar the supervisor's own brain spent. The bill has to be the bill either way.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedInteractionAsync(runId, "supervisor.decision", "claude-opus-4-8", inputTokens: 1_000_000, outputTokens: 0);   // $5

        var context = await RehydrateAsync(runId, teamId, new SupervisorGoalConfig { Goal = Goal });

        context.BrainPlaneSpendUsd.ShouldBe(5m);
        context.RunSpendUsd.ShouldBe(5m, "the brain's dollars are part of the run's spend whether or not anyone capped it");
        context.BrainPlaneSpendByKind["supervisor.decision"].ShouldBe(5m);
    }

    // ── The team bill covers both lanes ─────────────────────────────────────────────

    [Fact]
    public async Task The_team_rollup_now_includes_brain_plane_spend_without_changing_what_the_agent_number_means()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedTerminalAgentAsync(teamId, runId, "claude-opus-4-8", input: 1_000_000, output: 0);            // agent lane: $5
        await SeedInteractionAsync(runId, "supervisor.decision", "claude-opus-4-8", 400_000, 0);               // brain lane: $2
        await SeedInteractionAsync(runId, "critic.review", "claude-sonnet-4-6", 1_000_000, 0);                 // brain lane: $3

        using var scope = _fixture.BeginScope();
        var rollup = await scope.Resolve<ITeamCostService>().ComputeRollupAsync(teamId, since: null, CancellationToken.None);

        rollup.EstimatedCostUsd.ShouldBe(5m, "the pre-existing number keeps its EXACT prior meaning — agent execution only");
        rollup.BrainPlaneUsd.ShouldBe(5m, "the supervisor decision + the critic review, priced by the SAME pricer");
        rollup.TotalUsd.ShouldBe(10m, "what the team actually paid");

        var run = rollup.Runs.Single(r => r.WorkflowRunId == runId);
        run.EstimatedCostUsd.ShouldBe(5m);
        run.BrainPlaneUsd.ShouldBe(5m);
        run.TotalUsd.ShouldBe(10m);
    }

    [Fact]
    public async Task A_run_that_spent_ONLY_on_its_brain_still_appears_in_the_bill()
    {
        // A supervisor that planned, deliberated, and stopped without ever spawning an agent used to vanish from
        // the rollup entirely — "no agent rows" is not "cost nothing".
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedInteractionAsync(runId, "supervisor.decision", "claude-opus-4-8", 1_000_000, 0);   // $5

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<ITeamCostService>();

        var rollup = await service.ComputeRollupAsync(teamId, since: null, CancellationToken.None);
        rollup.Runs.ShouldContain(r => r.WorkflowRunId == runId);
        rollup.TotalUsd.ShouldBe(5m);
        rollup.EstimatedCostUsd.ShouldBeNull("no agent lane spend at all — null is distinct from a real $0");

        (await service.ComputeRunAsync(teamId, runId, CancellationToken.None)).TotalUsd.ShouldBe(5m);
        (await service.ComputeRunsAsync(teamId, new[] { runId }, CancellationToken.None))[runId].BrainPlaneUsd.ShouldBe(5m);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private static SupervisorGoalConfig Capped(decimal cap) => new() { Goal = Goal, MaxCostUsd = cap };

    private async Task<IReadOnlyList<Messages.Dtos.ModelCredentials.CredentialedModelSummary>> ListModelsAsync(Guid userId, Guid teamId, Guid credentialId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ListCredentialedModelsQuery { ModelCredentialId = credentialId });
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id,
            TeamId = teamId,
            Provider = provider,
            DisplayName = $"{provider} cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("sk-test"),
            Status = CredentialStatus.Active,
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedModelRowAsync(Guid credentialId, string modelId, decimal? input = null, decimal? output = null, bool enabled = true)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel
        {
            Id = id,
            ModelCredentialId = credentialId,
            ModelId = modelId,
            Source = ModelSource.Manual,
            Enabled = enabled,
            InputUsdPerMillion = input,
            OutputUsdPerMillion = output,
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        Guid workflowId;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
            {
                Name = "d1-pricing-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = new Messages.Dtos.Workflows.WorkflowDefinition
                {
                    SchemaVersion = 1,
                    Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                    {
                        new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                        new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
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

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    /// <summary>Put a TERMINAL spawn decision on the ledger whose folded agent result burned real tokens on <paramref name="model"/> — the durable fact the spend fold reads.</summary>
    private async Task SeedSettledSpawnAsync(Guid runId, Guid teamId, string model, int inputTokens, int outputTokens)
    {
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<ISupervisorDecisionLog>();

        var claim = await ledger.TryClaimAsync(new SupervisorDecisionClaimRequest
        {
            SupervisorRunId = runId,
            TeamId = teamId,
            DecisionKind = SupervisorDecisionKinds.Spawn,
            IdempotencyKey = $"d1-{Guid.NewGuid():N}",
            InputHash = "h",
            PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { "s1" } }, AgentJson.Options),
            FenceEpoch = 1,
        }, CancellationToken.None);

        var outcome = JsonSerializer.Serialize(new
        {
            agentCount = 1,
            agentResults = new[]
            {
                new SupervisorAgentResult
                {
                    AgentRunId = Guid.NewGuid(),
                    Status = nameof(AgentRunStatus.Succeeded),
                    Model = model,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    // Deliberately produces NO work: an agent that burned tokens and shipped nothing is exactly the
                    // runaway-spend shape a cost cap exists for, and it keeps the publish-or-park gate out of the
                    // way so the forced stop under test is what terminalizes the run.
                },
            },
        }, AgentJson.Options);

        // The real state machine is Pending → Running → Succeeded; a seed that skips the claim-to-execute CAS is
        // rejected outright, so drive the same transitions production does.
        (await ledger.TryBeginExecutionAsync(claim.DecisionId, teamId, CancellationToken.None)).ShouldBeTrue();

        await ledger.RecordTerminalAsync(claim.DecisionId, teamId, SupervisorDecisionStatus.Succeeded, outcome, error: null, CancellationToken.None);
    }

    private async Task SeedInteractionAsync(Guid runId, string kind, string model, int inputTokens, int outputTokens)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            RecordType = WorkflowRunRecordTypes.InteractionCompleted,
            NodeId = NodeId,
            IterationKey = "",
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new { kind, model, usage = new { inputTokens, outputTokens } }),
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedTerminalAgentAsync(Guid teamId, Guid runId, string model, int input, int output)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = runId,
            Harness = "claude-code",
            Status = AgentRunStatus.Succeeded,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = Goal, Harness = "claude-code", Model = model }, AgentJson.Options),
            ResultJson = JsonSerializer.Serialize(new AgentRunResult
            {
                Status = AgentRunStatus.Succeeded,
                ExitReason = "completed",
                TokenUsage = new AgentTokenUsage { InputTokens = input, OutputTokens = output },
            }, AgentJson.Options),
        });

        await db.SaveChangesAsync();
    }

    /// <summary>The <c>detail</c> the forced stop stamped on its own ledger payload — the operator-facing sentence, read back off the durable tape rather than trusted from memory.</summary>
    private async Task<string> LatestStopDetailAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<ISupervisorDecisionLog>().GetForRunAsync(runId, teamId, CancellationToken.None);

        return rows.Where(r => r.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(r => r.Sequence)
            .Select(r => SupervisorOutcome.ReadStopDetail(r.PayloadJson))
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "";
    }

    private async Task<SupervisorTurnContext> RehydrateAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig)
    {
        using var scope = _fixture.BeginScope();
        return await BuildService(scope, scope.Resolve<ISupervisorDecider>()).RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig, CancellationToken.None);
    }

    /// <summary>Run one turn with a decider that ALWAYS spawns, so the BOUND — never the decider — is what stops the run.</summary>
    private async Task<SupervisorTurnResult> RunSpawningTurnAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig)
    {
        using var scope = _fixture.BeginScope();
        return await BuildService(scope, new AlwaysSpawnDecider()).RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig, CancellationToken.None);
    }

    private static SupervisorTurnService BuildService(ILifetimeScope scope, ISupervisorDecider decider) =>
        new(scope.Resolve<ISupervisorDecisionLog>(), decider, scope.Resolve<ISupervisorActionExecutor>(), scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<ISupervisorAcceptanceGrader>(), scope.Resolve<IDecisionQueueService>(), scope.Resolve<Core.Services.Supervisor.Arbiter.IDecisionArbiter>(), scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<Core.Services.Plans.IWorkPlanService>(), scope.Resolve<Core.Services.Workflows.Lifecycle.IRunRecordLogger>(),
            scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<Core.Services.Agents.Publish.IPublishManifestStore>(),
            scope.Resolve<ISupervisorPublishedBranchResolver>(), scope.Resolve<Core.Services.Completion.ICompletionAssessmentComposer>(),
            scope.Resolve<Core.Services.Workflows.Budget.IBudgetLedger>(), scope.Resolve<Core.Services.Learning.ILessonReader>(),
            scope.Resolve<ILogger<SupervisorTurnService>>());

    private sealed class AlwaysSpawnDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Spawn,
                PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { "next" } }, AgentJson.Options),
            });
    }

    private const string SupervisorGraphJson = """
    {"nodes":[{"id":"sup","type":"agent.supervisor","config":{"goal":"ship it"}}],"edges":[]}
    """;
}
