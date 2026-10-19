using System.Text.Json;
using Autofac;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — a map-branch rerun forks the run and replays the sibling branches that were not chosen, a failed sibling among them
/// (a terminate map keeps one: the branch that ended it). The fork walks them through the real engine, so the map that fails on the replayed
/// branch must answer for it as the original run did: the cell the fork replays is re-emitted by the real seeder with the failure the original
/// recorded for it — on its own record, or on the agent run that worked in it, which belongs to the original run and not the fork — and the run
/// the fork ends on says what to repair. The fork's one fresh branch fails on its own connection, so a remedy that named THAT credential would be
/// the wrong branch's.
/// </summary>
public partial class ProviderFailureThroughTheEngineFlowTests
{
    [Theory]
    [InlineData(false)]   // the replayed branch's node recorded its own failure
    [InlineData(true)]    // the replayed branch's node worded its failure alone: the original run's agent run holds the account
    public async Task A_map_branch_rerun_fork_that_fails_on_a_replayed_failed_branch_answers_with_the_remedy_the_original_run_did(bool viaTheAgentRun)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        using var otherGitlab = new GitLabDoorkeeperStub();   // a second provider instance: a team holds one per address
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var first = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: !viaTheAgentRun, existingTeam: team);
        var second = await GitLabCredentialSeed.SeedAsync(_fixture, otherGitlab.BaseUrl, "at-1", "rt-1", expired: true, existingTeam: team);
        if (viaTheAgentRun) await DisconnectAsync(first.CredentialId);

        var originalRunId = await RunAdmittedMapAsync(team, viaTheAgentRun, first, second);

        (await ReadFailedRecordsAsync(originalRunId)).Single(r => r.NodeId == "map").Failure!.DetailGuid("credentialId").ShouldBe(first.CredentialId, "fixture check: the original run's map answers for element 0, the first branch to fail by element order");

        var forkRunId = await RerunBranchAsync(team, originalRunId, branchIndex: 1);

        await RunEngineAsync(forkRunId);
        await AssertRunStatusAsync(forkRunId, WorkflowRunStatus.Failure, "fixture check: element 0 is replayed failed, so the map fails again");

        var records = await ReadFailedRecordsAsync(forkRunId);
        var node = viaTheAgentRun ? "boom" : "list";

        records.Single(r => r.NodeId == node && r.IterationKey == "map#0").Failure.ShouldNotBeNull("the replayed branch's record on the fork says what the original run said it failed on");

        var fresh = records.Single(r => r.NodeId == node && r.IterationKey == "map#1");

        if (viaTheAgentRun) fresh.Failure.ShouldBeNull("fixture check: the fresh branch failed too, on a sentence alone");
        else fresh.Failure!.DetailGuid("credentialId").ShouldBe(second.CredentialId, "fixture check: the fresh branch failed on its own connection, which is not the one the map answers for");

        var map = records.Single(r => r.NodeId == "map");

        map.Error.ShouldBe($"Node '{node}' in map 'map' failed.", "the sentence the map always wrote");
        map.Failure.ShouldNotBeNull("the fork's map answers for the replayed branch it failed on, as the original run's did");
        map.Failure.DetailGuid("credentialId").ShouldBe(first.CredentialId, "element 0's credential — never the fresh branch's");

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, forkRunId);

        remedy.Code.ShouldBe(viaTheAgentRun ? FailureCodes.CredentialDisconnected : FailureCodes.CredentialNeedsReconnect);
        remedy.Credential!.Id.ShouldBe(first.CredentialId);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// An ADMITTED run (the one a rerun forks) of a terminate map over two elements, element <c>i</c> being <c>Elements[i]</c>'s repository, that fails. With
    /// <paramref name="viaTheAgentRun"/> each branch's node words its failure alone and element 0's cell has the agent run that holds the account.
    /// </summary>
    private async Task<Guid> RunAdmittedMapAsync((Guid TeamId, Guid UserId) team, bool viaTheAgentRun, SeededGitLabCredential first, SeededGitLabCredential second)
    {
        var definition = viaTheAgentRun ? FlakyBranchesDefinition(Guid.NewGuid().ToString("N")) : ContainerDefinition(ContainerShape.Map, first.RepositoryId);
        var workflowId = await CreateWorkflowAsync(team, definition);
        var runId = await WorkflowsTestSeed.SeedAdmittedManualRunAsync(_fixture, workflowId, team.TeamId, payloadJson: JsonSerializer.Serialize(new { things = new[] { first.RepositoryId, second.RepositoryId } }));

        if (viaTheAgentRun)
            await SeedAgentRunAsync(team.TeamId, runId, "boom", "map#0", ProviderFailureCapture.From(new CredentialDisconnectedException(first.CredentialId, "GitLab · NACHO.L"), first.RepositoryId)!);

        await RunEngineAsync(runId);
        await AssertRunStatusAsync(runId, WorkflowRunStatus.Failure, "fixture check: the map failed");

        return runId;
    }

    private async Task<Guid> RerunBranchAsync((Guid TeamId, Guid UserId) team, Guid originalRunId, int branchIndex)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IWorkflowService>().RerunMapBranchAsync(originalRunId, "map", branchIndex, team.TeamId, team.UserId, operationId: null, CancellationToken.None);
    }
}
