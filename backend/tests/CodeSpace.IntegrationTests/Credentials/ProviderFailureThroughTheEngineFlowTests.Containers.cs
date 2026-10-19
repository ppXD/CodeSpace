using System.Text.Json;
using Autofac;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — a provider connection that fails INSIDE a container (a <c>flow.map</c> branch, a <c>flow.loop</c> pass, either one
/// nested in the other) and ends the container. The real engine walks real <c>git.list_prs</c> nodes against the real GitLab provider, the
/// real credential resolver and OAuth refresher over real Postgres; the one fake is GitLab (<see cref="GitLabDoorkeeperStub"/>). What is
/// pinned is where the failure is written down: the container's own <c>node.failed</c> record carries the failure of the cell that ended it,
/// so the run's remedy is the one a top-level node of the same failure gets — and carries nothing when the cell that ended the container
/// did not fail on a connection, however many provider failures the run handled on the way.
///
/// <para>The cell is chosen deterministically. A <c>terminate</c> map whose branches fail concurrently answers for the first failing branch
/// BY ELEMENT ORDER — the same branch whose node the map's sentence names — never for whichever finished first. A branch that failed on an
/// earlier walk and was replayed from the ledger (its sibling parked, so the map could not fail until a later walk) is still the cell that
/// ended the map, and its failure is read back from its own record. A cell whose node worded its failure without a typed cause falls back to
/// the agent run that last worked in that very cell — never to another branch's.</para>
/// </summary>
public partial class ProviderFailureThroughTheEngineFlowTests
{
    public enum ContainerShape { Map, Loop, LoopInMap, MapInLoop }

    // shape → the containers that failed with the sentence each one wrote (innermost first); the outermost is the one the run's own error names.
    private static (string Id, string Sentence)[] ContainersOf(ContainerShape shape) => shape switch
    {
        ContainerShape.Map => [("map", "Node 'list' in map 'map' failed.")],
        ContainerShape.Loop => [("loop", "Node 'list' failed.")],
        ContainerShape.LoopInMap => [("loop", "Node 'list' failed."), ("map", "Node 'loop' in map 'map' failed.")],
        _ => [("map", "Node 'list' in map 'map' failed."), ("loop", "Node 'map' failed.")],
    };

    [Theory]
    [InlineData(ContainerShape.Map)]
    [InlineData(ContainerShape.Loop)]
    [InlineData(ContainerShape.LoopInMap)]
    [InlineData(ContainerShape.MapInLoop)]
    public async Task A_provider_failure_that_ends_a_container_is_recorded_on_every_container_it_ended_and_the_remedy_is_the_one_a_top_level_node_gets(ContainerShape shape)
    {
        using var gitlab = new GitLabDoorkeeperStub();   // never heard of the pair the row holds: the refresh is answered invalid_grant
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        await RecordThePersonItWasConnectedForAsync(seed.CredentialId, team.UserId);

        var runId = await RunContainerAsync(team, ContainerDefinition(shape, seed.RepositoryId), seed.RepositoryId);

        var records = await ReadFailedRecordsAsync(runId);
        var containers = ContainersOf(shape);

        foreach (var (id, sentence) in containers)
        {
            var container = records.Single(r => r.NodeId == id);

            container.Error.ShouldBe(sentence, $"{id}: the sentence the container always wrote is untouched");
            container.Failure.ShouldNotBeNull($"{id}: the container's own record carries the failure of the cell that ended it");
            container.Failure.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect, id);
            container.Failure.DetailGuid("credentialId").ShouldBe(seed.CredentialId, id);
            container.Failure.RepositoryId.ShouldBe(seed.RepositoryId, $"{id}: the repository the failing node declared it acted on travels with it");
        }

        records.Single(r => r.NodeId == "list").Failure!.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect, "fixture check: the node's own record said it first");
        (await ReadRunErrorAsync(runId)).ShouldBe($"Node '{containers[^1].Id}' failed.", "the run's own sentence is untouched");

        var failure = await ReadRecordedFailureAsync(runId);

        failure.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect, "the run's failure is the one the container that ended it recorded");

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, runId);

        remedy.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        remedy.Resolved.ShouldBeFalse();
        remedy.Credential!.Id.ShouldBe(seed.CredentialId);
        remedy.Repositories.ShouldHaveSingleItem().FullPath.ShouldBe("acme/api");
        remedy.Title.ShouldBe("GitLab no longer accepts the credential this run used", "the words a top-level node of the same failure gets");
        remedy.Actions.Select(a => (a.Kind, a.Enabled)).ShouldBe(new[]
        {
            (RemedyActionKind.Reconnect, true), (RemedyActionKind.Relink, true), (RemedyActionKind.OpenProviders, true), (RemedyActionKind.Rerun, false)
        }, "and the buttons: an Owner may do everything but run it again — that waits for the fix");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_map_whose_branches_fail_on_different_credentials_answers_for_the_first_branch_by_element_order(bool theFirstElementIsTheFirstCredential)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        using var otherGitlab = new GitLabDoorkeeperStub();   // a second provider instance: a team holds one per address
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var first = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        var second = await GitLabCredentialSeed.SeedAsync(_fixture, otherGitlab.BaseUrl, "at-1", "rt-1", expired: true, existingTeam: team);
        var (elementZero, elementOne) = theFirstElementIsTheFirstCredential ? (first, second) : (second, first);

        var runId = await RunContainerAsync(team, ContainerDefinition(ContainerShape.Map, elementZero.RepositoryId), elementZero.RepositoryId, elementOne.RepositoryId);

        var records = await ReadFailedRecordsAsync(runId);

        records.Where(r => r.NodeId == "list").Select(r => (r.IterationKey, r.Failure!.DetailGuid("credentialId"))).ShouldBe(new[] { ("map#0", (Guid?)elementZero.CredentialId), ("map#1", (Guid?)elementOne.CredentialId) }, ignoreOrder: true, customMessage: "fixture check: both branches failed, each on its own credential");

        var map = records.Single(r => r.NodeId == "map");

        map.Failure.ShouldNotBeNull("the map's own record carries the failure of the cell that ended it");
        map.Failure.DetailGuid("credentialId").ShouldBe(elementZero.CredentialId, "element 0 — whichever branch happened to finish first, the map's sentence names the first branch by element order and so does its failure");
        map.Failure.RepositoryId.ShouldBe(elementZero.RepositoryId);
        (await RemedyForAsync(team.UserId, team.TeamId, runId)).Credential!.Id.ShouldBe(elementZero.CredentialId);
    }

    [Theory]
    // the container, and the node that fails it for another reason: a node that worded its failure, or a flow.iterate handed something that is not a list
    [InlineData(ContainerShape.Map, FlakyTestNode.Key)]
    [InlineData(ContainerShape.Loop, FlakyTestNode.Key)]
    [InlineData(ContainerShape.Map, "flow.iterate")]
    [InlineData(ContainerShape.Loop, "flow.iterate")]
    public async Task A_container_that_fails_on_something_else_after_a_provider_failure_was_handled_has_no_remedy_for_the_old_failure(ContainerShape shape, string failingTypeKey)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);

        var runId = await RunContainerAsync(team, HandledThenFailingDefinition(shape, seed.RepositoryId, failingTypeKey), seed.RepositoryId);

        var records = await ReadFailedRecordsAsync(runId);
        var container = shape == ContainerShape.Map ? "map" : "loop";

        records.Single(r => r.NodeId == "list").Failure!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "fixture check: the OLD failure is on the ledger, in the same container, carrying the code the page must not show");
        records.Single(r => r.NodeId == "other").Failure.ShouldBeNull("fixture check: the cell that ended the container failed on something else");
        records.Single(r => r.NodeId == container).Failure.ShouldBeNull("the container answers for the cell that ended it, not for a failure its body handled earlier");
        (await ReadRunErrorAsync(runId)).ShouldBe($"Node '{container}' failed.");

        using var scope = _fixture.BeginScope();

        (await scope.Resolve<IRunFailureReader>().ReadAsync(runId, team.TeamId, CancellationToken.None)).ShouldBeNull();
        (await RemedyForAsync(team.UserId, team.TeamId, runId, allowNone: true)).ShouldBeNull("so no card blames a credential for a run that failed for another reason");
    }

    [Fact]
    public async Task A_provider_failure_a_try_caught_leaves_a_successful_run_and_a_try_record_with_no_failure()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var workflowId = await CreateWorkflowAsync(team, CaughtByATryDefinition(seed.RepositoryId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId);

        await RunEngineAsync(runId);

        var records = await ReadFailedRecordsAsync(runId);

        using var scope = _fixture.BeginScope();

        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "fixture check: the try caught the failure");
        records.Select(r => r.NodeId).ShouldBe(new[] { "list" }, "the try itself did not fail, so it wrote no failed record of its own");
        records[0].Failure!.Code.ShouldBe(FailureCodes.CredentialDisconnected);
        (await scope.Resolve<IRunFailureReader>().ReadAsync(runId, team.TeamId, CancellationToken.None)).ShouldBeNull("the run did not fail on it");
        (await RemedyForAsync(team.UserId, team.TeamId, runId, allowNone: true)).ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_branch_whose_node_worded_its_failure_without_a_cause_is_traced_through_its_own_agent_run_and_never_through_a_siblings(bool theAgentRunIsTheEndingBranchs)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var workflowId = await CreateWorkflowAsync(team, FlakyBranchesDefinition(Guid.NewGuid().ToString("N")));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId, payloadJson: """{ "things": ["x", "y"] }""");

        // The agent run the branch's agent.run node would have spawned: the executor's own account of a clone GitLab refused, in the cell it worked in.
        await SeedAgentRunAsync(team.TeamId, runId, "boom", theAgentRunIsTheEndingBranchs ? "map#0" : "map#1", ProviderFailureCapture.From(new CredentialDisconnectedException(seed.CredentialId, "GitLab · NACHO.L"), seed.RepositoryId)!);
        await RunEngineAsync(runId);

        var records = await ReadFailedRecordsAsync(runId);
        var map = records.Single(r => r.NodeId == "map");

        records.Where(r => r.NodeId == "boom").Select(r => r.IterationKey).ShouldBe(new[] { "map#0", "map#1" }, ignoreOrder: true, customMessage: "fixture check: both branches failed on a sentence alone — no node recorded a failure");
        records.Where(r => r.NodeId == "boom").ShouldAllBe(r => r.Failure == null);

        if (!theAgentRunIsTheEndingBranchs)
        {
            map.Failure.ShouldBeNull("the map's first branch is element 0, whose cell has no agent run of its own — another branch's account is not why the map ended");
            (await RemedyForAsync(team.UserId, team.TeamId, runId, allowNone: true)).ShouldBeNull();
            return;
        }

        map.Failure.ShouldNotBeNull("the cell that ended the map has an agent run, as a top-level agent.run node would");
        map.Failure.Code.ShouldBe(FailureCodes.CredentialDisconnected);
        map.Failure.DetailGuid("credentialId").ShouldBe(seed.CredentialId);
        (await RemedyForAsync(team.UserId, team.TeamId, runId)).Credential!.Id.ShouldBe(seed.CredentialId);
    }

    [Fact]
    public async Task A_branch_that_failed_on_an_earlier_walk_while_a_sibling_was_parked_is_still_the_cell_that_ended_the_map_when_it_fails_on_the_next()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        using var otherGitlab = new GitLabDoorkeeperStub();   // a second provider instance: a team holds one per address
        var key = "park-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var first = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        var second = await GitLabCredentialSeed.SeedAsync(_fixture, otherGitlab.BaseUrl, "at-1", "rt-1", expired: true, existingTeam: team);
        var workflowId = await CreateWorkflowAsync(team, ParkThenListDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId, payloadJson: JsonSerializer.Serialize(new { things = new[] { first.RepositoryId, second.RepositoryId } }));

        await RunEngineAsync(runId);
        await AssertRunStatusAsync(runId, WorkflowRunStatus.Suspended, "both branches parked");

        // Branch 0 resumes and fails on its connection; branch 1 is still parked, so the map cannot fail yet — it parks again, holding branch 0's failure in its ledger.
        await ResumeBranchAsync(runId, key, first.RepositoryId);
        await RunEngineAsync(runId);
        await AssertRunStatusAsync(runId, WorkflowRunStatus.Suspended, "the failure waits for the parked sibling");
        (await ReadFailedRecordsAsync(runId)).Select(r => r.NodeId).ShouldBe(new[] { "list" }, "fixture check: branch 0 failed, the map has not");

        // Branch 1 resumes and fails too. Branch 0 is REPLAYED from the ledger now, not re-run — and it is still the branch the map answers for.
        await ResumeBranchAsync(runId, key, second.RepositoryId);
        await RunEngineAsync(runId);
        await AssertRunStatusAsync(runId, WorkflowRunStatus.Failure, "both branches failed");

        var records = await ReadFailedRecordsAsync(runId);

        records.Where(r => r.NodeId == "list").Select(r => r.IterationKey).ShouldBe(new[] { "map#0", "map#1" }, "fixture check: branch 0 did not run its node again");
        var map = records.Single(r => r.NodeId == "map");

        map.Failure.ShouldNotBeNull("the map's own record carries the failure of the cell that ended it");
        map.Failure.DetailGuid("credentialId").ShouldBe(first.CredentialId, "branch 0's failure was written on an earlier walk and is read back from its own record");
        (await RemedyForAsync(team.UserId, team.TeamId, runId)).Credential!.Id.ShouldBe(first.CredentialId);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private sealed record FailedRecord(string NodeId, string IterationKey, string Error, PersistedFailure? Failure);

    private async Task<Guid> RunContainerAsync((Guid TeamId, Guid UserId) team, WorkflowDefinition definition, params Guid[] elements)
    {
        var workflowId = await CreateWorkflowAsync(team, definition);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId, payloadJson: JsonSerializer.Serialize(new { things = elements }));

        await RunEngineAsync(runId);
        await AssertRunStatusAsync(runId, WorkflowRunStatus.Failure, "fixture check: the connection's failure ended the container, which failed the run");

        return runId;
    }

    private async Task AssertRunStatusAsync(Guid runId, WorkflowRunStatus status, string because)
    {
        using var scope = _fixture.BeginScope();

        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(status, because);
    }

    /// <summary>Every <c>node.failed</c> record of the run, oldest first, with the failure it carries (none for a record that carries none).</summary>
    private async Task<IReadOnlyList<FailedRecord>> ReadFailedRecordsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed).OrderBy(r => r.Sequence).Select(r => new { r.NodeId, r.IterationKey, r.PayloadJson }).ToListAsync();

        return rows.Select(r => new FailedRecord(r.NodeId!, r.IterationKey, JsonDocument.Parse(r.PayloadJson).RootElement.GetProperty("error").GetString()!, PersistedFailureJson.Read(r.PayloadJson))).ToList();
    }

    private async Task SeedAgentRunAsync(Guid teamId, Guid runId, string nodeId, string iterationKey, PersistedFailure failure)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "executor_error", Failure = failure };

        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, NodeId = nodeId, IterationKey = iterationKey, Harness = "claude-cli", Status = AgentRunStatus.Failed,
            ResultJson = JsonSerializer.Serialize(result, CodeSpace.Core.Services.Agents.AgentJson.Options), CompletedAt = at, CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Resolves the parked branch whose element is the repository — the single-wait path an approval or a callback resumes by, which re-walks at once with a sibling still parked.</summary>
    private async Task ResumeBranchAsync(Guid runId, string key, Guid element)
    {
        using var scope = _fixture.BeginScope();
        var waitId = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId && w.Token == $"{key}::{element}" && w.Status == WorkflowWaitStatuses.Pending).Select(w => w.Id).SingleAsync();

        (await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, """{"summary":"go"}""", CancellationToken.None)).ShouldBeTrue();
    }

    // ─── Definitions ───────────────────────────────────────────────────────────

    private static NodeDefinition Node(string id, string typeKey, string? parentId, string config = "{}", string inputs = "{}") =>
        new() { Id = id, TypeKey = typeKey, ParentId = parentId, Config = WorkflowsTestSeed.Json(config), Inputs = WorkflowsTestSeed.Json(inputs) };

    private static NodeDefinition ListIn(string parentId, string repository) => Node("list", "git.list_prs", parentId, inputs: $$"""{"repositoryId":"{{repository}}"}""");

    private static NodeDefinition MapNode(string id, string? parentId) => Node(id, "flow.map", parentId, inputs: """{ "items": "{{trigger.things}}" }""");

    private static NodeDefinition LoopNode(string id, string? parentId) => Node(id, "flow.loop", parentId, config: """{ "maxIterations": 2 }""");

    private static EdgeDefinition Edge(string from, string to, string? handle = null) => new() { From = from, To = to, SourceHandle = handle };

    // start → <outermost container> → end. The container's body ends in `list`, the node the dead connection fails; no error edge anywhere, so it ends the container, which ends the run.
    // `list` is given {{item}} where an enclosing map supplies the repository, and the repository itself under a loop alone.
    private static WorkflowDefinition ContainerDefinition(ContainerShape shape, Guid onlyRepository)
    {
        const string repository = "{{item}}";
        var literal = onlyRepository.ToString();
        var nodes = new List<NodeDefinition> { Node("start", "trigger.manual", null), Node("end", "builtin.terminal", null) };
        var edges = new List<EdgeDefinition>();

        switch (shape)
        {
            case ContainerShape.Map:
                nodes.AddRange([MapNode("map", null), Node("ms", "flow.map_start", "map"), ListIn("map", repository)]);
                edges.AddRange([Edge("start", "map"), Edge("map", "end"), Edge("ms", "list")]);
                break;
            case ContainerShape.Loop:
                nodes.AddRange([LoopNode("loop", null), Node("ls", "flow.loop_start", "loop"), ListIn("loop", literal)]);
                edges.AddRange([Edge("start", "loop"), Edge("loop", "end"), Edge("ls", "list")]);
                break;
            case ContainerShape.LoopInMap:
                nodes.AddRange([MapNode("map", null), Node("ms", "flow.map_start", "map"), LoopNode("loop", "map"), Node("ls", "flow.loop_start", "loop"), ListIn("loop", repository)]);
                edges.AddRange([Edge("start", "map"), Edge("map", "end"), Edge("ms", "loop"), Edge("ls", "list")]);
                break;
            default:
                nodes.AddRange([LoopNode("loop", null), Node("ls", "flow.loop_start", "loop"), MapNode("map", "loop"), Node("ms", "flow.map_start", "map"), ListIn("map", repository)]);
                edges.AddRange([Edge("start", "loop"), Edge("loop", "end"), Edge("ls", "map"), Edge("ms", "list")]);
                break;
        }

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }

    // start → <container>(body: <start> → list =(error)=> other) → end. `list` fails on its connection and its error edge handles that; `other` — the body's terminal — then fails on its own, with no cause a person can repair.
    private static WorkflowDefinition HandledThenFailingDefinition(ContainerShape shape, Guid onlyRepository, string failingTypeKey)
    {
        const string repository = "{{item}}";
        var map = shape == ContainerShape.Map;
        var container = map ? "map" : "loop";
        var start = map ? "ms" : "ls";
        var other = failingTypeKey == FlakyTestNode.Key
            ? Node("other", FlakyTestNode.Key, container, config: $$"""{ "key": "{{Guid.NewGuid():N}}", "failTimes": 99 }""")
            : Node("other", failingTypeKey, container, config: """{ "template": "x" }""", inputs: """{ "items": "not a list" }""");

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = [Node("start", "trigger.manual", null), map ? MapNode("map", null) : LoopNode("loop", null), Node(start, map ? "flow.map_start" : "flow.loop_start", container), ListIn(container, map ? repository : onlyRepository.ToString()), other, Node("end", "builtin.terminal", null)],
            Edges = [Edge("start", container), Edge(container, "end"), Edge(start, "list"), Edge("list", "other", WorkflowHandles.Error)],
        };
    }

    // start → try(body: <start> → list) =(catch)=> caught; try → done. The try catches the connection's failure.
    private static WorkflowDefinition CaughtByATryDefinition(Guid repositoryId) => new()
    {
        SchemaVersion = 1,
        Nodes = [Node("start", "trigger.manual", null), Node("try", "flow.try", null), Node("ts", "flow.try_start", "try"), ListIn("try", repositoryId.ToString()), Node("done", "builtin.terminal", null), Node("caught", "builtin.terminal", null)],
        Edges = [Edge("start", "try"), Edge("ts", "list"), Edge("try", "done"), Edge("try", "caught", WorkflowHandles.Catch)],
    };

    // start → map(body: <start> → boom[fails on a sentence alone]) → end.
    private static WorkflowDefinition FlakyBranchesDefinition(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = [Node("start", "trigger.manual", null), MapNode("map", null), Node("ms", "flow.map_start", "map"), Node("boom", FlakyTestNode.Key, "map", config: $$"""{ "key": "{{flakyKey}}", "failTimes": 99 }"""), Node("end", "builtin.terminal", null)],
        Edges = [Edge("start", "map"), Edge("map", "end"), Edge("ms", "boom")],
    };

    // start → map(body: <start> → park[parks each branch's wait, resumes with a summary] → list) → end. Each element is a repository; `list` is the node its dead connection fails.
    private static WorkflowDefinition ParkThenListDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes =
        [
            Node("start", "trigger.manual", null), MapNode("map", null), Node("ms", "flow.map_start", "map"),
            Node("park", SuspendProbeNode.Key, "map", inputs: $$$"""{ "key": "{{{key}}}", "item": "{{item}}" }"""), ListIn("map", "{{item}}"), Node("end", "builtin.terminal", null),
        ],
        Edges = [Edge("start", "map"), Edge("map", "end"), Edge("ms", "park"), Edge("park", "list")],
    };
}
