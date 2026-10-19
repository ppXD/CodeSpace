using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.ProviderInstances;
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
/// 🟢 High fidelity — a provider connection that fails UNDER A RUN is written down as what it is. The real workflow engine walks a
/// real <c>git.list_prs</c> node, which calls the real GitLab provider through the real credential resolver, OAuth refresher and
/// advisory lock over real Postgres; the one fake is GitLab (<see cref="GitLabDoorkeeperStub"/>, and a loopback host that
/// redirects). The node's failure is recorded on its <c>node.failed</c> ledger record as a machine-readable <c>failure</c> beside the
/// sentence, and the remedy computed from it answers, for the person asking, what is wrong and what they can do.
///
/// <para>One scenario per way a connection fails: a dead OAuth grant, a token GitLab revoked (on a list and on each git write node, which word
/// the 401 themselves), a disconnected credential, a renewal GitLab did not answer (which blames nobody), an address that redirects — whose
/// repair is proved against the real update command, not an edit production would refuse — plus the negatives that keep the record honest: a
/// failure that is not a connection records nothing, a failure the run handled with an error edge has no remedy, and neither has one the
/// run handled before it failed on something else.</para>
///
/// <para>The one fake is GitLab, so the only thing between these tests and production is the provider's own answers.</para>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProviderFailureThroughTheEngineFlowTests
{
    private readonly PostgresFixture _fixture;

    public ProviderFailureThroughTheEngineFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_dead_oauth_grant_is_recorded_on_the_failed_node_and_the_remedy_says_reconnect()
    {
        using var gitlab = new GitLabDoorkeeperStub();   // never heard of the pair the row holds: the refresh is answered invalid_grant
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, existingTeam: team);
        await RecordThePersonItWasConnectedForAsync(seed.CredentialId, team.UserId);

        var runId = await RunListPullRequestsAsync(team, seed.RepositoryId);

        var failure = await ReadRecordedFailureAsync(runId);

        failure.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        failure.DetailGuid("credentialId").ShouldBe(seed.CredentialId, "the failure names the credential the provider refused");
        failure.RepositoryId.ShouldBe(seed.RepositoryId, "and the repository the node was acting on, read off its declared repository input");
        (await ReadRunErrorAsync(runId)).ShouldBe("Node 'list' failed.", "the sentence the run always carried is untouched");

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, runId);

        remedy.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        remedy.Resolved.ShouldBeFalse();
        remedy.Credential!.Id.ShouldBe(seed.CredentialId);
        remedy.Credential.Status.ShouldBe(CredentialStatus.Error);
        remedy.Credential.Health.ShouldBe(CredentialHealth.Rejected);
        remedy.Repositories.ShouldHaveSingleItem().FullPath.ShouldBe("acme/api");
        remedy.Repositories[0].CredentialWorks.ShouldBeFalse();
        remedy.Title.ShouldBe("GitLab no longer accepts the credential this run used");
        remedy.Cause.ShouldContain("“GitLab · NACHO.L”");
        remedy.Actions.Select(a => (a.Kind, a.Enabled)).ShouldBe(new[]
        {
            (RemedyActionKind.Reconnect, true), (RemedyActionKind.Relink, true), (RemedyActionKind.OpenProviders, true), (RemedyActionKind.Rerun, false)
        }, "an Owner may do everything but run it again — that waits for the fix");
    }

    [Fact]
    public async Task A_renewal_gitlab_did_not_answer_is_recorded_naming_the_answer_and_the_remedy_does_not_send_anyone_to_repair_the_credential()
    {
        await using var gitlab = LoopbackResponder.Start(_ => "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");   // an outage: not an OAuth answer at all
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, $"http://127.0.0.1:{gitlab.Port}", "at-0", "rt-0", expired: true, existingTeam: team);

        var runId = await RunListPullRequestsAsync(team, seed.RepositoryId);

        var failure = await ReadRecordedFailureAsync(runId);

        failure.Code.ShouldBe(FailureCodes.OAuthExchangeFailed, "the provider did not answer; it did not refuse the grant (that would be credential_needs_reconnect)");
        failure.DetailGuid("credentialId").ShouldBe(seed.CredentialId);
        failure.Detail("oauthError").ShouldBe("http_503");
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId)).Status.ShouldBe(CredentialStatus.Active, "fixture check: nothing marked the credential — an outage is not a verdict on it");

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, runId);

        remedy.Title.ShouldBe("Renewing the sign-in this run used did not work");
        remedy.Cause.ShouldContain("(it answered http_503)");
        remedy.Cause.ShouldContain("says nothing about the credential");
        remedy.Actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "a healthy credential is not sent to be reconnected for somebody else's outage");
        remedy.Actions.Single(a => a.Kind == RemedyActionKind.Rerun).Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_token_gitlab_revoked_is_recorded_as_a_rejected_credential_traced_to_its_credential_through_the_repository()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var token = $"glpat-{Guid.NewGuid():N}";
        gitlab.AddAccessToken(token, "nacho");
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, token, existingTeam: team);
        gitlab.RevokeAccessToken(token);

        var runId = await RunListPullRequestsAsync(team, seed.RepositoryId);

        var failure = await ReadRecordedFailureAsync(runId);

        failure.Code.ShouldBe(FailureCodes.ProviderUnauthorized);
        failure.Detail("providerStatus").ShouldBe("401");
        failure.DetailGuid("credentialId").ShouldBeNull("GitLab's answer names no credential");
        failure.RepositoryId.ShouldBe(seed.RepositoryId);

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, runId);

        remedy.Credential!.Id.ShouldBe(seed.CredentialId, "traced through the repository the node acted on");
        remedy.Credential.AuthType.ShouldBe(AuthType.Pat);
        remedy.Actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.ReplaceToken, RemedyActionKind.Relink, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "a pasted token is replaced, not reconnected");
        remedy.Actions.Single(a => a.Kind == RemedyActionKind.Rerun).Enabled.ShouldBeTrue("a 401 on a row that looks fine cannot be shown fixed, so running it again stays open");
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId)).Status.ShouldBe(CredentialStatus.Active, "fixture check: nothing marked the credential — only the failure knows");
    }

    [Fact]
    public async Task A_disconnected_credential_is_recorded_and_re_linking_the_repository_resolves_the_remedy()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);

        var runId = await RunListPullRequestsAsync(team, seed.RepositoryId);

        var failure = await ReadRecordedFailureAsync(runId);

        failure.Code.ShouldBe(FailureCodes.CredentialDisconnected);
        failure.DetailGuid("credentialId").ShouldBe(seed.CredentialId);

        var before = await RemedyForAsync(team.UserId, team.TeamId, runId);
        before.Resolved.ShouldBeFalse();
        before.Title.ShouldBe("The credential this run used was disconnected");

        // The repository is linked to a credential that works — the fix an Admin makes from the card.
        var replacement = await AddLiveCredentialAsync(seed);
        await BindAsync(seed.RepositoryId, replacement);

        var after = await RemedyForAsync(team.UserId, team.TeamId, runId);

        after.Resolved.ShouldBeTrue("what failed works again");
        after.Title.ShouldBe("This connection works again");
        after.Repositories.ShouldHaveSingleItem().CredentialWorks.ShouldBeTrue();
        after.Actions[0].Kind.ShouldBe(RemedyActionKind.Rerun, "running it again leads once the fix shows");
        after.Actions[0].Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_provider_whose_address_redirects_offers_only_the_repair_the_server_accepts_and_that_repair_resolves_it()
    {
        await using var elsewhere = LoopbackResponder.Start(_ => LoopbackResponder.Ok("[]"));
        var destination = $"http://localhost:{elsewhere.Port}";
        await using var redirecting = LoopbackResponder.Start(_ => LoopbackResponder.Redirect(destination + "/api/v4/x"));
        var baseUrl = $"http://127.0.0.1:{redirecting.Port}";
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, baseUrl, $"glpat-{Guid.NewGuid():N}", existingTeam: team);

        var runId = await RunListPullRequestsAsync(team, seed.RepositoryId);

        var failure = await ReadRecordedFailureAsync(runId);

        failure.Code.ShouldBe(FailureCodes.ProviderBaseUrlRedirects);
        failure.Detail("requestedOrigin").ShouldBe(baseUrl);
        failure.Detail("finalOrigin").ShouldBe(destination);
        elsewhere.Accepted.ShouldBe(0, "fixture check: the redirect was not followed, so the token went nowhere else");

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, runId);

        remedy.Resolved.ShouldBeFalse();
        remedy.RedirectsTo.ShouldBe(destination);
        remedy.ProviderInstance!.Id.ShouldBe(seed.InstanceId);
        remedy.Title.ShouldBe("The provider's address redirects somewhere else");
        remedy.Cause.ShouldContain("1 credential is connected through it", Case.Insensitive);
        remedy.Cause.ShouldContain("Disconnect them first");
        remedy.Actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "another host with a token connected: no edit button, because the server would refuse the edit it names");

        // The refusal the old card ran into: the SAME edit through the real command, with the token still connected.
        var refused = await Should.ThrowAsync<ProviderEndpointCredentialsBoundException>(() => UpdateAddressAsync(team, seed.InstanceId, destination));

        refused.Message.ShouldContain("1 connected credential");

        // What the cause says to do: disconnect them first — and now the edit is offered, accepted, and resolves it.
        await DisconnectThroughTheCommandAsync(team, seed.CredentialId);

        var afterDisconnect = await RemedyForAsync(team.UserId, team.TeamId, runId);

        afterDisconnect.Actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.EditProviderUrl, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "nothing is connected through it any more, so the edit the card names is accepted");
        afterDisconnect.Actions.Single(a => a.Kind == RemedyActionKind.EditProviderUrl).Enabled.ShouldBeTrue();

        await UpdateAddressAsync(team, seed.InstanceId, destination);

        (await RemedyForAsync(team.UserId, team.TeamId, runId)).Resolved.ShouldBeTrue("the address was edited off the one that redirected, through the same command the card's button runs");
    }

    [Theory]
    // the node, its inputs, whether it declares the repository it acts on (so the failure can be traced to its credential on its own — the issue nodes declare none, and are traced through the run's repositories when it has any)
    [InlineData("git.open_pr", """{"title":"t","sourceBranch":"feature","targetBranch":"main"}""", true)]
    [InlineData("git.merge_pr", """{"number":7}""", true)]
    [InlineData("git.pr_review", """{"number":7,"verdict":"approve"}""", true)]
    [InlineData("git.create_issue", """{"title":"t"}""", false)]
    [InlineData("git.close_issue", """{"number":7}""", false)]
    [InlineData("git.comment_issue", """{"number":7,"body":"hello"}""", false)]
    public async Task A_token_gitlab_rejects_on_a_git_write_node_is_recorded_as_a_rejected_credential_though_the_node_words_its_own_failure(string typeKey, string inputs, bool declaresItsRepository)
    {
        await using var gitlab = LoopbackResponder.Start(_ => "HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: 30\r\nConnection: close\r\n\r\n{\"message\":\"401 Unauthorized\"}");
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, $"http://127.0.0.1:{gitlab.Port}", $"glpat-{Guid.NewGuid():N}", existingTeam: team);
        var workflowId = await CreateWorkflowAsync(team, NodeThenEndDefinition(typeKey, seed.RepositoryId, inputs));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId);

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var nodeError = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed).Select(r => r.PayloadJson).SingleAsync();

        JsonDocument.Parse(nodeError).RootElement.GetProperty("error").GetString().ShouldContain("401", customMessage: "fixture check: the node worded the failure itself, from the provider's 401 — that sentence must stay as it was");

        var failure = await ReadRecordedFailureAsync(runId);

        failure.Code.ShouldBe(FailureCodes.ProviderUnauthorized, $"{typeKey}: a rejected token is the connection's failure however the node words its own");
        failure.Detail("providerStatus").ShouldBe("401");

        var remedy = await RemedyForAsync(team.UserId, team.TeamId, runId);

        remedy.Code.ShouldBe(FailureCodes.ProviderUnauthorized);

        if (!declaresItsRepository) return;

        failure.RepositoryId.ShouldBe(seed.RepositoryId, "traced through the repository the node declares it acts on");
        remedy.Credential!.Id.ShouldBe(seed.CredentialId);
        remedy.Actions.Select(a => a.Kind).ShouldContain(RemedyActionKind.ReplaceToken);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_provider_connection_records_no_failure_and_has_no_remedy()
    {
        var key = Guid.NewGuid().ToString("N");
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(team, FlakyThenEndDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId);

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var payload = await db.WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed).Select(r => r.PayloadJson).SingleAsync();

        JsonDocument.Parse(payload).RootElement.TryGetProperty("failure", out _).ShouldBeFalse("the record is byte-identical to what it always was");
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Failure);
        (await scope.Resolve<IRunFailureReader>().ReadAsync(runId, team.TeamId, CancellationToken.None)).ShouldBeNull();
        (await RemedyForAsync(team.UserId, team.TeamId, runId, allowNone: true)).ShouldBeNull();
    }

    [Fact]
    public async Task A_provider_failure_the_run_handled_with_an_error_edge_leaves_a_successful_run_with_no_remedy()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var workflowId = await CreateWorkflowAsync(team, ListWithErrorEdgeDefinition(seed.RepositoryId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId);

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();

        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "fixture check: the error edge handled the failure");
        var payload = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed).Select(r => r.PayloadJson).SingleAsync();

        PersistedFailureJson.Read(payload)!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "the node's own record still says what happened");
        (await scope.Resolve<IRunFailureReader>().ReadAsync(runId, team.TeamId, CancellationToken.None)).ShouldBeNull("but the run did not fail on it");
        (await RemedyForAsync(team.UserId, team.TeamId, runId, allowNone: true)).ShouldBeNull("a run that handled it is not one anybody has to repair");
    }

    [Fact]
    public async Task A_provider_failure_the_run_handled_and_then_failed_on_something_else_has_no_remedy_for_the_old_failure()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var key = Guid.NewGuid().ToString("N");
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false, existingTeam: team);
        await DisconnectAsync(seed.CredentialId);
        var workflowId = await CreateWorkflowAsync(team, ListHandledThenFlakyDefinition(seed.RepositoryId, key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId);

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        var failedNodes = await db.WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed).OrderBy(r => r.Sequence).Select(r => new { r.NodeId, r.PayloadJson }).ToListAsync();

        run.Status.ShouldBe(WorkflowRunStatus.Failure, "fixture check: the run failed");
        run.Error.ShouldBe("Node 'flaky' failed.", "fixture check: it failed on the flaky node, not the connection");
        failedNodes.Select(n => n.NodeId).ShouldBe(new[] { "list", "flaky" });
        PersistedFailureJson.Read(failedNodes[0].PayloadJson)!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "fixture check: the OLD failure is on the ledger, carrying the code the page must not show");
        PersistedFailureJson.Read(failedNodes[1].PayloadJson).ShouldBeNull();

        (await scope.Resolve<IRunFailureReader>().ReadAsync(runId, team.TeamId, CancellationToken.None)).ShouldBeNull("the connection failure was handled by the error edge; the run ended on the node that failed after it");
        (await RemedyForAsync(team.UserId, team.TeamId, runId, allowNone: true)).ShouldBeNull("so no card blames a credential for a run that failed for another reason");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid> RunListPullRequestsAsync((Guid TeamId, Guid UserId) team, Guid repositoryId)
    {
        var workflowId = await CreateWorkflowAsync(team, ListThenEndDefinition(repositoryId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId);

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();

        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Failure, "fixture check: the connection's failure failed the run");

        return runId;
    }

    private async Task<PersistedFailure> ReadRecordedFailureAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var teamId = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.TeamId).SingleAsync();
        var recorded = await scope.Resolve<IRunFailureReader>().ReadAsync(runId, teamId, CancellationToken.None);

        recorded.ShouldNotBeNull("the node's record carries the failure");
        recorded.At.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        return recorded.Failure;
    }

    private async Task<string?> ReadRunErrorAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Error).SingleAsync();
    }

    /// <summary>The remedy as the given member of the team sees it — a membership-scoped request, not an instance Admin's.</summary>
    private async Task<ProviderRemedy?> RemedyForAsync(Guid userId, Guid teamId, Guid runId, bool allowNone = false)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var remedy = await scope.Resolve<IProviderRemedyService>().GetForRunAsync(runId, teamId, CancellationToken.None);

        if (!allowNone) remedy.ShouldNotBeNull("the run recorded a failure a person can repair");

        return remedy;
    }

    /// <summary>
    /// What a connect always does and the shared seed leaves out: a sign-in is somebody's, so the credential names its person. A reconnect re-authorizes a
    /// person's own connection — it refuses one with none — so the button the remedy shows for it is only honest on a row shaped as a connect shapes it.
    /// </summary>
    private async Task RecordThePersonItWasConnectedForAsync(Guid credentialId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET owner_user_id = {userId} WHERE id = {credentialId}");
    }

    private async Task DisconnectAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = 'Revoked', encrypted_payload = '' WHERE id = {credentialId}");
    }

    private async Task BindAsync(Guid repositoryId, Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE repository SET credential_id = {credentialId} WHERE id = {repositoryId}");
    }

    /// <summary>Another working connection on the SAME provider instance — what a team's service token, or a person's second sign-in, is.</summary>
    private async Task<Guid> AddLiveCredentialAsync(SeededGitLabCredential seed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var payload = new CodeSpace.Messages.Credentials.PatPayload { Token = $"glpat-{Guid.NewGuid():N}" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = seed.TeamId, ProviderInstanceId = seed.InstanceId, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.GroupAccessToken, DisplayName = "GitLab · group token",
            EncryptedPayload = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>().Encrypt(scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>().Serialize(payload)), Status = CredentialStatus.Active,
        };

        db.Credential.Add(credential);
        await db.SaveChangesAsync();

        return credential.Id;
    }

    /// <summary>The edit the card's Edit provider address button opens: the real update command, as the team's Admin, with the address the provider pointed at for both the base and the API URL — and the OAuth client secret typed again, which the form asks for whenever the host changes.</summary>
    private async Task UpdateAddressAsync((Guid TeamId, Guid UserId) team, Guid instanceId, string address)
    {
        using var scope = _fixture.BeginScopeAs(team.UserId, team.TeamId, Roles.Admin);

        await scope.Resolve<IMediator>().Send(new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = address, ApiUrl = address, OauthClientSecret = GitLabDoorkeeperStub.DefaultClientSecret });
    }

    /// <summary>Disconnect, through the real command — what the cause says to do before the address may move.</summary>
    private async Task DisconnectThroughTheCommandAsync((Guid TeamId, Guid UserId) team, Guid credentialId)
    {
        using var scope = _fixture.BeginScopeAs(team.UserId, team.TeamId, Roles.Admin);

        await scope.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credentialId });
    }

    private async Task<Guid> CreateWorkflowAsync((Guid TeamId, Guid UserId) team, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(team.UserId, team.TeamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "provider-failure-" + Guid.NewGuid().ToString("N")[..6], Description = null, Definition = definition,
            Activations = new List<WorkflowActivationInput>(), Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static NodeDefinition ListNode(Guid repositoryId) => new() { Id = "list", TypeKey = "git.list_prs", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{"repositoryId":"{{repositoryId}}"}""") };

    // start → <node> → end. No error edge: the node's failure fails the run.
    private static WorkflowDefinition NodeThenEndDefinition(string typeKey, Guid repositoryId, string inputs) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "write", TypeKey = typeKey, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(inputs.Insert(1, $"\"repositoryId\":\"{repositoryId}\",")) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "write" }, new() { From = "write", To = "end" } },
    };

    // start → list (git.list_prs) → end. No error edge: the connection's failure fails the run.
    private static WorkflowDefinition ListThenEndDefinition(Guid repositoryId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            ListNode(repositoryId),
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "list" }, new() { From = "list", To = "end" } },
    };

    // start → list → end; list =(error)=> caught. The run handles the failure and succeeds.
    private static WorkflowDefinition ListWithErrorEdgeDefinition(Guid repositoryId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            ListNode(repositoryId),
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "caught", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"message":"{{nodes.list.outputs.error.message}}"}""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "list" }, new() { From = "list", To = "end" }, new() { From = "list", To = "caught", SourceHandle = WorkflowHandles.Error },
        },
    };

    // start → list =(error)=> flaky → end. list's failure is handled; flaky has no error edge, so IT fails the run.
    private static WorkflowDefinition ListHandledThenFlakyDefinition(Guid repositoryId, string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            ListNode(repositoryId),
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{key}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "list" }, new() { From = "list", To = "end" }, new() { From = "list", To = "flaky", SourceHandle = WorkflowHandles.Error }, new() { From = "flaky", To = "end" },
        },
    };

    private static WorkflowDefinition FlakyThenEndDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{key}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "flaky" }, new() { From = "flaky", To = "end" } },
    };
}
