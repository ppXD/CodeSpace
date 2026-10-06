using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;
using Shouldly;

using CodeSpace.Tests.Fakes;
namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> rehydrate + the REAL DI-resolved
/// <see cref="SupervisorAcceptanceGrader"/> over the production local runner, with only a recording wrapper around
/// it): a supervisor plan whose subtask acceptance carries a <c>setupCommand</c> never makes the grader run it. The
/// setup argv really executes when it runs — it echoes a GUID token the recorder reads back off the process's own
/// stdout, which works the same with or without bubblewrap confinement — and the positive control proves the same
/// grader on the same captured world DOES run an operator's setup, so a green row is the boundary holding, never a
/// lane that could not have run a setup at all.
///
/// <para>Two rows: the payload this projector freezes from a model reply that passed the server's schema check, and a
/// ledger row written before the boundary existed (the knobs still in its stored bytes). The rehydrate re-reads the
/// ledger on every turn, so the old row must be as inert as the new one. The unit is repo-less with a captured
/// <c>ArtifactPresent</c> deliverable — the grade really materializes the world and runs the oracle, so the verdict
/// it folds is the proof the grading pipeline reached the step a setup would have run before.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorModelAcceptanceSetupFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "write the findings report";

    private readonly PostgresFixture _fixture;

    public SupervisorModelAcceptanceSetupFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(false)]   // the bytes this projector freezes from a fresh model reply
    [InlineData(true)]    // a row stored before the boundary existed, knobs and all
    public async Task A_supervisor_authored_setup_command_never_runs_when_the_unit_is_graded(bool storedBeforeTheBoundary)
    {
        if (OperatingSystem.IsWindows()) return;

        var probe = new SetupProbe();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var agentRunId = Guid.NewGuid();

        var planPayload = storedBeforeTheBoundary ? LegacyPlanPayload(probe.SetupArgv) : ProjectedPlanPayload(probe.SetupArgv);

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan, planPayload, "{}");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["s1"]}""", SpawnOutcome(agentRunId));
        await SeedCapturedDeliverableAsync(teamId, runId, agentRunId, "report.md", "# Findings\n");

        var runner = new RecordingRunner(Resolve<ISandboxRunnerRegistry>().Resolve(SandboxKinds.Local));
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(new SandboxRunnerRegistry([runner])).As<ISandboxRunnerRegistry>());

        var rehydrated = await RehydrateAsync(scope, runId, teamId);

        runner.Invocations.ShouldNotContain(run => run.Spec.Args.Any(arg => arg.Contains(probe.Token)), "the grader never handed the model-authored setup argv to the runner — check that every supervisor acceptance slot carries ModelAuthoredAcceptanceConverter");
        runner.Invocations.ShouldNotContain(run => run.Result.Stdout.Contains(probe.Token), "and nothing printed the probe token, so the argv never executed by any other route");

        var unit = SupervisorOutcome.ReadAgentResults(rehydrated.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson).Single();
        unit.AcceptancePassed.ShouldBe(true, $"the grade reached the oracle — the step a setup runs right before — and the captured report satisfied it (detail '{unit.AcceptanceDetail}')");
        unit.AcceptanceDetail.ShouldBe("artifacts-present");
    }

    [Fact]
    public async Task The_same_grader_on_the_same_world_runs_an_operator_setup_under_a_bounded_window()
    {
        // Positive control for the rows above: an operator's own spec still runs its setup step, really, in the
        // rebuilt world — so the marker staying absent above is the boundary, not a lane that never runs one. Its
        // authored 0 also proves the grade window is bounded inside the grader, not by the caller.
        if (OperatingSystem.IsWindows()) return;

        var probe = new SetupProbe();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var agentRunId = Guid.NewGuid();

        await SeedCapturedDeliverableAsync(teamId, runId, agentRunId, "report.md", "# Findings\n");

        var runner = new RecordingRunner(Resolve<ISandboxRunnerRegistry>().Resolve(SandboxKinds.Local));
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(new SandboxRunnerRegistry([runner])).As<ISandboxRunnerRegistry>());

        var operatorSpec = new SupervisorAcceptanceSpec { Command = new[] { "report.md" }, Kind = BenchmarkGradingKind.ArtifactPresent, SetupCommand = probe.SetupArgv, TimeoutSeconds = 0 };
        var grade = await scope.Resolve<ISupervisorAcceptanceGrader>().GradeCapturedAsync(new CapturedAcceptanceGradeRequest { AgentRunId = agentRunId, TeamId = teamId, Spec = operatorSpec, TimeoutSeconds = 0, Posture = null }, CancellationToken.None);

        grade.Passed.ShouldBeTrue(grade.Detail);

        var setup = runner.Invocations.ShouldHaveSingleItem("the setup is the only step on this lane that reaches the runner");
        setup.Result.Stdout.ShouldContain(probe.Token, Case.Sensitive, $"an operator setup really executes in the rebuilt world before the check (status {setup.Result.Status}, stderr '{setup.Result.Stderr}')");
        setup.Spec.TimeoutSeconds.ShouldBe(SupervisorLane.AcceptanceGradeTimeoutSeconds, "an authored 0 used to arm no wall clock at all; the grader now runs it under the default window");
    }

    // ── The plan payload, as the projector freezes it and as an older row stored it ─────────────────────────

    /// <summary>A model reply that reaches past the schema (setupCommand + a zero timeout), through the server's schema check, the decider's bind, and the projector — exactly the bytes a turn freezes into the ledger.</summary>
    private static string ProjectedPlanPayload(IReadOnlyList<string> setupArgv)
    {
        var acceptance = new JsonObject
        {
            ["command"] = new JsonArray("report.md"),
            ["kind"] = "ArtifactPresent",
            ["setupCommand"] = JsonSerializer.SerializeToNode(setupArgv),
            ["timeoutSeconds"] = 0,
        };
        var reply = JsonDocument.Parse(new JsonObject
        {
            ["kind"] = "plan",
            ["plan"] = new JsonObject { ["goal"] = Goal, ["subtasks"] = new JsonArray(new JsonObject { ["id"] = "s1", ["title"] = "Report", ["instruction"] = "write the findings report", ["expectsChanges"] = false, ["acceptance"] = acceptance }) },
        }.ToJsonString()).RootElement;

        JsonSchemaValidator.Validate(reply, SupervisorDecisionSchema.ResponseSchema).ShouldBeEmpty("fixture check: production's schema check accepts this reply");

        return SupervisorDecisionProjector.Project(reply.Deserialize<SupervisorModelDecision>(SupervisorDecisionSchema.Options)!).PayloadJson;
    }

    /// <summary>The same plan as a pre-boundary projector stored it: the knobs sit in the row's own bytes.</summary>
    private static string LegacyPlanPayload(IReadOnlyList<string> setupArgv)
    {
        var payload = JsonSerializer.Serialize(new
        {
            goal = Goal,
            subtasks = new[] { new { id = "s1", title = "Report", instruction = "write the findings report", acceptance = new { command = new[] { "report.md" }, kind = "ArtifactPresent", timeoutSeconds = 0, setupCommand = setupArgv }, expectsChanges = false } },
        }, AgentJson.Options);

        payload.ShouldContain("\"setupCommand\"", Case.Sensitive, "fixture check: the legacy row really carries the setup argv");

        return payload;
    }

    // ── Seeding and the real rehydrate ─────────────────────────────────────────────────────────────────────

    private T Resolve<T>() where T : notnull
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<T>();
    }

    private static string SpawnOutcome(Guid agentRunId)
    {
        var unit = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Succeeded", Summary = "wrote the findings report" };
        return JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { unit } }, AgentJson.Options);
    }

    private async Task<SupervisorTurnContext> RehydrateAsync(ILifetimeScope scope, Guid runId, Guid teamId)
    {
        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            scope.Resolve<ISupervisorDecider>(),
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<ISupervisorAcceptanceGrader>(),
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<ISupervisorPublishedBranchResolver>(), scope.Resolve<CodeSpace.Core.Services.Completion.ICompletionAssessmentComposer>(), new AdmitAllBudgetLedger(),
            scope.Resolve<CodeSpace.Core.Services.Learning.ILessonReader>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), scope.Resolve<ILogger<SupervisorTurnService>>(),
            rubricJudge: null,
            modes: scope.Resolve<CodeSpace.Core.Services.Completion.IModeProfileRegistry>());

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, AgentProfile = new SupervisorAgentProfile { RepositoryId = null } };

        return await service.RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig, CancellationToken.None);
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

    /// <summary>One durably captured deliverable for <paramref name="agentRunId"/>: CAS bytes plus the manifest row the captured lane rebuilds its world from.</summary>
    private async Task SeedCapturedDeliverableAsync(Guid teamId, Guid runId, Guid agentRunId, string path, string content)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var payload = System.Text.Encoding.UTF8.GetBytes(content);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var artifactId = Guid.NewGuid();

        db.WorkflowArtifact.Add(new WorkflowArtifact { Id = artifactId, TeamId = teamId, Sha256 = sha, ContentType = "text/markdown", SizeBytes = payload.Length, InlineBytes = payload, CreatedAt = now });
        db.ArtifactManifest.Add(new ArtifactManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = agentRunId, WorkflowRunId = runId, FenceEpoch = 1,
            Kind = ArtifactManifestKind.Document, LogicalPath = path, ContentArtifactId = artifactId,
            Sha256 = sha, SizeBytes = payload.Length, ContentType = "text/markdown",
            CreatedDate = now, LastModifiedDate = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-model-setup-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"write the findings report"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
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

    /// <summary>The production local runner with a record of every spec it was handed and what came back — the grader's one door to running anything.</summary>
    private sealed class RecordingRunner(ISandboxRunner inner) : ISandboxRunner
    {
        private readonly ConcurrentQueue<(SandboxSpec Spec, SandboxResult Result)> _invocations = new();

        public IReadOnlyList<(SandboxSpec Spec, SandboxResult Result)> Invocations => _invocations.ToList();

        public string Kind => inner.Kind;

        public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            _invocations.Enqueue((spec, result));
            return result;
        }
    }

    /// <summary>A setup argv that proves it EXECUTED: it prints a GUID token only a real run of it can produce. No OS artefact is left behind, and confinement (a private /tmp under bubblewrap) cannot hide the evidence.</summary>
    private sealed class SetupProbe
    {
        public string Token { get; } = "setup-ran-" + Guid.NewGuid().ToString("N");

        public IReadOnlyList<string> SetupArgv => new[] { "sh", "-c", $"echo {Token}" };
    }
}
