using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Plans;

/// <summary>
/// Real-model E2E for the plan-authoring half of triad S1 (the GitHub-Actions real-model lane style): a LIVE
/// model drives the PRODUCTION <see cref="LlmWorkflowPlanner"/> — real structured wire, real
/// <c>PlannerSchema</c> commit-contract including the NEW per-subtask dependsOn/acceptance +
/// hasEnoughContext fields — and the result is persisted through the REAL <c>WorkPlanService</c> over real
/// Postgres. Assertions are STRUCTURAL (a schema-conformant plan landed as a durable work_plan version);
/// whether the model chose to author acceptance/DAG edges is REPORTED, never gated (model-dependent).
/// Gated on <c>CODESPACE_LLM_*</c> (green-skip when absent); a wire fault is non-gating via
/// <see cref="RealModelGate"/>, mirroring the sibling real-model suites.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
public sealed class RealModelPlanAuthorE2ETests
{
    private const string Custom = "Custom";

    /// <summary>A goal that NAMES an objective check + a deliverable — inviting (not forcing) the model to author per-subtask acceptance.</summary>
    private const string Goal =
        "Fix the failing normalizePhone() util in this repo so that running `sh check.sh` passes, " +
        "then write a short summary of the fix to summary.md.";

    private readonly PostgresFixture _fixture;

    public RealModelPlanAuthorE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_live_model_authors_a_plan_that_persists_as_a_work_plan_version()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip green

        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();   // the run link is a soft reference — no engine run needed at this tier

        await RealModelGate.AssessLiveAsync(Custom, async () =>
        {
            using var scope = _fixture.BeginScope();

            var planner = new LlmWorkflowPlanner(
                RealModelLiveWire.Registry(),
                RealModelLiveWire.Selector(model, RealModelLiveWire.Credential(Custom, baseUrl, apiKey)),
                scope.Resolve<IAgentHarnessRegistry>());

            PlannedWorkflow plan;
            try
            {
                plan = await planner.PlanAsync(new WorkflowPlanRequest { TaskText = Goal, TeamId = teamId }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or CodeSpace.Core.Services.Workflows.Llm.LlmApiException)
            {
                return (true, $"the live planner produced no plan (gateway infra / non-conformant reply) — not gating: {ex.Message}");
            }

            // ── STRUCTURAL gates: the commit-contract held on a live wire. ──
            plan.Goal.ShouldNotBeNullOrWhiteSpace();
            plan.Subtasks.Count.ShouldBeInRange(1, 20);
            plan.Subtasks.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Title) && !string.IsNullOrWhiteSpace(s.Instruction));

        // ── The durable artifact through the REAL store. ──
            var saved = await scope.Resolve<IWorkPlanService>().SaveVersionAsync(new WorkPlanDraft
            {
                TeamId = teamId,
                WorkflowRunId = runId,
                OriginKind = WorkPlanOrigins.Node,
                Goal = plan.Goal,
                Items = plan.Subtasks.Select(WorkPlanItem.From).ToList(),
                SuccessCriteria = plan.SuccessCriteria,
                Risks = plan.Risks,
            }, CancellationToken.None);

            saved.Version.ShouldBe(1);

            using var verify = _fixture.BeginScope();
            var row = await verify.Resolve<CodeSpaceDbContext>().WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId);
            row.Goal.ShouldNotBeNullOrWhiteSpace();

            // The CONTRACT half is reported, not gated: a model may decline to author acceptance/DAG edges.
            var withAcceptance = plan.Subtasks.Count(s => s.Acceptance is { Command.Count: > 0 });
            var withDeps = plan.Subtasks.Count(s => s.DependsOn is { Count: > 0 });

            return (true, $"live plan persisted: {plan.Subtasks.Count} subtask(s), {withAcceptance} with acceptance, {withDeps} with dependsOn, hasEnoughContext={plan.HasEnoughContext}");
        });
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"rmplan-{userId:N}@test.local", Name = $"rmplan-{userId:N}" });
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"rmplan-{teamId:N}", Name = "RM Plan Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }
}
