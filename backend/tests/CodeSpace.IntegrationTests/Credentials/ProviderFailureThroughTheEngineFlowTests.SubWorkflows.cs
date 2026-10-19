using System.Text.Json;
using Autofac;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — a provider connection that fails inside a <c>flow.subworkflow</c> CHILD run and so ends the PARENT run. The real engine walks
/// the parent, which parks on its <c>flow.subworkflow</c> node while a real child run executes a real <c>git.list_prs</c> against the real GitLab
/// provider, credential resolver and OAuth refresher over real Postgres; the one fake is GitLab (<see cref="GitLabDoorkeeperStub"/>). What is pinned
/// is that the failure is read back where the child recorded it, by the reader the child's own page asks, and carried up: the resume payload, the
/// parent node's own <c>node.failed</c> record and — through a map or a loop the node sits in, or a chain of nested sub-workflows — the record of
/// whatever ended the parent, so the parent's remedy is the one the child's would be, naming the child run it happened in.
///
/// <para>The negatives that keep it honest: a child that failed for any other reason, a child that handled the failure, and a child that handled it
/// and then failed on something else all leave the parent with the sentence it always had; an origin that is not the team's run is never named; and
/// what the page offers is the same remedy the child's page offers, because it is the same reader and the same builder.</para>
/// </summary>
public partial class ProviderFailureThroughTheEngineFlowTests
{
    public enum SubWorkflowSite { TopLevel, InMap, InLoop }

    // The sentence the parent's node wrote before failures were carried: "Sub-workflow run failed: " and what the child's run said.
    private const string ChildSentence = "Sub-workflow run failed: Node 'list' failed.";

    [Theory]
    [InlineData(SubWorkflowSite.TopLevel)]
    [InlineData(SubWorkflowSite.InMap)]
    [InlineData(SubWorkflowSite.InLoop)]
    public async Task A_provider_failure_that_ends_a_child_run_gives_the_parent_the_childs_remedy_naming_the_child_run(SubWorkflowSite site)
    {
        using var gitlab = new GitLabDoorkeeperStub();   // never heard of the pair the row holds: the refresh is answered invalid_grant
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        await RecordThePersonItWasConnectedForAsync(seed.CredentialId, team.UserId);
        var child = await CreateNamedWorkflowAsync(team, "Review the pull requests", ListThenEndDefinition(seed.RepositoryId));
        var parentRunId = await StartRunOfAsync(team, SubWorkflowParent(child, site));

        var chain = await WalkThroughChildRunsAsync(parentRunId);

        chain.Count.ShouldBe(2, "fixture check: the parent ran one child");
        var childRunId = chain[1];

        await AssertRunStatusAsync(childRunId, WorkflowRunStatus.Failure, "fixture check: the dead connection ended the child");
        await AssertRunStatusAsync(parentRunId, WorkflowRunStatus.Failure, "fixture check: the child's failure ended the parent");

        var expectedOrigin = new FailureOrigin { RunId = childRunId, NodeId = "list", At = await ReadFailureTimeAsync(childRunId, team.TeamId) };
        var records = await ReadFailedRecordsAsync(parentRunId);
        var subRecord = records.Single(r => r.NodeId == "sub");

        subRecord.Error.ShouldBe(ChildSentence, "the sentence the node always wrote is untouched");
        subRecord.Failure.ShouldNotBeNull("the parent's node carries the failure the child ended on");
        subRecord.Failure.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        subRecord.Failure.DetailGuid("credentialId").ShouldBe(seed.CredentialId);
        subRecord.Failure.RepositoryId.ShouldBe(seed.RepositoryId, "the repository the child's node declared it acted on travels with it");
        subRecord.Failure.Origin.ShouldBe(expectedOrigin, "and the child run it happened in, the node that ended that run and when the child recorded it — the time the child's own page judges it from");

        foreach (var container in records.Where(r => r.NodeId is "map" or "loop"))
            container.Failure.ShouldNotBeNull($"{container.NodeId}: the container the node sat in carries the failure of the cell that ended it, origin and all").Origin.ShouldBe(expectedOrigin);

        (await ReadRunErrorAsync(parentRunId)).ShouldBe(site switch { SubWorkflowSite.TopLevel => "Node 'sub' failed.", SubWorkflowSite.InMap => "Node 'map' failed.", _ => "Node 'loop' failed." }, "the run's own sentence is untouched");

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, parentRunId);

