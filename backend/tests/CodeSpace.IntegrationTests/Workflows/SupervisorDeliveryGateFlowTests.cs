using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): DC-2b (deliver-at-stop enforcement) end to end — the REAL <see cref="SupervisorTurnService"/>
/// + the REAL <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> publish step, opening
/// a PR through the REAL <see cref="Core.Services.PullRequests.IChangeSetService"/> against the test container's
/// <c>ProviderKind.Git</c> fake write capability (mirroring <c>RoomPullRequestServiceFlowTests</c> — a real GitHub/
/// GitLab call is reserved for the real-model E2E tier). Proves the full authorization ladder against a REAL
/// turn-by-turn drive: an operator pre-declaration opens a real PR at stop then lets the run complete; a pure
/// model proposal with no confirmation parks instead of opening one; a patch-only repo is skipped, not failed.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorDeliveryGateFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    private readonly PostgresFixture _fixture;

    public SupervisorDeliveryGateFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_operator_declared_pr_contract_opens_a_real_pr_at_stop_then_the_run_completes()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // P0-5 ledger-direct: a single accepted agent's own pushed manifest row is enough for I3's "published" —
        // no merge decision needed at all, so this test isolates DC-2b's OWN gate from I3's ladder.
        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        var goalConfig = GoalConfig(repoId, new DeliverySpec { OpenPullRequest = true });
        var decider = new AlwaysStopDecider();

        // Turn 1: I3 lets the stop through (published + summarized) — DC-2b then substitutes a server-authored publish.
        var publish = await RunTurnAsync(runId, teamId, decider, goalConfig);
        publish.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish, "the operator's own declaration authorizes opening a PR — DC-2b substitutes it before the stop can terminalize");

        var result = JsonSerializer.Deserialize<RoomPullRequestResult>(publish.OutcomeJson!, AgentJson.Options)!;
        var opened = result.PullRequests.Single();
        opened.Disposition.ShouldBe(RoomPullRequestDisposition.Opened);
        opened.Number.ShouldBe(777, "the test container's fake write capability always returns a fixed number");

        var manifest = (await ListManifestsAsync(runId, teamId)).Single(m => m.Kind == PublishManifestKind.Integration);
        manifest.PullRequestNumber.ShouldBe(777);

        // Turn 2: the SAME decider tries to stop again; the publish already succeeded — DC-2b lets it through.
        var stop = await RunTurnAsync(runId, teamId, decider, goalConfig);
        stop.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop, "every target is satisfied — the SECOND stop attempt is no longer rewritten");
    }

    [Fact]
    public async Task An_operator_declaration_combined_with_an_agreeing_plan_opens_a_pr_via_the_primary_effective_contract_path()
    {
        // Adversarial-sweep finding: every other happy-path test here uses AlwaysStopDecider (no plan at all),
        // which only exercises EffectiveOpenPullRequest's DEFENSIVE "no plan on the tape" fallback
        // (context.DeliverySpec?.OpenPullRequest) — never the PRIMARY path (ReadPlanDelivery off a REAL, clamped
        // plan payload) every actual Deep run takes, since a real run always plans first.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        var goalConfig = GoalConfig(repoId, new DeliverySpec { OpenPullRequest = true });
        var decider = new PlansTrueThenStopsDecider();

        await RunTurnAsync(runId, teamId, decider, goalConfig);   // turn 1: authors a plan (the operator's contract clamps into its payload)

        var publish = await RunTurnAsync(runId, teamId, decider, goalConfig);   // turn 2: tries to stop

        publish.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish, "the clamped plan's own delivery (read via ReadPlanDelivery, the primary path) authorizes the PR — never the fallback");

        var result = JsonSerializer.Deserialize<RoomPullRequestResult>(publish.OutcomeJson!, AgentJson.Options)!;
        result.PullRequests.Single().Disposition.ShouldBe(RoomPullRequestDisposition.Opened);
    }

    [Fact]
    public async Task A_second_round_of_work_merged_after_a_successful_publish_gets_its_own_fresh_pull_request()
    {
        // Adversarial-sweep finding (the most severe one, confirmed independently multiple times): the "already
        // satisfied" check must not trust a publish attempt's verdict once genuinely NEW work has landed since —
        // proven here end to end with a REAL second merge producing a REAL new branch after the first PR opened.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        var goalConfig = GoalConfig(repoId, new DeliverySpec { OpenPullRequest = true });
        var decider = new AlwaysStopDecider();

        var firstPublish = await RunTurnAsync(runId, teamId, decider, goalConfig);
        firstPublish.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish);
        JsonSerializer.Deserialize<RoomPullRequestResult>(firstPublish.OutcomeJson!, AgentJson.Options)!.PullRequests.Single().Disposition.ShouldBe(RoomPullRequestDisposition.Opened);

        // A SECOND, genuinely new round of accepted work lands on the SAME alias, on a NEW turn-scoped branch —
        // exactly the "later frontier" scenario a stale, unscoped publish lookup would silently skip.
        await SeedMergeAsync(runId, teamId, "codespace/integration/run/turn-later");

        var secondPublish = await RunTurnAsync(runId, teamId, decider, goalConfig);

        secondPublish.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish, "the second round's genuinely new branch must get its OWN publish attempt, not be silently covered by the first round's stale verdict");

        var secondResult = JsonSerializer.Deserialize<RoomPullRequestResult>(secondPublish.OutcomeJson!, AgentJson.Options)!;
        var reopened = secondResult.PullRequests.Single();
        reopened.Disposition.ShouldBe(RoomPullRequestDisposition.Opened, "the branch changed since the first PR — this must open a FRESH PR, not report AlreadyOpened against the stale one");

        var manifest = (await ListManifestsAsync(runId, teamId)).Single(m => m.Kind == PublishManifestKind.Integration);
        manifest.Branch.ShouldBe("codespace/integration/run/turn-later");

        var finalStop = await RunTurnAsync(runId, teamId, decider, goalConfig);
        finalStop.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop, "the second round's PR is now open too — the run may finally complete");
    }

    [Fact]
    public async Task A_plans_own_pr_proposal_with_no_confirmation_and_no_operator_declaration_parks_instead_of_opening_one()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        // No RequirePlanConfirmation, no operator DeliverySpec — the model's OWN plan-time proposal is the only
        // source naming a PR. It must never auto-execute.
        var goalConfig = GoalConfig(repoId, deliverySpec: null);
        var decider = new PlansTrueThenStopsDecider();

        await RunTurnAsync(runId, teamId, decider, goalConfig);   // turn 1: authors the plan (proposes true)

        var substituted = await RunTurnAsync(runId, teamId, decider, goalConfig);   // turn 2: tries to stop

        // H1: this run has NO conversation surface — a park would be unanswerable, so the gate force-stops with
        // the DISTINCT delivery diagnosis instead (never a silent auto-open, never a misleading NoProgress grind).
        substituted.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop, "a pure model proposal with neither a confirmed card nor an operator declaration must never auto-open a PR");
        SupervisorOutcome.ReadStopReason(substituted.PayloadJson).ShouldBe(SupervisorStopReasons.DeliveryAdjudicationUnavailable);
        substituted.PayloadJson.ShouldContain("never confirmed", customMessage: "the stop detail must name WHY, so a human knows to confirm or open it manually");

        (await ListManifestsAsync(runId, teamId)).ShouldNotContain(m => m.Kind == PublishManifestKind.Integration, "no PR was ever opened");
    }

    [Fact]
    public async Task An_opener_that_throws_unexpectedly_folds_into_a_diagnosed_failure_instead_of_crashing_the_turn()
    {
        // DC-2b sweep fix (ii) shipped unpinned: the executor's catch (Exception ex) when (ex is not
        // OperationCanceledException) had no test at any tier driving a genuinely THROWING opener through
        // ExecutePublishAsync. Proven here end to end with a REAL turn service + a DI-swapped opener that throws.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        var goalConfig = GoalConfig(repoId, new DeliverySpec { OpenPullRequest = true });
        var decider = new AlwaysStopDecider();

        var publish = await RunTurnAsync(runId, teamId, decider, goalConfig, b => b.RegisterType<ThrowingPullRequestOpener>().As<ISupervisorPullRequestOpener>());

        publish.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish, "the turn must still record a publish decision — an unexpected throw folds into the outcome, never crashes the turn loop mid-decision");

        var result = JsonSerializer.Deserialize<RoomPullRequestResult>(publish.OutcomeJson!, AgentJson.Options)!;
        var failed = result.PullRequests.Single();
        failed.Disposition.ShouldBe(RoomPullRequestDisposition.Failed);
        failed.Error.ShouldContain("boom");

        // The NEXT stop's gate reads this diagnosed failure and never blind-retries (mirrors the unit-level
        // ladder step, now proven with a REAL persisted outcome). H1: with NO conversation surface to park on,
        // that surfaces as the DISTINCT force-stop carrying the diagnosis — never another publish substitution.
        var stopped = await RunTurnAsync(runId, teamId, decider, goalConfig);
        stopped.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        SupervisorOutcome.ReadStopReason(stopped.PayloadJson).ShouldBe(SupervisorStopReasons.DeliveryAdjudicationUnavailable);
        stopped.PayloadJson.ShouldContain("boom");
    }

    [Fact]
    public async Task A_forced_publish_opens_the_pr_against_the_confirmed_plans_own_target_branch_not_the_repositorys_default()
    {
        // DC-2d sweep finding: the confirmation card names the LATEST PLAN's own clamped TargetBranch, but
        // execution used to read ONLY the operator's raw launch-time DeliverySpec — a plan-proposed branch the
        // operator never set would show on the card, then silently vanish at execution (the repo's default branch
        // opens instead). Proven here end to end: the operator names no branch at all, but the confirmed plan does.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        var goalConfig = GoalConfig(repoId, new DeliverySpec { OpenPullRequest = true });
        var decider = new PlansTrueThenStopsDecider("release");

        await RunTurnAsync(runId, teamId, decider, goalConfig);   // turn 1: plan proposes TargetBranch "release"

        var publish = await RunTurnAsync(runId, teamId, decider, goalConfig);   // turn 2: tries to stop

        publish.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish);

        var result = JsonSerializer.Deserialize<RoomPullRequestResult>(publish.OutcomeJson!, AgentJson.Options)!;
        result.PullRequests.Single().Disposition.ShouldBe(RoomPullRequestDisposition.Opened);

        using var verify = _fixture.BeginScope();
        verify.Resolve<TestPullRequestOpenCapture>().Last!.TargetBranch.ShouldBe("release", "the PR must target the branch the confirmation card actually showed the human, not the repository's default");
    }

    [Fact]
    public async Task A_patch_only_repository_parks_on_the_policy_conflict_and_completes_only_after_a_human_adjudicates()
    {
        // H1 (Skipped-as-satisfied fix): PatchOnly forbids the PR the operator ALSO required — two operator
        // intents conflict. The old behavior silently equated the skip with satisfaction; the honest behavior
        // surfaces the conflict once (ask_human naming patch-only), and the human's answer — content-blind, the
        // interim waiver until Phase T's structured WaivedByPolicy — releases the stop exactly once for this state.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var conversationId = await SeedConversationAsync(teamId, userId);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        await SetPatchOnlyAsync(repoId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        var goalConfig = GoalConfig(repoId, new DeliverySpec { OpenPullRequest = true });
        var decider = new AlwaysStopDecider();

        var publish = await RunTurnAsync(runId, teamId, decider, goalConfig, conversationId: conversationId);
        publish.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish);

        var result = JsonSerializer.Deserialize<RoomPullRequestResult>(publish.OutcomeJson!, AgentJson.Options)!;
        result.PullRequests.Single().Disposition.ShouldBe(RoomPullRequestDisposition.Skipped, "a patch-only repo is a deliberate policy choice, never a failure a human needs to fix");

        var parked = await RunTurnAsync(runId, teamId, decider, goalConfig, conversationId: conversationId);
        parked.DecisionKind.ShouldBe(SupervisorDecisionKinds.AskHuman, "policy forbids the required PR — the conflict goes to a human, never silently resolved as satisfied");
        JsonSerializer.Deserialize<SupervisorAskHumanPayload>(parked.PayloadJson, AgentJson.Options)!
            .Question.ShouldContain("patch-only", Case.Insensitive);

        await AnswerPendingAskAsync(runId, teamId, userId, "understood — patch-only is fine, finish without the PR");

        var reCheck = await RunTurnAsync(runId, teamId, decider, goalConfig, conversationId: conversationId);
        reCheck.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish, "the answer buys exactly one fresh re-attempt — had the human flipped the publish mode, THIS is where the real PR would open");

        var stop = await RunTurnAsync(runId, teamId, decider, goalConfig, conversationId: conversationId);
        stop.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop, "still policy-blocked after the adjudicated re-check — the answer stands as the interim waiver, the run completes");
    }

    /// <summary>A real conversation channel — without one the ask executor degrades (no card, no wait), so the park could never be answered.</summary>
    private async Task<Guid> SeedConversationAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var slug = "sup-delivery-" + Guid.NewGuid().ToString("N")[..8];
        return await scope.Resolve<Core.Services.Chat.IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, userId, CancellationToken.None);
    }

    /// <summary>Answers the run's single pending Action wait via the REAL token-correlated resume path (mirrors <c>SupervisorAskHumanFlowTests</c>' own helper).</summary>
    private async Task AnswerPendingAskAsync(Guid runId, Guid teamId, Guid actorUserId, string answer)
    {
        using var scope = _fixture.BeginScope();

        var token = (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .SingleAsync(w => w.RunId == runId && w.WaitKind == Messages.Constants.WorkflowWaitKinds.Action && w.Status == Messages.Constants.WorkflowWaitStatuses.Pending)).Token;

        var result = await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowResumeService>()
            .ResumeByActionTokenAsync(token, Core.Services.Supervisor.Executors.RealSupervisorActionExecutor.AnswerActionKey, actorUserId, answer, values: null, teamId, CancellationToken.None);

        result.ShouldBe(Core.Services.Workflows.Engine.ActionResumeResult.Resumed);
    }

    // ─── Drive a real turn ─────────────────────────────────────────────────────────

    private async Task<SupervisorPriorDecision> RunTurnAsync(Guid runId, Guid teamId, ISupervisorDecider decider, SupervisorGoalConfig goalConfig, Action<ContainerBuilder>? configureScope = null, Guid? conversationId = null)
    {
        using var scope = configureScope is null ? _fixture.BeginScope() : _fixture.BeginScope(configureScope);
        var service = NewTurnService(scope, decider);
        await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId, goalConfig, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderByDescending(d => d.Sequence).FirstAsync();

        return new SupervisorPriorDecision { Id = row.Id, Sequence = row.Sequence, DecisionKind = row.DecisionKind, Status = row.Status, PayloadJson = row.PayloadJson, OutcomeJson = row.OutcomeJson };
    }

    private static SupervisorGoalConfig GoalConfig(Guid repoId, DeliverySpec? deliverySpec) => new()
    {
        Goal = Goal, AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId }, DeliverySpec = deliverySpec,
    };

    private static SupervisorTurnService NewTurnService(ILifetimeScope scope, ISupervisorDecider decider) => new(
        scope.Resolve<ISupervisorDecisionLog>(),
        decider,
        scope.Resolve<ISupervisorActionExecutor>(),
        scope.Resolve<CodeSpaceDbContext>(),
        scope.Resolve<ISupervisorAcceptanceGrader>(),
        scope.Resolve<Core.Services.Decisions.IDecisionQueueService>(),
        scope.Resolve<Core.Services.Supervisor.Arbiter.IDecisionArbiter>(),
        scope.Resolve<Core.Services.Decisions.IDecisionAnswerService>(),
        scope.Resolve<Core.Services.Plans.IWorkPlanService>(),
        scope.Resolve<Core.Services.Workflows.Lifecycle.IRunRecordLogger>(),
        scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
        scope.Resolve<IPublishManifestStore>(),
        scope.Resolve<ISupervisorPublishedBranchResolver>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

    // ─── Deciders ───────────────────────────────────────────────────────────────────

    private sealed class AlwaysStopDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "completed", Summary = "shipped the feature" }, AgentJson.Options),
            });
    }

    /// <summary>Turn 1 authors a plan proposing <c>openPullRequest:true</c> (optionally naming its own <paramref name="targetBranch"/>, DC-2d); every later turn tries to stop.</summary>
    private sealed class PlansTrueThenStopsDecider : ISupervisorDecider
    {
        private readonly string? _targetBranch;

        public PlansTrueThenStopsDecider(string? targetBranch = null) => _targetBranch = targetBranch;

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(context.PriorDecisions.All(d => d.DecisionKind != SupervisorDecisionKinds.Plan)
                ? new SupervisorDecision
                {
                    Kind = SupervisorDecisionKinds.Plan,
                    PayloadJson = JsonSerializer.Serialize(new SupervisorPlanPayload
                    {
                        Goal = Goal,
                        Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "T", Instruction = "do it" } },
                        Delivery = new DeliverySpec { OpenPullRequest = true, TargetBranch = _targetBranch },
                    }, AgentJson.Options),
                }
                : new SupervisorDecision
                {
                    Kind = SupervisorDecisionKinds.Stop,
                    PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "completed", Summary = "shipped the feature" }, AgentJson.Options),
                });
    }

    /// <summary>DC-2d regression: a real dependency that throws, DI-swapped in for one turn to prove the executor's exception fold (RealSupervisorActionExecutor.Publish.cs) never crashes the turn loop.</summary>
    private sealed class ThrowingPullRequestOpener : ISupervisorPullRequestOpener
    {
        public Task<RoomPullRequestResult> OpenAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid? primaryRepositoryId, string? targetBranchOverride, string? currentTurnStopSummary, Guid? actorUserId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    // ─── Seeding (mirrors RoomPullRequestServiceFlowTests) ─────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "delivery-gate-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    /// <summary>Registered under <c>ProviderKind.Git</c> — the test container's <c>TestRepositoryProvider</c> gives it a real <c>IPullRequestWriteCapability</c> (fixed number 777, deterministic URL), so opening a PR never reaches a real GitHub/GitLab.</summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = $"https://local-{suffix}" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
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
            ExternalId = $"ext-{suffix}", NamespacePath = "org", Name = "repo", FullPath = $"org/repo-{suffix}",
            DefaultBranch = "main", WebUrl = $"https://local-{suffix}/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task SetPatchOnlyAsync(Guid repoId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var repo = await db.Repository.SingleAsync(r => r.Id == repoId);
        repo.PublishMode = RepositoryPublishMode.PatchOnly;
        await db.SaveChangesAsync();
    }

    private async Task SeedSpawnAsync(Guid runId, Guid teamId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Succeeded", ChangedFiles = new[] { "a.txt" } };
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentManifestAsync(Guid runId, Guid teamId, Guid agentRunId, Guid repositoryId, string branch)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId, WorkflowRunId = runId, RepositoryAlias = "primary", RepositoryId = repositoryId,
            Branch = branch, ChangedFileCount = 1, PublishStateValue = PublishState.Pushed,
        }, CancellationToken.None);
    }

    private async Task<IReadOnlyList<PublishManifest>> ListManifestsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None);
    }

    /// <summary>Hand-seeds a TERMINAL single-repo Merge decision's outcome — the exact JSON shape <c>SupervisorOutcome.ReadFinalIntegratedBranch</c> reads. Merge-derived resolution takes precedence over the P0-5 ledger-direct fallback, so this simulates a genuinely NEW second round of work superseding the first round's branch.</summary>
    private async Task SeedMergeAsync(Guid runId, Guid teamId, string integratedBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch, appliedCount = 1, reason = (string?)null, excludedAgents = Array.Empty<string>() } }, AgentJson.Options);

        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Merge, IdempotencyKey = $"merge-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }
}
