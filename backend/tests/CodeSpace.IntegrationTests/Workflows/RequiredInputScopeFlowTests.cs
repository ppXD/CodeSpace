using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end coverage for <see cref="MissingRequiredInputValidator"/> wired into the engine's
/// fresh-run scope build. Closes Rule 9 three-tier coverage on the required-input enforcement
/// — unit tests pin the validator in isolation, this suite proves the engine wiring + the
/// mode propagation through real Postgres.
///
/// <para>Observable signal: same as ProjectScopeFlowTests — the Terminal node echoes its
/// resolved Inputs into <c>WorkflowRun.OutputsJson</c>, so we can check what the resolver
/// did with the missing required input.</para>
///
/// <para>Coverage:</para>
/// <list type="number">
///   <item>Happy path — Required input supplied → run succeeds, OutputsJson carries the
///         resolved value.</item>
///   <item>Required input with Default + caller omitted — run succeeds (the Default
///         populates the bag; validator sees nothing missing).</item>
///   <item>Required input + no Default + caller omitted, Warn mode (default) — run
///         completes Success, missing ref resolves to null in OutputsJson, validator log
///         emitted but no exception.</item>
///   <item>Required input + no Default + caller omitted, Strict mode — run lands in
///         Failure with the validator's actionable error in <c>WorkflowRun.Error</c>;
///         no node ever runs (bootstrap-phase failure).</item>
/// </list>
///
/// <para>Env-var safety mirrors ProjectScopeFlowTests: tests SET
/// <c>CODESPACE_MISSING_REQUIRED_INPUT_ENFORCEMENT</c> on the process for Strict-mode
/// coverage; <see cref="PostgresCollection"/> serialises every IntegrationTests class so
/// concurrent runs cannot read a stale env; <see cref="IDisposable"/> always clears.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RequiredInputScopeFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    public RequiredInputScopeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    public void Dispose() => Environment.SetEnvironmentVariable(MissingRequiredInputValidator.EnforcementEnvVar, null);

    [Fact]
    public async Task Required_input_supplied_by_caller_resolves_to_value()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = SingleRequiredInputDefinition(hasDefault: false);
        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{"customer_email":"alice@example.com"}""");

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.OutputsJson.ShouldNotBeNull();
        using var outputs = JsonDocument.Parse(run.OutputsJson!);
        outputs.RootElement.GetProperty("resolved").GetString().ShouldBe("alice@example.com",
            customMessage: "Terminal echoes {{input.customer_email}} into OutputsJson; if it's not the supplied value, the resolver isn't seeing input scope correctly");
    }

    [Fact]
    public async Task Required_input_with_default_does_not_trip_validator_when_caller_omits()
    {
        // Required + Default is a legitimate shape: the field MUST be present in the bag,
        // but the Default fills it when callers omit. Validator MUST treat this as satisfied.
        // (A future codebase could decide otherwise — pin the current contract here so the
        // change is loud.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = SingleRequiredInputDefinition(hasDefault: true);

        // Set strict to make this assertion sharper — even under strict, default fills the
        // gap so validator does not throw.
        Environment.SetEnvironmentVariable(MissingRequiredInputValidator.EnforcementEnvVar, "strict");

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "Default fills the missing required input even under Strict — validator must NOT throw " +
                           "when BuildInputScope populated the name. A failure here means the validator is checking declaration " +
                           "instead of actual-bag presence");
        run.Error.ShouldBeNull();

        using var outputs = JsonDocument.Parse(run.OutputsJson!);
        outputs.RootElement.GetProperty("resolved").GetString().ShouldBe("fallback-default",
            customMessage: "Default value must reach the Terminal's resolved Inputs untouched");
    }

    [Fact]
    public async Task Missing_required_input_under_default_warn_mode_resolves_to_null_and_run_succeeds()
    {
        // Default (Warn) preserves backward compat: the validator logs a warning but the
        // run still succeeds. The missing input shows up as null in OutputsJson because
        // BuildInputScope omitted the key entirely.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = SingleRequiredInputDefinition(hasDefault: false);

        Environment.SetEnvironmentVariable(MissingRequiredInputValidator.EnforcementEnvVar, null);

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "Default (Warn) mode MUST NOT fail the run for a missing required input. " +
                           "A Failure here means either the validator's default flipped to Strict, or the env-var dispose " +
                           "from a previous test leaked Strict into this process");
        run.Error.ShouldBeNull("Warn mode produces no run-row error");

        using var outputs = JsonDocument.Parse(run.OutputsJson!);
        outputs.RootElement.GetProperty("resolved").ValueKind.ShouldBe(JsonValueKind.Null,
            customMessage: "BuildInputScope omitted the key → resolver returns null → Terminal's Inputs serialize as null. " +
                           "If a literal string is here, the resolver is leaking the {{input.customer_email}} text");
    }

    [Fact]
    public async Task Missing_required_input_under_strict_mode_lands_run_in_failure_with_actionable_error()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = SingleRequiredInputDefinition(hasDefault: false);

        Environment.SetEnvironmentVariable(MissingRequiredInputValidator.EnforcementEnvVar, "strict");

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure,
            customMessage: "Strict mode MUST fail the run when a Required input is missing AND has no Default. " +
                           "Success here means the engine swallowed the validator's exception, or the env var didn't propagate to the engine's call site");

        run.Error.ShouldNotBeNull("Strict failure must surface a message on the run row for the operator to triage");
        run.Error.ShouldContain(nameof(MissingRequiredInputException),
            customMessage: "the engine's bootstrap-failure wrapper prefixes the exception type name — operators grep the run row for the failure class");
        run.Error.ShouldContain("customer_email",
            customMessage: "the validator's message must name the missing input so the operator knows WHICH declaration to add a Default to / which caller to update");
        run.Error.ShouldContain(MissingRequiredInputValidator.EnforcementEnvVar,
            customMessage: "the message must surface the env-var name so the operator can flip back to warn while triaging");

        // Defensive: zero node-statuses got recorded — the bootstrap failure happened BEFORE
        // the walker started, so no node ever ran. A non-empty node map means the validator
        // wired in too late (mid-walk rather than at scope-build).
        var nodes = await LoadRunNodesAsync(runId);
        nodes.ShouldBeEmpty(
            customMessage: "scope-build failure must short-circuit BEFORE any node executes — a non-empty node map here means the validator wired in too late");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Definition with a single Required input "customer_email" → Terminal that echoes the
    /// resolved value into OutputsJson. <paramref name="hasDefault"/> toggles whether the
    /// declaration carries a Default value.
    /// </summary>
    private static WorkflowDefinition SingleRequiredInputDefinition(bool hasDefault)
    {
        var inputDecl = new WorkflowVariable
        {
            Name = "customer_email",
            Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""),
            Required = true,
            Default = hasDefault ? JsonDocument.Parse("\"fallback-default\"").RootElement.Clone() : null,
        };

        var terminalInputsJson = JsonSerializer.Serialize(new { resolved = "{{input.customer_email}}" });

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Inputs = new[] { inputDecl },
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
            Name = "req-input-" + Guid.NewGuid().ToString("N")[..8],
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
