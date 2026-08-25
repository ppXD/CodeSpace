using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The <see cref="WorkflowRunDataOwnerKinds.ModelCall"/> facet's producer — the recording decorator over the LLM
/// client seam — against the real completeness writer and real Postgres (Rule 12 high fidelity). The facet is FIRST in
/// <see cref="RunDataManifestCoverage.RequiredFacets"/>, so no run folds a run-wide verdict without it.
///
/// <para><b>What only this tier can execute.</b> Every claim below is a database one: 0171 seeds this facet at a
/// DETERMINATE zero before the engine emits anything, 0148 computes the verdict from the deltas a producer folds in,
/// 0166's <c>masked_observed</c> latches the redacted arm monotonically, and 0146 refuses every complete verdict over a
/// NULL expectation. A unit tier can only assert the decorator's own arguments back at itself.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelCallCompletenessFlowTests
{
    private const string Secret = "sk-live-model-call-secret";

    private readonly PostgresFixture _fixture;

    public ModelCallCompletenessFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>
    /// THE DIRECTION A PRESENT-ONLY ADVANCE TURNS INTO A FALSE ASSURANCE. 0171 seeds this facet at expected=0
    /// present=0, so a producer that loses its declaration and states presence anyway lands present=1 over expected=0 —
    /// which 0148 reads as Exact, a complete verdict over a model call whose obligation nobody established. The
    /// producer must un-state the expectation instead, which 0146 refuses every complete verdict over.
    ///
    /// <para>The two arms are the two ways a declaration is lost. A REFUSED one is what the writer's own containment
    /// reports in production (a lost claim is returned as false, never thrown); a THROWN one is what an empty catch
    /// around the declaration made invisible. Neither may be followed by a presence.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lost_model_call_declaration_leaves_the_facet_indeterminate_instead_of_manufacturing_exact(bool thrown)
    {
        var run = await SeedInitializedRunAsync();

        using var scope = _fixture.BeginScope();
        var writer = new LostDeclarationWriter(scope.Resolve<IRunDataCompletenessWriter>(), thrown);

        await CallAsync(scope, run, writer, redactor: null, userPrompt: "no secret here");

        (await InteractionCountAsync(run)).ShouldBe(2,
            customMessage: "the premise: the capture floor is untouched and only the claim about it was lost, or this test asserts nothing about the fail direction");

        Describe(await StatementOrNullAsync(run)).ShouldBe("LegacyUnknown over expected=null present=0",
            customMessage: "a declaration nobody admitted may not be followed by a present-only delta: 0171 seeded expected=0, so that delta reads Exact over a model call the facet never undertook to hold");
    }

    /// <summary>The happy path, unchanged: declare one, land the rows, state the one that landed, and the facet folds complete over a determinate expectation.</summary>
    [Fact]
    public async Task A_model_call_that_declared_and_landed_states_a_complete_manifest()
    {
        var run = await SeedInitializedRunAsync();

        using var scope = _fixture.BeginScope();

        await CallAsync(scope, run, scope.Resolve<IRunDataCompletenessWriter>(), redactor: null, userPrompt: "no secret here");

        Describe(await StatementOrNullAsync(run)).ShouldBe("Exact over expected=1 present=1");
    }

    /// <summary>
    /// The masked flag is a claim about the BYTES this call persisted, not about whether a redactor was configured. A
    /// node scope carries a redactor for every call it makes and most of those calls contain no secret at all; feeding
    /// 0166's latch a constant true whenever one is configured makes the latch meaningless in the other direction,
    /// because it is monotonic and nothing later can take the claim back.
    /// </summary>
    [Theory]
    [InlineData(true, WorkflowRunCaptureCompleteness.RedactedExact)]
    [InlineData(false, WorkflowRunCaptureCompleteness.Exact)]
    public async Task The_redacted_arm_is_reached_only_when_the_redactor_actually_replaced_content(bool promptCarriesTheSecret, WorkflowRunCaptureCompleteness verdict)
    {
        var run = await SeedInitializedRunAsync();

        using var scope = _fixture.BeginScope();
        var prompt = promptCarriesTheSecret ? $"use {Secret} now" : "use nothing sensitive now";

        await CallAsync(scope, run, scope.Resolve<IRunDataCompletenessWriter>(), new PersistenceSecretRedactor([Secret]), prompt);

        (await StatementOrNullAsync(run)).ShouldNotBeNull().Verdict.ShouldBe(verdict,
            customMessage: "the redacted arm says the stored record has masked spans in it; a configured redactor that replaced nothing leaves a verbatim record");
    }

    /// <summary>One model call through the REAL decorator, the REAL run-record ledger and the REAL artifact offloader — only the completeness writer is ever substituted, and only to lose a claim the way production loses one.</summary>
    private static async Task CallAsync(ILifetimeScope scope, SeededRun run, IRunDataCompletenessWriter writer, PersistenceSecretRedactor? redactor, string userPrompt)
    {
        var decorator = new RecordingLLMClientDecorator(new EchoingPlainClient());
        var callScope = new LlmCallScope(run.RunId, run.TeamId, "start", "start#1", "llm.complete", scope.Resolve<IRunRecordLogger>(),
            scope.Resolve<IArtifactOffloader>(), CaptureRedactor: redactor, Completeness: writer);

        using (LlmCallContext.Push(callScope))
        {
            await decorator.CompleteAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "sys", UserPrompt = userPrompt }, CancellationToken.None);
        }
    }

    /// <summary>The facet's whole answer as one line, so a red run prints what was actually written rather than which assertion tripped first.</summary>
    private static string Describe(WorkflowRunDataManifest? statement) =>
        statement is null
            ? "absent"
            : $"{statement.Verdict} over expected={statement.ExpectedRecordCount?.ToString() ?? "null"} present={statement.PresentRecordCount}";

    private async Task<WorkflowRunDataManifest?> StatementOrNullAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.RunId && candidate.Facet == WorkflowRunDataOwnerKinds.ModelCall);
    }

    private async Task<int> InteractionCountAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(record => record.RunId == run.RunId && record.RecordType.StartsWith("interaction."));
    }

    /// <summary>A run whose manifest the engine has already initialized (0171), which is what makes expected=0 a determinate claim rather than an absent statement.</summary>
    private async Task<SeededRun> SeedInitializedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "model-call-completeness-" + Guid.NewGuid().ToString("N")[..6],
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        (await scope.Resolve<IRunDataCompletenessWriter>().InitializeAsync(new RunDataManifestInitialization(teamId, runId), CancellationToken.None)).ShouldBeTrue();

        return new SeededRun(teamId, runId);
    }

    private sealed record SeededRun(Guid TeamId, Guid RunId);

    /// <summary>The real writer with the producer's DECLARATION dropped — reported as a refusal, or thrown, which are the two ways the claim never lands. Every other facet and every other delta reaches the real writer untouched.</summary>
    private sealed class LostDeclarationWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;
        private readonly bool _thrown;

        public LostDeclarationWriter(IRunDataCompletenessWriter real, bool thrown) { _real = real; _thrown = thrown; }

        public async Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken)
        {
            if (advance.Facet != WorkflowRunDataOwnerKinds.ModelCall || advance.Expected == 0) return await _real.AdvanceAsync(advance, cancellationToken).ConfigureAwait(false);

            if (_thrown)
                throw new InvalidOperationException("the declaration never reached the manifest");

            return false;
        }

        public async Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) =>
            await _real.NoticeAsync(gap, cancellationToken).ConfigureAwait(false);

        public async Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            await _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false);
    }

    private sealed class EchoingPlainClient : ILLMClient
    {
        public string Provider => "plain";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = $"provider echoed {request.UserPrompt}", Model = request.Model });
    }
}
