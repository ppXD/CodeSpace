using Autofac;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — what one cell (a node at an iteration key) of a run is recorded as having failed on, read from real rows over real
/// Postgres: the ledger written by the real <see cref="IRunRecordLogger"/> from the real exceptions, and agent runs whose stored result is
/// the one the real executor writes. This is the reader a container asks for the cell that ended it, so what is pinned is its boundary:
/// the cell's own newest record wins, the cell's last agent run is the fallback, and nothing outside the cell — another iteration, another
/// node, another run, another team — is ever read. A cell a rerun carries into its fork keeps its account: the real <see cref="IRerunCellSeeder"/>
/// re-emits a failed cell with the failure the reader answers for it on the original run, so the fork answers for it as the original did.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CellFailureReaderFlowTests
{
    private const string Branch = "map#0";

    private readonly PostgresFixture _fixture;

    public CellFailureReaderFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_cells_own_record_wins_over_its_agent_run()
    {
        var (teamId, runId) = await SeedRunAsync();
        await RecordFailureAsync(runId, "list", Branch, new CredentialDisconnectedException(Guid.NewGuid(), "own"));
        await SeedAgentRunAsync(teamId, runId, "list", Branch, FailureOf(new CredentialNeedsReconnectException(Guid.NewGuid(), "agent", "invalid_grant")));

        var failure = await ReadAsync(runId, teamId, "list", Branch);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe(FailureCodes.CredentialDisconnected, "what the node itself recorded is the first account");
    }

    [Fact]
    public async Task A_cell_whose_record_carries_no_failure_is_traced_through_the_agent_run_that_last_worked_in_it()
    {
        var (teamId, runId) = await SeedRunAsync();
        var credentialId = Guid.NewGuid();
        await RecordFailureAsync(runId, "boom", Branch, exception: null);
        await SeedAgentRunAsync(teamId, runId, "boom", Branch, FailureOf(new CredentialDisconnectedException(Guid.NewGuid(), "earlier attempt")), createdMinutesAgo: 10);
        await SeedAgentRunAsync(teamId, runId, "boom", Branch, FailureOf(new CredentialNeedsReconnectException(credentialId, "last attempt", "invalid_grant")), createdMinutesAgo: 1);

        var failure = await ReadAsync(runId, teamId, "boom", Branch);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect, "only the LAST attempt speaks for the cell: an earlier one that was respawned says nothing about why it ended");
        failure.DetailGuid("credentialId").ShouldBe(credentialId);

        await SeedAgentRunAsync(teamId, runId, "boom", Branch, failure: null, createdMinutesAgo: 0);

        (await ReadAsync(runId, teamId, "boom", Branch)).ShouldBeNull("the newest attempt failed on something else, so the older failures are not why the cell ended");
    }

    [Fact]
    public async Task The_newest_failed_record_of_a_cell_is_the_one_that_speaks()
    {
        var (teamId, runId) = await SeedRunAsync();
        await RecordFailureAsync(runId, "list", Branch, new CredentialDisconnectedException(Guid.NewGuid(), "first walk"));
        await RecordFailureAsync(runId, "list", Branch, exception: null);

        (await ReadAsync(runId, teamId, "list", Branch)).ShouldBeNull("the cell failed again, on something else, on a later walk");

        await RecordFailureAsync(runId, "list", Branch, new CredentialNeedsReconnectException(Guid.NewGuid(), "third walk", "invalid_grant"));

        (await ReadAsync(runId, teamId, "list", Branch))!.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
    }

    [Fact]
    public async Task A_cell_answers_only_for_itself_not_for_another_iteration_node_run_or_team()
    {
        var (teamId, runId) = await SeedRunAsync();
        var (otherTeamId, otherRunId) = await SeedRunAsync();
        await RecordFailureAsync(runId, "list", "map#1", new CredentialDisconnectedException(Guid.NewGuid(), "another branch"));
        await RecordFailureAsync(runId, "other", Branch, new CredentialDisconnectedException(Guid.NewGuid(), "another node"));
        await RecordFailureAsync(otherRunId, "list", Branch, new CredentialDisconnectedException(Guid.NewGuid(), "another run"));
        await SeedAgentRunAsync(teamId, runId, "list", "map#1", FailureOf(new CredentialDisconnectedException(Guid.NewGuid(), "another branch's agent run")));
        await SeedAgentRunAsync(teamId, runId, "other", Branch, FailureOf(new CredentialDisconnectedException(Guid.NewGuid(), "another node's agent run")));
        await SeedAgentRunAsync(otherTeamId, runId, "list", Branch, FailureOf(new CredentialDisconnectedException(Guid.NewGuid(), "another team's agent run")));

        (await ReadAsync(runId, teamId, "list", Branch)).ShouldBeNull("nothing in this cell recorded a failure — and nothing outside it is evidence");
        (await ReadAsync(runId, teamId, "list", string.Empty)).ShouldBeNull("the top-level cell of the same node is another cell");
        (await ReadAsync(runId, teamId, "list", "map#1"))!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "fixture check: the reader does read the cell the record belongs to");
    }

    [Fact]
    public async Task An_agent_run_of_the_same_node_and_iteration_in_another_run_of_the_team_is_not_this_runs_evidence()
    {
        var (teamId, runId) = await SeedRunAsync();
        var (_, anotherRunId) = await SeedRunAsync(teamId);   // a team runs the same workflow again and again: the node and the iteration key recur in every run of it
        await SeedAgentRunAsync(teamId, anotherRunId, "boom", Branch, FailureOf(new CredentialDisconnectedException(Guid.NewGuid(), "another run's agent run")));

        (await ReadAsync(runId, teamId, "boom", Branch)).ShouldBeNull("the agent run belongs to another run of the team, so it says nothing about why this run's cell ended");
        (await ReadAsync(anotherRunId, teamId, "boom", Branch))!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "fixture check: the reader does read the run the agent run belongs to");
    }

    [Theory]
    [InlineData(false, false)]   // a kept top-level cell, whose own record carries the failure
    [InlineData(false, true)]    // a kept top-level cell whose node worded its failure alone: its agent run holds the account
    [InlineData(true, false)]    // a map's sibling branch cell, whose own record carries the failure
    [InlineData(true, true)]     // a map's sibling branch cell whose node worded its failure alone
    public async Task A_failed_cell_a_rerun_carries_into_its_fork_is_recorded_as_failing_on_what_the_original_was(bool siblingBranch, bool viaTheAgentRun)
    {
        var (teamId, originalRunId) = await SeedRunAsync();
        var (_, forkRunId) = await SeedRunAsync(teamId);
        var iterationKey = siblingBranch ? Branch : string.Empty;
        var credentialId = Guid.NewGuid();
        var failing = new CredentialNeedsReconnectException(credentialId, "GitLab · NACHO.L", "invalid_grant");

        if (viaTheAgentRun)
        {
            await RecordFailureAsync(originalRunId, "list", iterationKey, exception: null);
            await SeedAgentRunAsync(teamId, originalRunId, "list", iterationKey, FailureOf(failing));
        }
        else
            await RecordFailureAsync(originalRunId, "list", iterationKey, failing);

        (await ReadAsync(forkRunId, teamId, "list", iterationKey)).ShouldBeNull("fixture check: the fork has no account of the cell until the rerun carries it there");

        await SeedForkAsync(originalRunId, forkRunId, siblingBranch);

        var carried = await ReadAsync(forkRunId, teamId, "list", iterationKey);

        carried.ShouldNotBeNull("the fork's own record of the cell carries what the original run said it failed on — the agent run that worked in it belongs to the original run, not the fork");
        carried.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        carried.DetailGuid("credentialId").ShouldBe(credentialId);
        (await ReadForkRecordAsync(forkRunId, "list", iterationKey)).Error.ShouldBe("it failed", "the sentence is carried as it was");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_cell_that_recorded_no_failure_is_carried_into_a_fork_exactly_as_it_was(bool siblingBranch)
    {
        var (teamId, originalRunId) = await SeedRunAsync();
        var (_, forkRunId) = await SeedRunAsync(teamId);
        var iterationKey = siblingBranch ? Branch : string.Empty;

        await RecordFailureAsync(originalRunId, "list", iterationKey, exception: null);
        await SeedForkAsync(originalRunId, forkRunId, siblingBranch);

        var carried = await ReadForkRecordAsync(forkRunId, "list", iterationKey);

        carried.Error.ShouldBe("it failed");
        carried.HasFailureKey.ShouldBeFalse("a cell with nothing to say about a connection carries no failure key: its record is the one it always was");
        (await ReadAsync(forkRunId, teamId, "list", iterationKey)).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"error":"it failed"}""")]
    [InlineData("""{"error":"it failed","failure":"credential_disconnected"}""")]
    [InlineData("""{"error":"it failed","failure":{"details":{"credentialId":"x"}}}""")]
    [InlineData("""{"error":"it failed","failure":{"code":""}}""")]
    public async Task A_record_that_carries_no_readable_failure_is_no_failure_and_never_an_error(string payload)
    {
        var (teamId, runId) = await SeedRunAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeFailed, NodeId = "list", IterationKey = Branch, PayloadJson = payload, OccurredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        (await ReadAsync(runId, teamId, "list", Branch)).ShouldBeNull();
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private async Task<PersistedFailure?> ReadAsync(Guid runId, Guid teamId, string nodeId, string iterationKey)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<ICellFailureReader>().ReadAsync(runId, teamId, nodeId, iterationKey, CancellationToken.None);
    }

    private static PersistedFailure FailureOf(Exception exception) => ProviderFailureCapture.From(exception, repositoryId: null)!;

    private async Task RecordFailureAsync(Guid runId, string nodeId, string iterationKey, Exception? exception)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<IRunRecordLogger>().NodeFailedAsync(runId, nodeId, iterationKey, "it failed", TimeSpan.FromMilliseconds(5), exception is null ? null : ProviderFailureCapture.From(exception, repositoryId: null), CancellationToken.None);
    }

    private async Task SeedAgentRunAsync(Guid teamId, Guid runId, string nodeId, string iterationKey, PersistedFailure? failure, int createdMinutesAgo = 1)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-createdMinutesAgo);
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "executor_error", Failure = failure };

        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, NodeId = nodeId, IterationKey = iterationKey, Harness = "claude-cli", Status = AgentRunStatus.Failed,
            ResultJson = System.Text.Json.JsonSerializer.Serialize(result, CodeSpace.Core.Services.Agents.AgentJson.Options), CompletedAt = at, CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>What the real seeder does for a rerun: re-emits the original's settled cells onto the fork — a kept top-level cell, or the sibling branch cells of a map whose other branch is the one re-run.</summary>
    private async Task SeedForkAsync(Guid originalRunId, Guid forkRunId, bool siblingBranch)
    {
        using var scope = _fixture.BeginScope();
        var seeder = scope.Resolve<IRerunCellSeeder>();

        if (siblingBranch)
            await seeder.SeedSiblingBranchCellsAsync(originalRunId, forkRunId, "map", new HashSet<int> { 1 }, branchCount: 2, CancellationToken.None);
        else
            await seeder.SeedKeptCellsAsync(originalRunId, forkRunId, new HashSet<string> { "list" }, writeEmptySnapshotSentinel: false, CancellationToken.None);
    }

    private sealed record ForkRecord(string Error, bool HasFailureKey);

    /// <summary>The one <c>node.failed</c> record the fork holds for the cell, as written.</summary>
    private async Task<ForkRecord> ReadForkRecordAsync(Guid forkRunId, string nodeId, string iterationKey)
    {
        using var scope = _fixture.BeginScope();
        var payload = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == forkRunId && r.RecordType == WorkflowRunRecordTypes.NodeFailed && r.NodeId == nodeId && r.IterationKey == iterationKey)
            .Select(r => r.PayloadJson).SingleAsync();
        using var document = System.Text.Json.JsonDocument.Parse(payload);

        return new ForkRecord(document.RootElement.GetProperty("error").GetString()!, document.RootElement.TryGetProperty(PersistedFailureJson.PayloadKey, out _));
    }

    /// <summary>A failed run of a team — its own unless one is given — the ledger and agent runs are what the reader looks at, so the run needs no workflow.</summary>
    private async Task<(Guid TeamId, Guid RunId)> SeedRunAsync(Guid? existingTeamId = null)
    {
        var teamId = existingTeamId ?? (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure, Error = "Node 'map' failed.",
            ScopeRepositoryIds = [], ScopeProjectIds = [], DefinitionSnapshotJson = "{\"nodes\":[],\"edges\":[]}", DefinitionSnapshotHash = "sha256:test", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();

        return (teamId, runId);
    }
}
