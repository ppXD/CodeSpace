using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Proves the recorder's REAL persistence path: a structured model call routed through the
/// <see cref="RecordingLLMClientDecorator"/> over the REAL <see cref="IRunRecordLogger"/> + real DbContext +
/// real <see cref="IArtifactOffloader"/> (the exact production wiring the supervisor's brain call uses) actually
/// lands a correlated <c>interaction.started</c> + <c>interaction.completed</c> pair in <c>workflow_run_record</c>,
/// with the kind / provider / model / usage the call ran with. The unit tests cover the decorator over a FAKE
/// logger; this closes the gap where a real-container break (bad column, EF mapping, missing migration) would ship
/// green because fail-open swallows the write error.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunRecordInteractionFlowTests
{
    private static readonly string[] InteractionTypes =
    {
        WorkflowRunRecordTypes.InteractionStarted,
        WorkflowRunRecordTypes.InteractionCompleted,
        WorkflowRunRecordTypes.InteractionFailed,
    };

    private readonly PostgresFixture _fixture;

    public RunRecordInteractionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_recorded_model_call_persists_a_correlated_started_completed_pair_to_the_ledger()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Drive the REAL decorator over the REAL scoped logger + offloader — the production write path.
        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            var offloader = scope.Resolve<IArtifactOffloader>();
            var decorator = new RecordingLLMClientDecorator(new FakeStructuredClient());

            using (LlmCallContext.Push(new LlmCallScope(runId, teamId, "sup", "sup#turn1", "supervisor.decision", logger, offloader)))
            {
                await decorator.CompleteStructuredAsync(BuildRequest(), CancellationToken.None);
            }
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var rows = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && InteractionTypes.Contains(r.RecordType))
            .OrderBy(r => r.Sequence)
            .ToListAsync();

        rows.Select(r => r.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionCompleted },
            "the real persistence path lands the started+completed pair — not just a fake-logger assertion");
        rows[0].CorrelationId.ShouldNotBeNull();
        rows[0].CorrelationId.ShouldBe(rows[1].CorrelationId, "the triple is paired by one correlation id on the real ledger");
        rows.ShouldAllBe(r => r.IterationKey == "sup#turn1");

        var started = JsonDocument.Parse(rows[0].PayloadJson).RootElement;
        started.GetProperty("kind").GetString().ShouldBe("supervisor.decision");
        started.GetProperty("provider").GetString().ShouldBe("anthropic");
        started.GetProperty("model").GetString().ShouldBe("claude-x");
        started.GetProperty("prompt").GetProperty("system").GetString().ShouldBe("SYS");

        var completed = JsonDocument.Parse(rows[1].PayloadJson).RootElement;
        completed.GetProperty("usage").GetProperty("inputTokens").GetInt32().ShouldBe(10);
        completed.GetProperty("usage").GetProperty("outputTokens").GetInt32().ShouldBe(5);
        completed.GetProperty("output").GetProperty("decision").GetString().ShouldBe("plan", "the completion JSON is captured inline when small");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static StructuredLLMCompletionRequest BuildRequest() => new()
    {
        Model = "claude-x",
        SystemPrompt = "SYS",
        UserPrompt = "USR",
        JsonSchema = JsonDocument.Parse("{}").RootElement,
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "interaction-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private sealed class FakeStructuredClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "t", Model = "claude-x" });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new StructuredLLMCompletion
        {
            Json = JsonSerializer.SerializeToElement(new { decision = "plan" }),
            Model = "claude-x",
            Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5, FinishReason = "stop" },
        });
    }
}
