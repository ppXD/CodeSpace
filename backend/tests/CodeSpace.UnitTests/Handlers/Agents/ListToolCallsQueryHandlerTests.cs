using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

/// <summary>
/// Unit-proves <see cref="ListToolCallsQueryHandler"/> is a thin dispatcher (Rule 16): it delegates to
/// <see cref="IToolCallLedgerService.GetForRunAsync"/> with the CALLER'S team (never the DbContext), maps each
/// <c>ToolCallLedger</c> to a <c>ToolCallView</c> field-for-field, and re-orders the rows chronological
/// (GetForRunAsync returns newest-first; the handler flips it to oldest-first for the audit timeline).
/// </summary>
[Trait("Category", "Unit")]
public class ListToolCallsQueryHandlerTests
{
    [Fact]
    public async Task Delegates_to_the_service_with_the_callers_team_and_run()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var ledger = new CapturingLedger(Array.Empty<ToolCallLedger>());

        await new ListToolCallsQueryHandler(ledger, new StubCurrentTeam(teamId))
            .Handle(new ListToolCallsQuery { AgentRunId = runId }, CancellationToken.None);

        ledger.LastRunId.ShouldBe(runId, "the handler must pass the requested run id through");
        ledger.LastTeamId.ShouldBe(teamId, "the handler must scope the read to the CALLER's team (ICurrentTeam), not the wire");
    }

    [Fact]
    public async Task Maps_every_audit_field_and_orders_chronological()
    {
        var teamId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var older = DateTimeOffset.UtcNow.AddMinutes(-5);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
        var approvedAt = newer.AddSeconds(20);

        // GetForRunAsync hands back NEWEST first (the row order the real service uses); the handler must flip it.
        var ledger = new CapturingLedger(new[]
        {
            new ToolCallLedger { ToolKind = "git.merge_pr", Status = ToolCallLedgerStatus.Succeeded, CreatedDate = newer, LastModifiedDate = approvedAt, Error = null, ApprovedByUserId = approverId, ApprovedAt = approvedAt },
            new ToolCallLedger { ToolKind = "git.open_pr", Status = ToolCallLedgerStatus.Failed, CreatedDate = older, LastModifiedDate = older, Error = "boom", ApprovedByUserId = null, ApprovedAt = null },
        });

        var result = await new ListToolCallsQueryHandler(ledger, new StubCurrentTeam(teamId))
            .Handle(new ListToolCallsQuery { AgentRunId = Guid.NewGuid() }, CancellationToken.None);

        result.Select(r => r.ToolKind).ShouldBe(new[] { "git.open_pr", "git.merge_pr" }, "the audit list is oldest-first by CreatedDate");

        var first = result[0];
        first.Status.ShouldBe(ToolCallLedgerStatus.Failed);
        first.CreatedDate.ShouldBe(older);
        first.LastModifiedDate.ShouldBe(older);
        first.Error.ShouldBe("boom", "the already-redacted Error maps straight through");
        first.ApprovedByUserId.ShouldBeNull();
        first.ApprovedAt.ShouldBeNull();

        var second = result[1];
        second.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        second.CreatedDate.ShouldBe(newer);
        second.ApprovedByUserId.ShouldBe(approverId, "the approval trail maps through for audit");
        second.ApprovedAt.ShouldBe(approvedAt);
    }

    /// <summary>An IToolCallLedgerService double that records the read args + returns canned rows — no DbContext anywhere (proving the handler owns no data access).</summary>
    private sealed class CapturingLedger : IToolCallLedgerService
    {
        private readonly IReadOnlyList<ToolCallLedger> _rows;

        public CapturingLedger(IReadOnlyList<ToolCallLedger> rows) { _rows = rows; }

        public Guid LastRunId { get; private set; }
        public Guid LastTeamId { get; private set; }

        public Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken)
        {
            LastRunId = agentRunId;
            LastTeamId = teamId;
            return Task.FromResult(_rows);
        }

        public Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> TryBeginApprovalAsync(Guid ledgerId, Guid teamId, string approvalToken, DateTimeOffset deadlineAt, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task SetApprovalMessageAsync(Guid ledgerId, Guid teamId, Guid messageId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> TryBeginExecutionAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ToolCallApprovalState?> ReadApprovalStateAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> TryAnswerDecisionAsync(Guid ledgerId, Guid teamId, string answerJson, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task SetDecisionEnvelopeAsync(Guid ledgerId, Guid teamId, string envelopeJson, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodeSpace.Messages.Decisions.TimedOutDecision>> ExpireStaleDecisionsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}
