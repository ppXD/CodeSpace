using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Projects;
using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end coverage for the <c>project.&lt;slug&gt;.&lt;name&gt;</c> resolution path through the
/// workflow engine on real Postgres. Unit tests pin <see cref="MissingProjectRefValidator"/> in
/// isolation; this suite proves it (and the happy-path resolver) work end-to-end through
/// <c>WorkflowEngine.LoadReferencedProjectVariablesAsync</c> against real DB rows.
///
/// <para>Observable signal: the Terminal node's Inputs are echoed into <c>WorkflowRun.OutputsJson</c>
/// by the engine. Each test seeds a Terminal whose Inputs reference <c>{{project.&lt;slug&gt;.X}}</c>
/// and inspects OutputsJson — that's the post-resolution value the engine produced.</para>
///
/// <para>Coverage:</para>
/// <list type="number">
///   <item>Happy path — engine resolves <c>{{project.billing.API_BASE}}</c> to the project
///         variable's actual value; OutputsJson contains the literal URL.</item>
///   <item>Missing slug — the validator throws; the run lands in Failure with
///         <see cref="MissingProjectRefException"/> details on <c>WorkflowRun.Error</c>; no node
///         ever runs (bootstrap-phase failure).</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProjectScopeFlowTests
{
    private readonly PostgresFixture _fixture;
    public ProjectScopeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Project_slug_variable_resolves_to_concrete_value_in_terminal_outputs()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Create a Project + a project-scoped variable. ProjectService.SlugifyName lowercases
        // and hyphenates the input — "Billing" deterministically becomes "billing". The
        // workflow definition below references the slug "billing" verbatim.
        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand { Name = "Billing" });
            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "API_BASE",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"https://api.billing.example.com\"").RootElement,
            });
        }

        var def = ProjectRefDefinition(refTemplate: "{{project.billing.API_BASE}}");
        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.OutputsJson.ShouldNotBeNull();

        using var outputs = JsonDocument.Parse(run.OutputsJson!);
        outputs.RootElement.GetProperty("resolved").GetString().ShouldBe("https://api.billing.example.com",
            customMessage: "Terminal's Inputs include {{project.billing.API_BASE}}; the engine resolves that against NodeRunScope.Projects[\"billing\"] " +
                           "and echoes the resolved value into workflow_run.OutputsJson. A different (or null) value here means either the engine never loaded the project, " +
                           "or VariableResolver.WalkProjectsScope failed to walk slug → name");
    }

    [Fact]
    public async Task Missing_project_slug_fails_the_run_with_actionable_error()
    {
        // No CreateProjectCommand → no "ghost" project in this team, but the definition references
        // it. The validator throws MissingProjectRefException, which the engine catches as a
        // bootstrap-phase failure and records on WorkflowRun.Error, instead of resolving the ref to null.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var def = ProjectRefDefinition(refTemplate: "{{project.ghost.SOMETHING}}");

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure,
            customMessage: "A referenced project slug that isn't found MUST fail the run. Success here means the engine swallowed " +
                           "the validator's exception, or the validator never wired into scope-build");

        run.Error.ShouldNotBeNull("the failure must surface a message on the run row for the operator to triage");
        run.Error.ShouldContain(nameof(MissingProjectRefException),
            customMessage: "the engine's bootstrap-failure wrapper prefixes the exception type name — operators grep the run row for the failure class");
        run.Error.ShouldContain("ghost",
            customMessage: "the validator's message must name the missing slug so the operator knows WHICH ref to remove from the definition");

        // Defensive: zero node-statuses got recorded — the bootstrap failure happened BEFORE the
        // walker started, so no node ever ran. A non-empty map means the validator fires too late.
        var nodes = await LoadRunNodesAsync(runId);
        nodes.ShouldBeEmpty(
            customMessage: "scope-build failure must short-circuit BEFORE any node executes — a non-empty node map here means the validator " +
                           "wired in too late (mid-walk rather than at scope-build)");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal definition for project-ref tests: trigger → terminal, where the Terminal's
    /// Inputs carry the <paramref name="refTemplate"/>. The engine echoes resolved Terminal
    /// Inputs into <c>WorkflowRun.OutputsJson</c>, giving every test a direct observable
    /// for what the project resolver produced (concrete value, or — on a missing slug — never).
    /// </summary>
    private static WorkflowDefinition ProjectRefDefinition(string refTemplate)
    {
        var terminalInputsJson = JsonSerializer.Serialize(new { resolved = refTemplate });
        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(terminalInputsJson) }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "end" }
            }
        };
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "proj-scope-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private async Task<Dictionary<string, WorkflowRunNode>> LoadRunNodesAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToDictionaryAsync(n => n.NodeId);
    }
}