        remedy.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        remedy.Resolved.ShouldBeFalse();
        remedy.Origin.ShouldNotBeNull("the remedy says where it happened");
        remedy.Origin.RunId.ShouldBe(childRunId);
        remedy.Origin.WorkflowId.ShouldBe(child.Id);
        remedy.Origin.WorkflowName.ShouldBe("Review the pull requests");
        remedy.Origin.NodeId.ShouldBe("list");
        remedy.Cause.ShouldContain($"sub-workflow “Review the pull requests” (run {childRunId.ToString()[..8]})", Case.Sensitive, "the words name it too");
        remedy.Cause.ShouldContain("“GitLab · NACHO.L”", Case.Sensitive, "and still say which credential");
        remedy.Credential!.Id.ShouldBe(seed.CredentialId);
        remedy.Repositories.ShouldHaveSingleItem().FullPath.ShouldBe("acme/api");
        remedy.Actions.Select(a => (a.Kind, a.Enabled)).ShouldBe(new[]
        {
            (RemedyActionKind.Reconnect, true), (RemedyActionKind.Relink, true), (RemedyActionKind.OpenProviders, true), (RemedyActionKind.Rerun, false)
        }, "an Owner may do everything but run it again — that waits for the fix, and it runs the PARENT again, which starts a fresh child");
    }

    [Fact]
    public async Task The_parents_remedy_is_the_remedy_the_childs_own_page_gives_and_the_childs_names_no_origin()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        await RecordThePersonItWasConnectedForAsync(seed.CredentialId, team.UserId);
        var child = await CreateNamedWorkflowAsync(team, "Review", ListThenEndDefinition(seed.RepositoryId));
        var chain = await WalkThroughChildRunsAsync(await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel)));

        var parentRemedy = await RemedyForAsync(team.UserId, team.TeamId, chain[0]);
        var childRemedy = await RemedyForAsync(team.UserId, team.TeamId, chain[1]);

        childRemedy.Origin.ShouldBeNull("the child's failure happened in the child: there is nowhere else to point");
        parentRemedy.Origin.ShouldNotBeNull();
        parentRemedy.Code.ShouldBe(childRemedy.Code);
        parentRemedy.Title.ShouldBe(childRemedy.Title);
        parentRemedy.Resolved.ShouldBe(childRemedy.Resolved);
        parentRemedy.Credential!.Id.ShouldBe(childRemedy.Credential!.Id);
        parentRemedy.Repositories.Select(r => (r.Id, r.CredentialWorks)).ShouldBe(childRemedy.Repositories.Select(r => (r.Id, r.CredentialWorks)));
        parentRemedy.Actions.Select(a => (a.Kind, a.Label, a.Enabled)).ShouldBe(childRemedy.Actions.Select(a => (a.Kind, a.Label, a.Enabled)));
        parentRemedy.Cause.ShouldEndWith(childRemedy.Cause, Case.Sensitive, "the parent's words are the child's, after the sentence that says where it happened");

        // Fixed through the credential the failure named: both pages read the repair the same way, and the parent's leads with running it again.
        var replacement = await AddLiveCredentialAsync(seed);
        await BindAsync(seed.RepositoryId, replacement);

        var parentAfter = await RemedyForAsync(team.UserId, team.TeamId, chain[0]);
        var childAfter = await RemedyForAsync(team.UserId, team.TeamId, chain[1]);

        parentAfter.Resolved.ShouldBeTrue("what failed in the child works again");
        childAfter.Resolved.ShouldBeTrue();
        parentAfter.Actions[0].Kind.ShouldBe(RemedyActionKind.Rerun, "running the parent again leads once the fix shows");
        parentAfter.Actions[0].Enabled.ShouldBeTrue();
        parentAfter.Origin!.RunId.ShouldBe(chain[1], "it is still said where it happened");
    }

    [Fact]
    public async Task Nested_sub_workflows_carry_the_deepest_childs_failure_to_every_parent_above_it()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        await RecordThePersonItWasConnectedForAsync(seed.CredentialId, team.UserId);
        var leaf = await CreateNamedWorkflowAsync(team, "Leaf: list the pull requests", ListThenEndDefinition(seed.RepositoryId));
        var middle = await CreateNamedWorkflowAsync(team, "Middle", SubWorkflowParent(leaf, SubWorkflowSite.TopLevel));
        var topRunId = await StartRunOfAsync(team, SubWorkflowParent(middle, SubWorkflowSite.TopLevel));

        var chain = await WalkThroughChildRunsAsync(topRunId);

        chain.Count.ShouldBe(3, "fixture check: top → middle → leaf");
        foreach (var run in chain) await AssertRunStatusAsync(run, WorkflowRunStatus.Failure, $"fixture check: the failure ended every level ({run})");

        var (topRun, middleRun, leafRun) = (chain[0], chain[1], chain[2]);

        foreach (var run in new[] { topRun, middleRun })
        {
            var sub = (await ReadFailedRecordsAsync(run)).Single(r => r.NodeId == "sub");

            sub.Error.ShouldBe(run == topRun ? "Sub-workflow run failed: Node 'sub' failed." : ChildSentence, "each level's own sentence is untouched");
            sub.Failure.ShouldNotBeNull();
            sub.Failure.Origin.ShouldBe(new FailureOrigin { RunId = leafRun, NodeId = "list", At = await ReadFailureTimeAsync(leafRun, team.TeamId) }, "the deepest run that met it, however many levels it passed through — and when IT recorded it, not when each level resumed");
            sub.Failure.DetailGuid("credentialId").ShouldBe(seed.CredentialId);

            var remedy = await RemedyForAsync(team.UserId, team.TeamId, run);

            remedy.Origin!.RunId.ShouldBe(leafRun);
            remedy.Origin.WorkflowName.ShouldBe("Leaf: list the pull requests");
            remedy.Origin.NodeId.ShouldBe("list");
            remedy.Credential!.Id.ShouldBe(seed.CredentialId);
        }

        (await RemedyForAsync(team.UserId, team.TeamId, leafRun)).Origin.ShouldBeNull("the run that met it is the origin of it, not a parent of anything");
    }

    [Fact]
    public async Task The_failure_of_the_last_attempt_is_the_one_a_retried_sub_workflow_node_carries()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        var child = await CreateNamedWorkflowAsync(team, "Review", ListThenEndDefinition(seed.RepositoryId));
        var plain = SubWorkflowParent(child, SubWorkflowSite.TopLevel);
        var parentRunId = await StartRunOfAsync(team, plain with { Nodes = plain.Nodes.Select(n => n.Id == "sub" ? n with { Retry = new RetryPolicy { MaxAttempts = 2, BackoffSeconds = 0 } } : n).ToList() });

        await RunEngineAsync(parentRunId);
        var firstChild = await ChildRunIdAsync(parentRunId);
        await RunEngineAsync(firstChild);
        await RunEngineAsync(parentRunId);   // the first child failed: the node retries, which stages a fresh child and parks again
        await AssertRunStatusAsync(parentRunId, WorkflowRunStatus.Suspended, "fixture check: the retry parked the parent on a second child");

        var secondChild = (await ChildRunIdsAsync(parentRunId)).Single(id => id != firstChild);

        await RunEngineAsync(secondChild);
        await RunEngineAsync(parentRunId);
        await AssertRunStatusAsync(parentRunId, WorkflowRunStatus.Failure, "fixture check: the second child failed too, so the node is out of attempts");

        var sub = (await ReadFailedRecordsAsync(parentRunId)).Single(r => r.NodeId == "sub");

        sub.Failure.ShouldNotBeNull("the retried node ends carrying the failure");
        sub.Failure.Origin!.RunId.ShouldBe(secondChild, "of the attempt that ended it, never of the first one that was retried");
    }

    [Fact]
    public async Task A_child_that_failed_on_something_else_leaves_the_parent_with_the_sentence_alone()
    {
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var child = await CreateNamedWorkflowAsync(team, "Flaky child", FlakyThenEndDefinition(Guid.NewGuid().ToString("N")));
        var chain = await WalkThroughChildRunsAsync(await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel)));

        await AssertRunStatusAsync(chain[1], WorkflowRunStatus.Failure, "fixture check: the child failed");
        await AssertRunStatusAsync(chain[0], WorkflowRunStatus.Failure, "fixture check: so did the parent");

        var sub = (await ReadFailedRecordsAsync(chain[0])).Single(r => r.NodeId == "sub");

        sub.Error.ShouldBe("Sub-workflow run failed: Node 'flaky' failed.");
        sub.Failure.ShouldBeNull("nothing a person can repair outside the run was recorded in the child, so nothing is carried");
        (await ReadResumePayloadAsync(chain[0])).TryGetProperty("failure", out _).ShouldBeFalse("and the resume payload is the shape it always was");
        using var scope = _fixture.BeginScope();

        (await scope.Resolve<IRunFailureReader>().ReadAsync(chain[0], team.TeamId, CancellationToken.None)).ShouldBeNull();
        (await RemedyForAsync(team.UserId, team.TeamId, chain[0], allowNone: true)).ShouldBeNull("no card blames a credential for a parent whose child failed for another reason");
    }

    [Fact]
    public async Task A_child_whose_node_worded_its_failure_alone_is_traced_through_its_own_agent_run_exactly_as_the_childs_own_page_does()
    {
        // The child's page answers from the agent run that worked in the node that ended it when that node recorded no failure of its own. The parent is told
        // by the same reader, so the two can never name different failures — nor can the parent miss one the child's page shows.
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var child = await CreateNamedWorkflowAsync(team, "Runs an agent", FlakyThenEndDefinition(Guid.NewGuid().ToString("N")));
        var parentRunId = await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel));

        await RunEngineAsync(parentRunId);
        var childRunId = await ChildRunIdAsync(parentRunId);

        await SeedAgentRunAsync(team.TeamId, childRunId, "flaky", string.Empty, ProviderFailureCapture.From(new CredentialDisconnectedException(seed.CredentialId, "GitLab · NACHO.L"), seed.RepositoryId)!);
        await RunEngineAsync(childRunId);
        await RunEngineAsync(parentRunId);

        await AssertRunStatusAsync(parentRunId, WorkflowRunStatus.Failure, "fixture check: the child's failure ended the parent");
        (await ReadFailedRecordsAsync(childRunId)).Single(r => r.NodeId == "flaky").Failure.ShouldBeNull("fixture check: the child's node worded its failure alone");

        var childRemedy = await RemedyForAsync(team.UserId, team.TeamId, childRunId);
        var parentRemedy = await RemedyForAsync(team.UserId, team.TeamId, parentRunId);

        childRemedy.Code.ShouldBe(FailureCodes.CredentialDisconnected, "fixture check: the child's own page is traced through its agent run");
        parentRemedy.Code.ShouldBe(FailureCodes.CredentialDisconnected);
        parentRemedy.Credential!.Id.ShouldBe(seed.CredentialId);
        parentRemedy.Origin!.RunId.ShouldBe(childRunId);
        parentRemedy.Origin.NodeId.ShouldBe("flaky", "the node that ended the child, which the agent run worked in");
        (await ReadFailedRecordsAsync(parentRunId)).Single(r => r.NodeId == "sub").Failure!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "and the parent's own record carries it");
    }

    [Fact]
    public async Task A_provider_failure_the_child_handled_leaves_a_successful_parent_and_no_remedy()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var child = await CreateNamedWorkflowAsync(team, "Handles it", ListWithErrorEdgeDefinition(seed.RepositoryId));
        var chain = await WalkThroughChildRunsAsync(await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel)));

        await AssertRunStatusAsync(chain[1], WorkflowRunStatus.Success, "fixture check: the child's error edge handled the failure");
        await AssertRunStatusAsync(chain[0], WorkflowRunStatus.Success, "fixture check: so the parent went on");
        PersistedFailureJson.Read((await ReadFailedPayloadsAsync(chain[1])).Single()).ShouldNotBeNull("fixture check: the child's own record still says what happened");
        (await ReadFailedRecordsAsync(chain[0])).ShouldBeEmpty("the parent's node did not fail");
        (await RemedyForAsync(team.UserId, team.TeamId, chain[0], allowNone: true)).ShouldBeNull();
    }

    [Fact]
    public async Task A_provider_failure_the_child_handled_and_then_failed_on_something_else_carries_nothing_to_the_parent()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var child = await CreateNamedWorkflowAsync(team, "Handles then fails", ListHandledThenFlakyDefinition(seed.RepositoryId, Guid.NewGuid().ToString("N")));
        var chain = await WalkThroughChildRunsAsync(await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel)));

        await AssertRunStatusAsync(chain[0], WorkflowRunStatus.Failure, "fixture check: the child ended on the node that failed after the handled failure");
        PersistedFailureJson.Read((await ReadFailedPayloadsAsync(chain[1])).First()).ShouldNotBeNull("fixture check: the OLD failure is on the child's ledger");
        (await ReadFailedRecordsAsync(chain[0])).Single(r => r.NodeId == "sub").Failure.ShouldBeNull("the child's decisive failure is the one that ended it, not one it handled earlier");
        (await RemedyForAsync(team.UserId, team.TeamId, chain[0], allowNone: true)).ShouldBeNull();
    }

    [Fact]
    public async Task A_failure_that_names_no_repository_is_traced_through_the_childs_scope_exactly_as_the_childs_own_page_does()
    {
        await using var gitlab = LoopbackResponder.Start(_ => "HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: 30\r\nConnection: close\r\n\r\n{\"message\":\"401 Unauthorized\"}");
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, $"http://127.0.0.1:{gitlab.Port}", $"glpat-{Guid.NewGuid():N}", existingTeam: team);
        // git.create_issue declares no repository input and a 401 names no credential: only the run's own launch scope could trace it.
        var child = await CreateNamedWorkflowAsync(team, "Files an issue", NodeThenEndDefinition("git.create_issue", seed.RepositoryId, """{"title":"t"}"""));
        var parentRunId = await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel));

        await SetScopeAsync(parentRunId, seed.RepositoryId);   // the parent was launched against the repository; the child, started by a node, was not

        var chain = await WalkThroughChildRunsAsync(parentRunId);

        var childRemedy = await RemedyForAsync(team.UserId, team.TeamId, chain[1]);
        var parentRemedy = await RemedyForAsync(team.UserId, team.TeamId, chain[0]);

        childRemedy.Repositories.ShouldBeEmpty("fixture check: the child's page traces nothing — the 401 names no repository and the child was launched against none");
        parentRemedy.Repositories.ShouldBeEmpty("the parent's is the child's remedy: not the parent's own launch scope read onto a failure that happened elsewhere");
        parentRemedy.Credential.ShouldBe(childRemedy.Credential);
        parentRemedy.Actions.Select(a => (a.Kind, a.Enabled)).ShouldBe(childRemedy.Actions.Select(a => (a.Kind, a.Enabled)));

        // Launched against the repository, the child's own page traces it — and so does the parent's, since it is the child's remedy.
        await SetScopeAsync(chain[1], seed.RepositoryId);

        (await RemedyForAsync(team.UserId, team.TeamId, chain[1])).Repositories.ShouldHaveSingleItem().Id.ShouldBe(seed.RepositoryId);
        (await RemedyForAsync(team.UserId, team.TeamId, chain[0])).Repositories.ShouldHaveSingleItem().Id.ShouldBe(seed.RepositoryId);
    }

    [Fact]
    public async Task A_fan_out_parent_judges_the_fix_from_when_its_child_failed_not_from_when_the_slowest_sibling_let_it_resume()
    {
        // The parent is resumed only once EVERY sub-workflow it waits on has finished, so its own failure record can be written long after the child's. A 401 on a
        // token that looks fine is shown fixed only by something that validated the credential SINCE the failure — and "since" is the child's failure, which is what the
        // child's own page measures. Here the person replaces the token between the child's failure and the slow sibling's end; both pages must say it is fixed.
        using var gitlab = new GitLabDoorkeeperStub();
        var token = $"glpat-{Guid.NewGuid():N}";
        gitlab.AddAccessToken(token, "nacho");
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, token, existingTeam: team);
        gitlab.RevokeAccessToken(token);
        var failing = await CreateNamedWorkflowAsync(team, "Failing child", ListThenEndDefinition(seed.RepositoryId));
        var slow = await CreateNamedWorkflowAsync(team, "Slow sibling", new WorkflowDefinition { SchemaVersion = 1, Nodes = [Node("start", "trigger.manual", null), Node("end", "builtin.terminal", null)], Edges = [Edge("start", "end")] });
        var parentRunId = await StartRunOfAsync(team, FanOutParent(failing, slow));

        await RunEngineAsync(parentRunId);   // parks on both children at once
        var failingRunId = await ChildRunOfNodeAsync(parentRunId, "subA");
        var slowRunId = await ChildRunOfNodeAsync(parentRunId, "subB");

        await RunEngineAsync(failingRunId);
        await AssertRunStatusAsync(failingRunId, WorkflowRunStatus.Failure, "fixture check: the revoked token ended the first child");
        await AssertRunStatusAsync(parentRunId, WorkflowRunStatus.Suspended, "fixture check: the parent is still parked on the slow sibling — it has not been told yet");

        var childFailedAt = await ReadFailureTimeAsync(failingRunId, team.TeamId);
        var before = await RemedyForAsync(team.UserId, team.TeamId, failingRunId);

        before.Code.ShouldBe(FailureCodes.ProviderUnauthorized);
        before.Resolved.ShouldBeFalse("fixture check: nothing validated the token since it failed");

        await Task.Delay(200);
        var replacedAt = DateTimeOffset.UtcNow;
        await GitLabCredentialSeed.SetLastValidatedAsync(_fixture, seed.CredentialId, replacedAt);   // the person replaced the token, from the child's page
        await Task.Delay(200);

        await RunEngineAsync(slowRunId);
        await RunEngineAsync(parentRunId);   // the last wait resolved: the parent walks, and fails on the first child's failure
        await AssertRunStatusAsync(parentRunId, WorkflowRunStatus.Failure, "fixture check: the child's failure ended the parent");

        (await ReadFailureTimeAsync(parentRunId, team.TeamId)).ShouldBeGreaterThan(replacedAt, "fixture check: the parent's own record is LATER than the fix — judged from it, the fix would read as older than the failure");

        var parentRemedy = await RemedyForAsync(team.UserId, team.TeamId, parentRunId);
        var childRemedy = await RemedyForAsync(team.UserId, team.TeamId, failingRunId);

        childRemedy.Resolved.ShouldBeTrue("the child's page: validated after it failed");
        parentRemedy.Origin!.RunId.ShouldBe(failingRunId);
        parentRemedy.Resolved.ShouldBeTrue("the parent's is the child's remedy, judged from when the child failed");
        parentRemedy.Actions.Select(a => (a.Kind, a.Enabled)).ShouldBe(childRemedy.Actions.Select(a => (a.Kind, a.Enabled)));
        parentRemedy.Actions[0].Kind.ShouldBe(RemedyActionKind.Rerun, "so running the parent again leads, exactly as it does on the child's page");
        parentRemedy.Repositories.Select(r => r.CredentialWorks).ShouldBe(childRemedy.Repositories.Select(r => r.CredentialWorks));
        (await ReadFailedRecordsAsync(parentRunId)).Single(r => r.NodeId == "subA").Failure!.Origin!.At.ShouldBe(childFailedAt);
    }

    [Theory]
    [InlineData(-120, true)]    // the child failed two minutes before the parent recorded it; the token was validated a minute before that record — AFTER the child failed: fixed
    [InlineData(-30, false)]    // the child failed half a minute before the record, AFTER the last validation: nothing has fixed it since
    [InlineData(null, false)]   // a record that kept no time of its own (an older writer's) is judged from the record that carries it, as it always was
    public async Task A_carried_failure_is_judged_from_the_time_it_kept_and_from_its_records_own_when_it_kept_none(int? failedSecondsBeforeTheRecord, bool fixedSince)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, $"glpat-{Guid.NewGuid():N}", existingTeam: team);
        var child = await CreateNamedWorkflowAsync(team, "Review", ListThenEndDefinition(seed.RepositoryId));
        var childRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, child.Id, team.TeamId);
        var parentRunId = await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel));
        DateTimeOffset? failedAt = failedSecondsBeforeTheRecord is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;

        await FailRunAsync(parentRunId, "Node 'sub' failed.");
        await RecordFailureAsync(parentRunId, "sub", ChildSentence, new PersistedFailure { Code = FailureCodes.ProviderUnauthorized, RepositoryId = seed.RepositoryId, Origin = new FailureOrigin { RunId = childRunId, NodeId = "list", At = failedAt } });
        await GitLabCredentialSeed.SetLastValidatedAsync(_fixture, seed.CredentialId, DateTimeOffset.UtcNow.AddSeconds(-60));

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, parentRunId);

        remedy.Origin!.RunId.ShouldBe(childRunId, "fixture check: the origin was read live and named — only then is its time the one to judge from");
        remedy.Resolved.ShouldBe(fixedSince);
    }

    [Fact]
    public async Task An_origin_that_is_not_this_teams_run_is_never_named_and_never_changes_the_remedy()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var other = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var foreign = await CreateNamedWorkflowAsync(other, "THE-OTHER-TEAMS-SECRET-WORKFLOW", ListThenEndDefinition(seed.RepositoryId));
        var foreignRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, foreign.Id, other.TeamId);
        var ours = await CreateNamedWorkflowAsync(team, "Ours", ListThenEndDefinition(seed.RepositoryId));
        var details = new Dictionary<string, string> { ["credentialId"] = seed.CredentialId.ToString() };

        var forged = await StartRunOfAsync(team, SubWorkflowParent(ours, SubWorkflowSite.TopLevel));
        await FailRunAsync(forged, "Node 'sub' failed.");
        await RecordFailureAsync(forged, "sub", ChildSentence, new PersistedFailure { Code = FailureCodes.CredentialDisconnected, Details = details, RepositoryId = seed.RepositoryId, Origin = new FailureOrigin { RunId = foreignRunId, NodeId = "list" } });

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, forged);

        remedy.Origin.ShouldBeNull("a run of another team is not named, nor linked to");
        JsonSerializer.Serialize(remedy).ShouldNotContain("THE-OTHER-TEAMS-SECRET-WORKFLOW");
        JsonSerializer.Serialize(remedy).ShouldNotContain(foreignRunId.ToString());
        remedy.Cause.ShouldNotContain("sub-workflow", Case.Insensitive);
        remedy.Credential!.Id.ShouldBe(seed.CredentialId, "the remedy is still the parent's own, read under the parent's team");

        // A run that is nobody's — gone, or never there — is not named either.
        var missing = await StartRunOfAsync(team, SubWorkflowParent(ours, SubWorkflowSite.TopLevel));
        await FailRunAsync(missing, "Node 'sub' failed.");
        await RecordFailureAsync(missing, "sub", ChildSentence, new PersistedFailure { Code = FailureCodes.CredentialDisconnected, Details = details, Origin = new FailureOrigin { RunId = Guid.NewGuid(), NodeId = "list" } });

        (await RemedyForAsync(team.UserId, team.TeamId, missing)).Origin.ShouldBeNull();
    }

    [Fact]
    public async Task A_parent_recorded_before_sub_workflows_carried_their_failures_answers_exactly_as_it_did()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var child = await CreateNamedWorkflowAsync(team, "Review", ListThenEndDefinition(seed.RepositoryId));
        var parentRunId = await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel));

        // What the engine wrote before: the sub node failed on the child's sentence alone, and the child's own run held the failure.
        await FailRunAsync(parentRunId, "Node 'sub' failed.");
        await RecordFailureAsync(parentRunId, "sub", ChildSentence, failure: null);

        using var scope = _fixture.BeginScope();

        (await scope.Resolve<IRunFailureReader>().ReadAsync(parentRunId, team.TeamId, CancellationToken.None)).ShouldBeNull();
        (await RemedyForAsync(team.UserId, team.TeamId, parentRunId, allowNone: true)).ShouldBeNull("the row has no failure to build a remedy from, exactly as it never did");
    }

    [Fact]
    public async Task The_resume_payload_carries_the_childs_failure_beside_the_status_outputs_and_error_it_always_carried()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        var child = await CreateNamedWorkflowAsync(team, "Review", ListThenEndDefinition(seed.RepositoryId));
        var chain = await WalkThroughChildRunsAsync(await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel)));

        var payload = await ReadResumePayloadAsync(chain[0]);

        payload.GetProperty("status").GetString().ShouldBe("Failure");
        payload.GetProperty("error").GetString().ShouldBe("Node 'list' failed.");
        payload.GetProperty("outputs").ValueKind.ShouldBe(JsonValueKind.Object);
        payload.GetProperty("failure").GetProperty("code").GetString().ShouldBe(FailureCodes.CredentialNeedsReconnect);
        payload.GetProperty("failure").GetProperty("origin").GetProperty("runId").GetGuid().ShouldBe(chain[1]);
        payload.GetProperty("failure").GetProperty("origin").GetProperty("nodeId").GetString().ShouldBe("list");
        payload.GetProperty("failure").GetProperty("origin").GetProperty("at").GetDateTimeOffset().ShouldBe(await ReadFailureTimeAsync(chain[1], team.TeamId), "the child's own failure time, for a parent resumed long after it");
        payload.GetRawText().ShouldNotContain("at-0", Case.Sensitive, "ids, names and codes travel — never a token");
        payload.GetRawText().ShouldNotContain("rt-0", Case.Sensitive);
    }

    [Fact]
    public async Task A_child_whose_failure_cannot_be_read_still_resumes_its_parent_with_the_sentence_alone()
    {
        // Reading what the child ended on is a courtesy to the parent's page. If the read itself fails, the parent must still be resumed — a parent left parked
        // because its child's ledger could not be read would be a run stuck forever on something that was only ever there to explain it.
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        var child = await CreateNamedWorkflowAsync(team, "Review", ListThenEndDefinition(seed.RepositoryId));
        var parentRunId = await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel));

        await RunEngineAsync(parentRunId);   // the parent parks on its child
        var childRunId = await ChildRunIdAsync(parentRunId);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var failed = await db.WorkflowRun.SingleAsync(r => r.Id == childRunId);

            failed.Status = WorkflowRunStatus.Failure;
            failed.Error = "Node 'list' failed.";
            failed.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var service = new CodeSpace.Core.Services.Workflows.Engine.SubworkflowService(db, scope.Resolve<CodeSpace.Core.Services.Workflows.RunSources.IRunStarter>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Dispatch.IWorkflowRunDispatcher>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Engine.IWorkflowResumeService>(), new UnreadableFailures(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeSpace.Core.Services.Workflows.Engine.SubworkflowService>.Instance);
            var resumed = await service.ResumeParentIfChildTerminalAsync(await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == childRunId), WorkflowRunStatus.Failure, "Node 'list' failed.", CancellationToken.None);

            resumed.ShouldBeTrue("the parent was resumed although the child's failure could not be read");
        }

        var payload = await ReadResumePayloadAsync(parentRunId);

        payload.GetProperty("status").GetString().ShouldBe("Failure");
        payload.GetProperty("error").GetString().ShouldBe("Node 'list' failed.");
        payload.TryGetProperty("failure", out _).ShouldBeFalse("it carries the sentence alone");

        await RunEngineAsync(parentRunId);

        (await ReadFailedRecordsAsync(parentRunId)).Single(r => r.NodeId == "sub").Failure.ShouldBeNull("and the parent fails as it always did");
    }

    [Fact]
    public async Task A_child_that_was_cancelled_carries_nothing_even_when_an_agent_run_of_it_met_a_refused_push()
    {
        // The reader answers for a run that did not fail with one thing — a push the remote refused, which the run survived. A cancelled child did not end on
        // that: the parent is told it was cancelled, and a card sending someone to reconnect a credential for it would blame the wrong thing.
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        var child = await CreateNamedWorkflowAsync(team, "Review", ListThenEndDefinition(seed.RepositoryId));
        var parentRunId = await StartRunOfAsync(team, SubWorkflowParent(child, SubWorkflowSite.TopLevel));

        await RunEngineAsync(parentRunId);   // the parent parks on its child
        var childRunId = await ChildRunIdAsync(parentRunId);

        await SeedAgentRunAsync(team.TeamId, childRunId, "review", string.Empty, new PersistedFailure { Code = FailureCodes.GitCredentialRejected, RepositoryId = seed.RepositoryId });

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var cancelled = await db.WorkflowRun.SingleAsync(r => r.Id == childRunId);

            cancelled.Status = WorkflowRunStatus.Cancelled;
            cancelled.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            (await scope.Resolve<IRunFailureReader>().ReadAsync(childRunId, team.TeamId, CancellationToken.None))!.Failure.Code.ShouldBe(FailureCodes.GitCredentialRejected, "fixture check: the reader WOULD answer the refused push for a run that did not fail");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            (await scope.Resolve<CodeSpace.Core.Services.Workflows.Engine.ISubworkflowService>().ResumeParentIfChildTerminalAsync(await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == childRunId), WorkflowRunStatus.Cancelled, null, CancellationToken.None)).ShouldBeTrue();
        }

        var cancelledPayload = await ReadResumePayloadAsync(parentRunId);

        cancelledPayload.TryGetProperty("failure", out _).ShouldBeFalse($"a cancelled child is a cancel, whatever its agent runs met: {cancelledPayload.GetRawText()}");

        await RunEngineAsync(parentRunId);

        var sub = (await ReadFailedRecordsAsync(parentRunId)).Single(r => r.NodeId == "sub");

        sub.Error.ShouldBe("Sub-workflow run failed: see the child run");
        sub.Failure.ShouldBeNull();
    }

    /// <summary>A failure reader whose read always fails — the database hiccup the courtesy must survive.</summary>
    private sealed class UnreadableFailures : IRunFailureReader
    {
        public Task<RecordedFailure?> ReadAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => throw new InvalidOperationException("the child's ledger could not be read");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private sealed record NamedWorkflow(Guid Id, string Name);

    private async Task<NamedWorkflow> CreateNamedWorkflowAsync((Guid TeamId, Guid UserId) team, string name, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(team.UserId, team.TeamId, Roles.Admin);
        var id = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = name, Description = null, Definition = definition, Activations = new List<WorkflowActivationInput>(), Enabled = true });

        return new NamedWorkflow(id, name);
    }

    /// <summary>The workflow that is the PARENT: it runs <paramref name="child"/> as a sub-workflow, on its own, inside a map's body, or inside a loop's body.</summary>
    private static WorkflowDefinition SubWorkflowParent(NamedWorkflow child, SubWorkflowSite site)
    {
        var sub = Node("sub", "flow.subworkflow", site == SubWorkflowSite.TopLevel ? null : site == SubWorkflowSite.InMap ? "map" : "loop", config: $$"""{ "workflowId": "{{child.Id}}" }""", inputs: """{ "inputs": {} }""");

        return site switch
        {
            SubWorkflowSite.TopLevel => new WorkflowDefinition { SchemaVersion = 1, Nodes = [Node("start", "trigger.manual", null), sub, Node("end", "builtin.terminal", null)], Edges = [Edge("start", "sub"), Edge("sub", "end")] },
            SubWorkflowSite.InMap => new WorkflowDefinition { SchemaVersion = 1, Nodes = [Node("start", "trigger.manual", null), MapNode("map", null), Node("ms", "flow.map_start", "map"), sub, Node("end", "builtin.terminal", null)], Edges = [Edge("start", "map"), Edge("map", "end"), Edge("ms", "sub")] },
            _ => new WorkflowDefinition { SchemaVersion = 1, Nodes = [Node("start", "trigger.manual", null), LoopNode("loop", null), Node("ls", "flow.loop_start", "loop"), sub, Node("end", "builtin.terminal", null)], Edges = [Edge("start", "loop"), Edge("loop", "end"), Edge("ls", "sub")] },
        };
    }

    /// <summary>The parent that runs two children at once and waits for both: <paramref name="first"/> at <c>subA</c>, <paramref name="second"/> at <c>subB</c>.</summary>
    private static WorkflowDefinition FanOutParent(NamedWorkflow first, NamedWorkflow second) => new()
    {
        SchemaVersion = 1,
        Nodes =
        [
            Node("start", "trigger.manual", null),
            Node("subA", "flow.subworkflow", null, config: $$"""{ "workflowId": "{{first.Id}}" }""", inputs: """{ "inputs": {} }"""),
            Node("subB", "flow.subworkflow", null, config: $$"""{ "workflowId": "{{second.Id}}" }""", inputs: """{ "inputs": {} }"""),
            Node("end", "builtin.terminal", null)
        ],
        Edges = [Edge("start", "subA"), Edge("start", "subB"), Edge("subA", "end"), Edge("subB", "end")],
    };

    /// <summary>The child run a parent's sub-workflow node is parked on — the token of its pending wait.</summary>
    private async Task<Guid> ChildRunOfNodeAsync(Guid parentRunId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        var token = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == parentRunId && w.NodeId == nodeId && w.WaitKind == WorkflowWaitKinds.Subworkflow).Select(w => w.Token).SingleAsync();

        return Guid.Parse(token);
    }

    /// <summary>Saves <paramref name="definition"/> as a workflow of the team and seeds a manual run of it — one element for a map to run a branch over.</summary>
    private async Task<Guid> StartRunOfAsync((Guid TeamId, Guid UserId) team, WorkflowDefinition definition)
    {
        var workflow = await CreateNamedWorkflowAsync(team, "Parent " + Guid.NewGuid().ToString("N")[..6], definition);

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflow.Id, team.TeamId, payloadJson: JsonSerializer.Serialize(new { things = new[] { Guid.NewGuid() } }));
    }

    /// <summary>
    /// Walks a run and every child run it parks on, to the end: the run until it parks on a child, the child the same way, and the run again once the child's
    /// completion has resumed it. Returns the runs in the order they started — the run first, then the child, then the child's child.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> WalkThroughChildRunsAsync(Guid runId)
    {
        var chain = new List<Guid> { runId };

        await RunEngineAsync(runId);

        if (await ChildRunIdsAsync(runId) is not [var childId]) return chain;

        chain.AddRange(await WalkThroughChildRunsAsync(childId));
        await RunEngineAsync(runId);

        return chain;
    }

    private async Task<IReadOnlyList<Guid>> ChildRunIdsAsync(Guid parentRunId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.ParentRunId == parentRunId && r.SourceType == WorkflowRunSourceTypes.ChildWorkflow).OrderBy(r => r.CreatedDate).Select(r => r.Id).ToListAsync();
    }

    private async Task<Guid> ChildRunIdAsync(Guid parentRunId) => (await ChildRunIdsAsync(parentRunId)).Single();

    /// <summary>When the run's own record of its failure was written — what its page judges "fixed since" from.</summary>
    private async Task<DateTimeOffset> ReadFailureTimeAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return (await scope.Resolve<IRunFailureReader>().ReadAsync(runId, teamId, CancellationToken.None))!.At;
    }

    /// <summary>What the parent's sub-workflow wait was resolved with — the payload the child's completion stamped, which the node read when it resumed.</summary>
    private async Task<JsonElement> ReadResumePayloadAsync(Guid parentRunId)
    {
        using var scope = _fixture.BeginScope();
        var payload = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == parentRunId && w.WaitKind == WorkflowWaitKinds.Subworkflow && w.Status == WorkflowWaitStatuses.Resolved).Select(w => w.PayloadJson).SingleAsync();

        return JsonDocument.Parse(payload!).RootElement.Clone();
    }

    private async Task<IReadOnlyList<string>> ReadFailedPayloadsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed).OrderBy(r => r.Sequence).Select(r => r.PayloadJson).ToListAsync();
    }

    private async Task SetScopeAsync(Guid runId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);

        run.ScopeRepositoryIds = [repositoryId];
        await db.SaveChangesAsync();
    }

    /// <summary>Ends a run on a sentence, as the engine would have — for a run whose ledger a test writes by hand.</summary>
    private async Task FailRunAsync(Guid runId, string error)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);

        run.Status = WorkflowRunStatus.Failure;
        run.Error = error;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task RecordFailureAsync(Guid runId, string nodeId, string sentence, PersistedFailure? failure)
    {
        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<IRunRecordLogger>();

        await logger.NodeStartedAsync(runId, nodeId, string.Empty, new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), CancellationToken.None);
        await logger.NodeFailedAsync(runId, nodeId, string.Empty, sentence, TimeSpan.FromMilliseconds(5), failure, CancellationToken.None);
    }
}
