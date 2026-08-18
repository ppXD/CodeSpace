using CodeSpace.Messages.Workflows;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): the P2 terminal COMPARE-AND-SWAP at its own boundary — the verify→stamp window
/// has no seam a test could inject into, so the CAS is proven directly: it stamps only while the run is still
/// Running AND the ledger head still reads the version the arbitration composed over, both compared inside the
/// one statement that writes. A refused stamp leaves the row byte-untouched for the caller to park.
///
/// <para>The stamped row is COMPLETE — the run's declared outputs land in the same statement as the status. These
/// run against a scope where nothing else is tracked or dirty, which is exactly the condition under which the
/// outputs used to vanish: an <c>ExecuteUpdate</c> flushes no tracked state, so unless some unrelated writer in
/// the same context happened to save first, an Enforced run's declared outputs never reached the row.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TerminalStampCasFlowTests
{
    private readonly PostgresFixture _fixture;

    public TerminalStampCasFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_stable_ledger_stamps_the_terminal_exactly_once()
    {
        var (runId, _) = await SeedRunningRunAsync();
        using var scope = _fixture.BeginScope();
        var engine = (WorkflowEngine)scope.Resolve<IWorkflowEngine>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await CompletionLedgerVersionBump.BumpAsync(db, runId, CancellationToken.None);
        await CompletionLedgerVersionBump.BumpAsync(db, runId, CancellationToken.None);

        (await engine.TryStampArbitratedTerminalAsync(runId, Terminal(WorkflowRunStatus.Success, null, "completed", Declared), ledgerVersionRead: 2, CancellationToken.None))
            .ShouldBeTrue("the head reads exactly the version the arbitration composed over — the stamp lands");

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.Outcome.ShouldBe("completed");
        run.CompletedAt.ShouldNotBeNull();
        Answer(run.OutputsJson).ShouldBe("42", "the declared outputs are part of the terminal row — status, outcome and outputs are ONE row transition, so a run can never be Success with the outputs it produced missing");

        (await engine.TryStampArbitratedTerminalAsync(runId, Terminal(WorkflowRunStatus.Failure, "again", null, """{"answer":"clobbered"}"""), 2, CancellationToken.None))
            .ShouldBeFalse("the run is no longer Running — a terminal can never be stamped twice");

        Answer((await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).OutputsJson)
            .ShouldBe("42", "the losing racer writes nothing at all — its outputs must not overwrite the winner's");
    }

    [Fact]
    public async Task A_ledger_that_moved_past_the_arbitrated_version_refuses_the_stamp()
    {
        // THE race this slice closes: the arbitration composed over version 1, a fact landed (version 2) between
        // the app-level verify and the stamp — the stamp must fail so the caller parks, never terminalize a claim
        // about facts it did not read.
        var (runId, _) = await SeedRunningRunAsync();
        using var scope = _fixture.BeginScope();
        var engine = (WorkflowEngine)scope.Resolve<IWorkflowEngine>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await CompletionLedgerVersionBump.BumpAsync(db, runId, CancellationToken.None);
        await CompletionLedgerVersionBump.BumpAsync(db, runId, CancellationToken.None);   // the late fact

        (await engine.TryStampArbitratedTerminalAsync(runId, Terminal(WorkflowRunStatus.Success, null, "completed", Declared), ledgerVersionRead: 1, CancellationToken.None))
            .ShouldBeFalse("the head moved past the arbitrated version inside the race window");

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Running, "a refused stamp leaves the row untouched — the caller parks it deliberately");
        run.OutputsJson.ShouldBe("{}", "carrying the outputs into the statement must not weaken the predicate — a refused stamp writes NO column, outputs included");
    }

    [Fact]
    public async Task A_run_with_no_ledger_activity_stamps_at_version_zero()
    {
        // The watermark reads a missing head row as 0 — the CAS must agree, or every ledger-quiet run parks forever.
        var (runId, _) = await SeedRunningRunAsync();
        using var scope = _fixture.BeginScope();
        var engine = (WorkflowEngine)scope.Resolve<IWorkflowEngine>();

        (await engine.TryStampArbitratedTerminalAsync(runId, Terminal(WorkflowRunStatus.Failure, "honest failure", null, "{}"), ledgerVersionRead: 0, CancellationToken.None))
            .ShouldBeTrue();
    }

    /// <summary>The outputs a Terminal declared — what the run produced, and what the terminal row owes its readers.</summary>
    private const string Declared = """{"answer":"42"}""";

    /// <summary>Read the declared answer back out of a persisted <c>outputs_jsonb</c> — jsonb round-trips through its own canonical text, so the VALUE is the assertion, never the raw bytes.</summary>
    private static string? Answer(string outputsJson) => JsonDocument.Parse(outputsJson).RootElement.GetProperty("answer").GetString();

    private async Task<(Guid RunId, Guid TeamId)> SeedRunningRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
            {
                Name = "cas-" + Guid.NewGuid().ToString("N")[..8],
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
        await db.SaveChangesAsync();

        return (runId, teamId);
    }

    /// <summary>The four values the arbitrated CAS writes as ONE row transition — split apart is exactly how the outputs used to go missing.</summary>
    private static ArbitratedTerminal Terminal(WorkflowRunStatus status, string? error, string? outcome, string outputsJson) => new() { Status = status, Error = error, Outcome = outcome, OutputsJson = outputsJson };
}
