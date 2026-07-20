using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): P2b-1 — the ONE production owner of the terminal SUCCESS claim
/// (<see cref="ICompletionTerminalAuthority"/>), arbitrating AT the terminal boundary (run still Running).
/// Pins: Legacy/Shadow and every non-Success claim pass through VERBATIM (Lock Clause 1's cohort gate);
/// an Enforced Success claim maps the sealed six-state decision onto the run vocabulary — honest failure
/// demotes with a named reason, the full predicate alone stays Success, and an unsettled obligation parks
/// (never a fake Success, never a fake Failure). Nothing here writes the run row — the engine's
/// CompleteRunAsync consumes the arbitration.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CompletionTerminalAuthorityFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionTerminalAuthorityFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_shadow_run_and_a_non_success_claim_pass_through_verbatim()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Shadow");

        using var scope = _fixture.BeginScope();
        var authority = scope.Resolve<ICompletionTerminalAuthority>();

        var shadow = await authority.ArbitrateAsync(runId, teamId, "Shadow", WorkflowRunStatus.Success, CancellationToken.None);
        shadow.Status.ShouldBe(WorkflowRunStatus.Success);
        shadow.Decision.ShouldBeNull("only the Enforced cohort is arbitrated — Lock Clause 1's activation gate");

        var failure = await authority.ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Failure, CancellationToken.None);
        failure.Status.ShouldBe(WorkflowRunStatus.Failure);
        failure.Decision.ShouldBeNull("the engine's own Failure is already an honest non-success — the authority guards only the SUCCESS claim");
    }

    [Fact]
    public async Task An_enforced_success_claim_with_a_failed_oracle_demotes_to_honest_failure()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await SeedGradedTapeAsync(runId, teamId, acceptancePassed: false);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Failure, "an engine-Success run whose own oracle FAILED can never terminalize as Success under Enforced");
        arbitration.Decision.ShouldBe(TerminalDecision.HonestFailure);
        arbitration.Reason.ShouldNotBeNull();
        arbitration.Reason!.ShouldContain("honest failure");
    }

    [Fact]
    public async Task An_enforced_success_claim_with_the_full_predicate_stays_success()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        var attemptId = await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Decision.ShouldBe(TerminalDecision.CleanSuccess, "solved + verified + captured + delivered + reachable — the FULL predicate");
        arbitration.Status.ShouldBe(WorkflowRunStatus.Success);
        arbitration.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task An_enforced_success_claim_with_an_unsettled_obligation_parks()
    {
        // Acceptance passed but the staked delivery/output never settled (no manifest) — Unknown obligations
        // park the run for a human; never a fake Success, never a fake Failure.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Suspended);
        arbitration.Decision.ShouldBe(TerminalDecision.NeedsReview);
        arbitration.Reason!.ShouldContain("NeedsReview");
    }

    [Fact]
    public async Task A_read_only_unit_with_authorized_NA_stakes_reaches_clean_success()
    {
        // P2b-2 closes the read-only park hole: the declared no-changes unit stakes delivery/output as
        // ServerPolicy-AUTHORIZED-NotApplicable — explicitly authorized off, never silently absent — so under
        // Enforced it terminalizes CleanSuccess instead of parking on an Unknown artifact.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeNaAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeNaAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Decision.ShouldBe(TerminalDecision.CleanSuccess);
        arbitration.Status.ShouldBe(WorkflowRunStatus.Success);
    }

    [Fact]
    public void The_engine_chokepoint_arbitrates_before_the_terminal_write()
    {
        // Lock Clause 1's architecture pin: CompleteRunAsync must consult the authority BEFORE any status write —
        // a new terminal writer (or a reorder that writes first) breaks this pin and must argue itself in review.
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "backend/src/CodeSpace.Core/Services/Workflows/Engine/WorkflowEngine.cs"));
        var body = source[source.IndexOf("private async Task CompleteRunAsync", StringComparison.Ordinal)..];

        body.IndexOf("ArbitrateAsync", StringComparison.Ordinal).ShouldBeGreaterThan(0);
        body.IndexOf("ArbitrateAsync", StringComparison.Ordinal).ShouldBeLessThan(body.IndexOf("run.Status =", StringComparison.Ordinal), "arbitration precedes the terminal write");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // ── Seeds (the composer flow tests' shapes, at the RUNNING boundary) ──

    private async Task<Guid> SeedRunningRunAsync(Guid teamId, Guid userId, string mode)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
            {
                Name = "authority-" + Guid.NewGuid().ToString("N")[..8],
                Description = null,
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var seed = _fixture.BeginScope();
        var db = seed.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Status = WorkflowRunStatus.Running;
        run.CompletionPolicyVersion = CompletionPolicy.CurrentVersion;
        run.CompletionEnforcementMode = mode;
        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> SeedGradedTapeAsync(Guid runId, Guid teamId, bool acceptancePassed)
    {
        var attemptId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed, acceptanceDetail = acceptancePassed ? null : "tests-failed-exit-1", acceptanceEvidenceId = (Guid?)Guid.NewGuid(), producedBranch = "codespace/agent/s1" } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Stop, "{}", "{}");
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

    /// <summary>A live team-bound repository — the handoff probe's reachability target (an alias-only manifest fails CLOSED by design).</summary>
    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = CodeSpace.Messages.Enums.ProviderKind.GitLab, DisplayName = "instance",
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

    private async Task StakeNaAsync(Guid runId, Guid teamId, string requirementRef, string kind)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = requirementRef, Kind = kind, Requiredness = Requiredness.ServerPolicyAuthorizedNotApplicable, Authority = ContractAuthority.ServerPolicy, ContractSchemaVersion = "1" },
        }, CancellationToken.None);
    }

    private async Task StakeAsync(Guid runId, Guid teamId, string requirementRef, string kind)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = requirementRef, Kind = kind, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);
    }
}
