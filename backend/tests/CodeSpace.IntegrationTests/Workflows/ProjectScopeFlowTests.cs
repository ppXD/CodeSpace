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
/// End-to-end coverage for the <c>project.&lt;slug&gt;.&lt;name&gt;</c> resolution path
/// through the workflow engine on real Postgres. Closes the three-tier gap on PR #11's
/// Rule-11 missing-project-ref validator: unit tests pin the validator in isolation, this
/// suite proves it (and the happy-path resolver) work end-to-end through
/// <c>WorkflowEngine.LoadReferencedProjectVariablesAsync</c> against real DB rows.
///
/// <para>Observable signal: the Terminal node's Inputs are echoed into
/// <c>WorkflowRun.OutputsJson</c> by the engine (see <c>WorkflowEngine.WalkGraphAsync</c>
/// state.WorkflowOutputs = resolvedInputs). Each test seeds a Terminal whose Inputs
/// reference <c>{{project.&lt;slug&gt;.X}}</c> and inspects OutputsJson — that's the
/// post-resolution value the engine produced.</para>
///
/// <para>Coverage:</para>
/// <list type="number">
///   <item>Happy path — engine resolves <c>{{project.billing.API_BASE}}</c> to the project
///         variable's actual value; OutputsJson contains the literal URL.</item>
///   <item>Missing slug + default (Warn) — run completes Success; missing ref resolves to
///         JSON <c>null</c> in OutputsJson; no exception thrown.</item>
///   <item>Missing slug + Strict — run lands in Failure;
///         <see cref="MissingProjectRefException"/> details surface via
///         <c>WorkflowRun.Error</c>; no node ever runs (bootstrap-phase failure).</item>
/// </list>
///
/// <para>Env-var safety: tests SET <c>CODESPACE_MISSING_PROJECT_REF_ENFORCEMENT</c> on the
/// test process for Strict-mode coverage. <see cref="PostgresCollection"/> serialises every
/// IntegrationTests class so concurrent runs can't read a stale env. Dispose always clears.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProjectScopeFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    public ProjectScopeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>
    /// Clear the enforcement env var after every test — Strict-mode coverage sets it on the
    /// process, and we MUST NOT leak that into the next test (which would unexpectedly start
    /// throwing on the first missing-ref encounter).
    /// </summary>
    public void Dispose() => Environment.SetEnvironmentVariable(MissingProjectRefValidator.EnforcementEnvVar, null);

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
    public async Task Missing_project_slug_under_default_warn_mode_resolves_to_null_and_run_succeeds()
    {
        // PR #11's v0 rollout: when the env var is unset the validator defaults to Warn —
        // log + continue. The engine then hands the resolver an empty Projects bag for the
        // missing slug, and VariableResolver returns null for {{project.ghost.SOMETHING}}.
        // The Terminal's Inputs JSON ends up with the key mapped to JSON null — visible in
        // workflow_run.OutputsJson.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // No CreateProjectCommand → no "ghost" project in this team. Definition references it.
        var def = ProjectRefDefinition(refTemplate: "{{project.ghost.SOMETHING}}");

        // Make the default explicit (in case a previous test left it dirty).
        Environment.SetEnvironmentVariable(MissingProjectRefValidator.EnforcementEnvVar, null);

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "default (Warn) mode MUST NOT fail the run for a missing project ref. " +
                           "Failure here means either the validator's default flipped to Strict, or env-var dispose " +
                           "from a previous test leaked Strict into this process");
        run.Error.ShouldBeNull("Warn mode produces no run-row error");

        run.OutputsJson.ShouldNotBeNull();
        using var outputs = JsonDocument.Parse(run.OutputsJson!);
        outputs.RootElement.GetProperty("resolved").ValueKind.ShouldBe(JsonValueKind.Null,
            customMessage: "the missing project ref MUST resolve to JSON null in the resolved Inputs. " +
                           "If a literal string is here, the resolver is leaking the {{project.ghost.SOMETHING}} text; " +
                           "if the property is missing, the resolver is dropping the key entirely (which would surprise consumers)");
    }

    [Fact]
    public async Task Missing_project_slug_under_strict_mode_lands_run_in_failure_with_actionable_error()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var def = ProjectRefDefinition(refTemplate: "{{project.ghost.SOMETHING}}");

        // Flip to Strict — the validator throws MissingProjectRefException, which the engine
        // catches as a bootstrap-phase failure (per WorkflowEngine.ExecuteRunAsync's outer
        // catch) and translates into WorkflowRunStatus.Failure with a prefixed error message.
        Environment.SetEnvironmentVariable(MissingProjectRefValidator.EnforcementEnvVar, "strict");

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure,
            customMessage: "Strict mode MUST fail the run when a referenced project slug isn't found. " +
                           "Success here means the engine swallowed the validator's exception, or the env var didn't propagate to the engine's call site");

        run.Error.ShouldNotBeNull("Strict failure must surface a message on the run row for the operator to triage");
        run.Error.ShouldContain(nameof(MissingProjectRefException),
            customMessage: "the engine's bootstrap-failure wrapper prefixes the exception type name — operators grep the run row for the failure class");
        run.Error.ShouldContain("ghost",
            customMessage: "the validator's message must name the missing slug so the operator knows WHICH ref to remove from the definition");
        run.Error.ShouldContain(MissingProjectRefValidator.EnforcementEnvVar,
            customMessage: "the message must surface the env-var name so the operator can flip back to warn while triaging");

        // Defensive: zero node-statuses got recorded — the bootstrap failure happened BEFORE
        // the walker started, so no node ever ran. A non-empty map means the validator fires
        // after the walker has already started touching nodes (wrong wire point).
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
    /// for what the project resolver produced (concrete value, null, or — in strict — never).
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
