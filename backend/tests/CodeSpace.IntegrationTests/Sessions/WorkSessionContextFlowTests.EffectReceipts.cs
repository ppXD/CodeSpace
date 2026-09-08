using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

public partial class WorkSessionContextFlowTests
{
    [Fact]
    [Trait("P17", "Regression")]
    public async Task Builder_and_continue_launch_carry_prior_side_effect_receipts_into_the_frozen_goal()
    {
        var (teamId, userId) = await Workflows.Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "Open the release PR", summary: "work completed", branch: null);
        var (workflowRunId, ledgerId) = await SeedPriorEffectAsync(teamId, sessionId, "git.open_pr", ToolCallLedgerStatus.Succeeded, "PR_URL_1850", null);

        var context = await BuildContextAsync(sessionId, teamId);
        context.ShouldContain($"receipt:tool-call-ledger/{ledgerId}");
        context.ShouldContain($"workflowRun={workflowRunId}");
        context.ShouldContain("PR_URL_1850");

        var launch = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, "Verify and merge it"));
        var frozenGoal = await ReadAgentGoalAsync(launch.RunId);
        frozenGoal.ShouldContain($"receipt:tool-call-ledger/{ledgerId}", customMessage: "a cold continuation sees the prior effect before its first model action");
        frozenGoal.ShouldContain("Verify and merge it");
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task Builder_excludes_decisions_and_never_leaks_another_teams_receipts()
    {
        var (teamA, _) = await Workflows.Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await Workflows.Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionA = await SeedSessionAsync(teamA);
        await SeedCompletedTurnAsync(teamA, sessionA, turn: 1, goal: "work", summary: "done", branch: null);
        await SeedPriorEffectAsync(teamA, sessionA, "decision.request", ToolCallLedgerStatus.Succeeded, "SHOULD_NOT_RENDER", null);
        await SeedPriorEffectAsync(teamA, sessionA, "git.merge_pr", ToolCallLedgerStatus.Failed, null, "REMOTE_STATE_UNCERTAIN");

        var owned = await BuildContextAsync(sessionA, teamA);
        owned.ShouldContain("REMOTE_STATE_UNCERTAIN");
        owned.ShouldContain("do not assume it is safe to retry");
        owned.ShouldNotContain("SHOULD_NOT_RENDER");

        (await BuildContextAsync(sessionA, teamB)).ShouldBeNull("a foreign team cannot recover another tenant's turns or effect receipts");
    }

    private async Task<(Guid WorkflowRunId, Guid LedgerId)> SeedPriorEffectAsync(Guid teamId, Guid sessionId, string toolKind, ToolCallLedgerStatus status, string? resultText, string? error)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var workflowRunId = await db.WorkflowRun.AsNoTracking().Where(run => run.TeamId == teamId && run.SessionId == sessionId && run.SessionTurnIndex != null)
            .OrderByDescending(run => run.SessionTurnIndex).Select(run => run.Id).FirstAsync();
        var agentRunId = Guid.NewGuid();
        var ledgerId = Guid.CreateVersion7();
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = workflowRunId, Harness = "codex-cli",
            Status = Messages.Enums.AgentRunStatus.Succeeded, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId, TeamId = teamId, AgentRunId = agentRunId, ToolKind = toolKind,
            IdempotencyKey = $"{toolKind}:{ledgerId:N}", InputHash = ledgerId.ToString("N").PadRight(64, '0')[..64], Status = status,
            ResultJson = resultText == null ? null : JsonSerializer.Serialize(new { isError = false, content = new[] { new { type = "text", text = resultText } } }),
            Error = error, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return (workflowRunId, ledgerId);
    }
}
