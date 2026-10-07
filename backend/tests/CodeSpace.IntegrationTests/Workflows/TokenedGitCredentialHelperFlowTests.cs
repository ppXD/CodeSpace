using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Integrators;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): the REAL <see cref="LocalGitWorkspaceProvider"/>, <see cref="RemoteTipResolver"/>,
/// <see cref="LocalGitBranchIntegrator"/> and <see cref="SupervisorAcceptanceGrader"/> on the real
/// <see cref="LocalProcessRunner"/>, with real <c>git</c> and <c>git-lfs</c>, against a loopback smart-HTTP remote
/// (<see cref="GitPublishRemoteFixture"/>) that demands a FAKE token for every request, read or write. The operator's host
/// is simulated by a scratch <c>HOME</c> whose global config (<c>GIT_CONFIG_GLOBAL</c>, with system config off) sets
/// <c>credential.helper=store --file=&lt;scratch&gt;</c> plus a second store scoped to the remote's URL — the setup under
/// which a tokened run would leave its token on disk.
///
/// <para>A tokened command names the remote by a URL without userinfo and carries the token in its environment, where a
/// credential helper scoped to the remote answers for it. Each tokened operation must authenticate that way while no argv
/// the runner is handed carries the token or a URL's userinfo, no file under the clone or the publish repo holds it at any
/// point while the operation runs (a watch scans them throughout, against a deliberately slow remote), and both store files
/// stay without it. The positive controls: the same production code with ONE named git subcommand run without the
/// credential-helper reset and with trace2 on leaves the token in that store, so its absence in the real run is the reset's
/// doing; and a raw clone given the token, in the URL or in a Basic header, leaves it where the watch finds it.</para>
///
/// <para>A remote that redirects to another authority must get no credential sent there: a harvester at that authority asks
/// for one, and the operation fails instead. Its positive control is the same redirect followed with the token in the URL,
/// as git was handed it before: the harvester collects it. An untokened clone must still authenticate through the
/// operator's helper, and a tokened one must still reach the remote through a proxy whose password that helper holds: the
/// reset covers the tokened remote only. The operator's trace2 targets must record no credential even when they name the
/// credential variables, and the LFS downloads an integration or a grade makes through its origin must store nothing.</para>
///
/// <para>A token the remote refuses must fail an LFS command at once, with the reason, after a handful of requests; its
/// positive control is the helper ignoring erase, which keeps git-lfs retrying until the command is killed. The operator's
/// <c>url.&lt;base&gt;.insteadOf</c> and <c>pushInsteadOf</c> rules must move no tokened command; their positive control is
/// the same command without the pin that maps the remote to itself, which they move.</para>
///
/// <para>Each test owns its remote, proxy and temp tree and removes them on every path; it skips on Windows or without
/// git, and the LFS rows without git-lfs. Nothing reads or writes the real global config, system config or keychain.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class TokenedGitCredentialHelperFlowTests
{
    private const string ReadmePatch = "diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-base, revised\n+integrated\n";

    /// <summary>Each git response waits this long, so a clone or a push runs for long enough to be watched while it does.</summary>
    private static readonly TimeSpan SlowRemote = TimeSpan.FromMilliseconds(150);

    [Theory]
    [InlineData(null)]          // every tokened command carries the reset
    [InlineData("ls-remote")]   // positive controls: the soft-ref probe without it,
    [InlineData("clone")]       //   the clone,
    [InlineData("fetch")]       //   or the pin's fetch rungs through origin
    public async Task Provisioning_a_tokened_workspace_leaves_no_token_on_an_argv_on_disk_or_in_the_operators_credential_store(string? unreset)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Runner.Unreset = unreset;
        ctx.Remote.ResponseDelay = SlowRemote;

        await using var watch = ctx.WatchTheWorkspaces();
        await using var handle = await ctx.ProvisionAsync(GitPublishRemoteFixture.FakeToken);
        await watch.StopAsync();

        handle.Repositories.Single().BaseSha.ShouldBe(ctx.PinnedSha, "the probe, the clone and the pin's fetch all authenticated with the token");
        ctx.Runner.Ran(unreset ?? "fetch").ShouldBeTrue("fixture check: the command under test ran");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ShouldNeverHaveSeenTheToken(watch, "the clone");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lfs")]         // positive controls: the LFS upload without the reset,
    [InlineData("push")]        //   the push,
    [InlineData("ls-remote")]   //   or the readback
    public async Task Publishing_leaves_no_token_on_an_argv_on_disk_or_in_the_operators_credential_store(string? unreset)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        var lfs = await GitLfsAvailableAsync();
        if (unreset == "lfs" && !lfs) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        await using var handle = await ctx.ProvisionAsync(GitPublishRemoteFixture.FakeToken);
        ctx.ShouldHoldTheToken(stored: false);

        var oid = lfs ? await ctx.AgentCommitsLfsFileAsync(handle.Directory) : null;
        await ctx.AgentCommitsAsync(handle.Directory);
        ctx.Runner.Unreset = unreset;
        ctx.Remote.ResponseDelay = SlowRemote;

        await using var watch = ctx.WatchTheWorkspaces();
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);
        await watch.StopAsync();

        ctx.Remote.AuthenticatedPushRequests.ShouldBeGreaterThan(0, "the push authenticated with the token");
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldBe(await ctx.RemoteShaAsync(ctx.BranchName), "the readback authenticated and confirmed the pushed tip");
        if (oid is not null) ctx.Remote.HasLfsObject(oid).ShouldBeTrue("the LFS upload authenticated with the token");
        ctx.Runner.Ran(unreset ?? "push").ShouldBeTrue("fixture check: the command under test ran");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ShouldNeverHaveSeenTheToken(watch, "the publish repo");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ls-remote")]
    public async Task Resolving_the_launch_base_leaves_no_token_on_an_argv_or_in_the_operators_credential_store(string? unreset)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Runner.Unreset = unreset;

        var tip = await new RemoteTipResolver(ctx.Registry).ResolveTipShaAsync(new WorkspaceRequest
        {
            RepositoryUrl = ctx.Remote.Url, Token = GitPublishRemoteFixture.FakeToken, TokenUsername = "x-access-token", Ref = "main",
        }, refRequired: true, CancellationToken.None);

        tip.ShouldBe(ctx.Remote.BaseSha, "the probe authenticated with the token");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clone")]   // positive controls: the integration clone without the reset,
    [InlineData("push")]    //   or the push through its origin
    public async Task Integrating_leaves_no_token_on_an_argv_on_disk_or_in_the_operators_credential_store(string? unreset)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Runner.Unreset = unreset;
        ctx.Remote.ResponseDelay = SlowRemote;

        await using var watch = new TokenOnDiskWatch(GitPublishRemoteFixture.FakeToken, LocalGitWorkspaceProvider.WorkspacesRoot, "integrate-");
        var result = await ctx.IntegrateAsync(ctx.BranchName, ctx.Remote.BaseSha, ReadmePatch);
        await watch.StopAsync();

        result.Status.ShouldBe(IntegrationStatus.Clean, result.Reason);
        (await ctx.RemoteFileAsync(ctx.BranchName, "README.md")).ShouldBe("integrated\n", "the clone and the push authenticated with the token");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ShouldNeverHaveSeenTheToken(watch, "the integration clone");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clone")]      // positive controls: the clone without the reset,
    [InlineData("checkout")]   //   or the base checkout, whose LFS download asks the helpers through git-lfs
    public async Task Integrating_over_lfs_history_downloads_through_origin_and_stores_nothing(string? unreset)
    {
        // The integration clone's origin carries no credential, so the base checkout and the apply download their LFS objects
        // through it with the token in their environment too: git-lfs asks the credential helpers for it, and tells them when
        // it worked. Each runs as a tokened command, so only the scoped helper answers and no operator helper stores it.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync() || !await GitLfsAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        var lfs = await ctx.Remote.AddLfsHistoryAsync();
        ctx.InstallLfsFilters();
        ctx.Runner.Unreset = unreset;

        var result = await ctx.IntegrateAsync(ctx.BranchName, lfs.BaseSha, LfsPointerPatch(lfs.AtBase, lfs.Unreferenced));

        result.Status.ShouldBe(IntegrationStatus.Clean, result.Reason);
        (await ctx.RemoteFileAsync(ctx.BranchName, "big.bin")).ShouldBe(lfs.Unreferenced.Pointer, "the clone and the push authenticated with the token");
        ctx.Remote.DownloadedLfsOids.ShouldContain(lfs.AtBase.Oid, "fixture check: the base checkout downloaded its LFS object through origin");
        ctx.Remote.DownloadedLfsOids.ShouldContain(lfs.Unreferenced.Oid, "fixture check: the apply downloaded the patched LFS object through origin");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData("clone")]       // the workspace clone,
    [InlineData("push")]        //   the publish push,
    [InlineData("integrate")]   //   and the integration clone, each redirected by the remote
    public async Task A_redirect_to_another_authority_gets_no_credential(string operation)
    {
        // git follows the redirect of the ref advertisement and makes the new authority the remote's base, so its next
        // request goes there. The credential answers for the remote's own scheme and authority only: when the harvester asks,
        // no helper answers for it, and the operation fails rather than send the token anywhere.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Remote.StartHarvester();

        await Should.ThrowAsync<WorkspaceException>(() => ctx.RedirectedAsync(operation));

        ctx.Remote.HarvesterRequests.ShouldBeGreaterThan(1, "fixture check: git followed the redirect and asked the harvester for more than the advertisement");
        ctx.Remote.HarvestedAuthorizations.ShouldBeEmpty("a credential reached the authority the remote redirected to");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ctx.ShouldHoldTheToken(stored: false);
    }

    [Fact]
    public async Task The_harvester_collects_the_token_a_redirected_url_carries()
    {
        // Positive control for the redirect row: the same redirect, with the token in the URL as git was handed it before —
        // git sends it to the authority the remote redirected to, so the harvester's silence there is the scoped helper's doing.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Remote.StartHarvester();
        ctx.Remote.RedirectsToHarvester = true;

        await ctx.RawGitAsync(ctx.Root, "clone", ctx.TokenedUrl, Path.Combine(ctx.Root, "raw-clone"));

        ctx.Remote.HarvestedAuthorizations.ShouldContain(GitPublishRemoteFixture.TokenAuthorization, "git sends a token in the URL to the authority the remote redirected to");
    }

    [Theory]
    [InlineData("url")]      // the token in the URL's userinfo, which git writes as origin before the transfer starts
    [InlineData("header")]   //   or base64-encoded in a Basic Authorization header, which git writes as http.extraHeader
    public async Task The_disk_watch_sees_the_token_a_clone_writes_into_its_config(string carrier)
    {
        // Positive control for the watch, in both forms the token can take on disk.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        var clone = Path.Combine(ctx.WorkspacesRoot, "raw-clone");
        var args = carrier == "url" ? new[] { "clone", ctx.TokenedUrl, clone } : new[] { "clone", "--config", $"http.extraHeader=Authorization: {GitPublishRemoteFixture.TokenAuthorization}", ctx.Remote.Url, clone };

        await using var watch = ctx.WatchTheWorkspaces();
        (await ctx.RawGitAsync(ctx.Root, args)).Status.ShouldBe(SandboxStatus.Success);
        await watch.StopAsync();

        watch.Hits.ShouldContain(Path.Combine(clone, ".git", "config"), "the watch finds the token git wrote");
    }

    [Fact]
    public async Task Tokened_commands_reach_the_remote_through_a_proxy_whose_password_the_operators_helper_holds()
    {
        // The operator's http.proxy names only its user, so git asks the credential helpers for the proxy's password before
        // every tokened command. The reset empties the helper list for the tokened remote only: that lookup still finds the
        // operator's store, and the token still reaches no helper.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        await using var proxy = new AuthenticatingProxy();
        ctx.RouteThroughProxy(proxy);

        var tip = await new RemoteTipResolver(ctx.Registry).ResolveTipShaAsync(new WorkspaceRequest
        {
            RepositoryUrl = ctx.Remote.Url, Token = GitPublishRemoteFixture.FakeToken, TokenUsername = "x-access-token", Ref = "main",
        }, refRequired: true, CancellationToken.None);
        tip.ShouldBe(ctx.Remote.BaseSha, "the launch-base probe passed the proxy and authenticated to the remote");

        await using var handle = await ctx.ProvisionAsync(GitPublishRemoteFixture.FakeToken);
        handle.Repositories.Single().BaseSha.ShouldBe(ctx.PinnedSha, "the soft-ref probe, the clone and the pin's fetch passed the proxy");

        await ctx.AgentCommitsAsync(handle.Directory);
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldBe(await ctx.RemoteShaAsync(ctx.BranchName), "the push and its readback passed the proxy");

        var integrated = await ctx.IntegrateAsync("codespace/integration/" + Guid.NewGuid().ToString("N"), ctx.Remote.BaseSha, ReadmePatch);
        integrated.Status.ShouldBe(IntegrationStatus.Clean, integrated.Reason);

        proxy.RelayedRequests.ShouldBeGreaterThan(0, "fixture check: git reached the remote through the proxy, not around it");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ctx.ShouldHoldTheToken(stored: false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clone")]   // positive controls: the clone as it would run with trace2 on,
    [InlineData("push")]    //   or the publish push
    public async Task The_operators_trace2_targets_record_no_credential(string? unreset)
    {
        // git writes every command's argv, and each child's (git remote-http <url>), to the trace2 targets in system and
        // global config — read before any -c or environment config can reach them — and the value of each environment
        // variable their trace2.envVars names. A tokened argv names no credential, but trace2.envVars can name anything, so a
        // tokened command runs with trace2 off; untokened commands still trace.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.TraceToOperatorTargets(envVars: "CODESPACE_GIT_USERNAME,CODESPACE_GIT_PASSWORD");
        ctx.Runner.Unreset = unreset;

        await using var handle = await ctx.ProvisionAsync(GitPublishRemoteFixture.FakeToken);
        await ctx.AgentCommitsAsync(handle.Directory);
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        var trace = ctx.OperatorTrace();
        trace.ShouldContain("rev-parse", Case.Sensitive, "fixture check: the operator's trace2 targets are live, and untokened commands still trace");
        trace.ShouldNotContain("@127.0.0.1", Case.Sensitive, "no traced argv names the remote by a URL with userinfo");
        trace.Contains(GitPublishRemoteFixture.FakeToken, StringComparison.Ordinal).ShouldBe(unreset is not null, unreset is not null ? "positive control: with trace2 on, trace2.envVars records the token" : "the token reached the operator's trace2 targets");
    }

    [Fact]
    public async Task An_untokened_clone_still_authenticates_through_the_operators_helper()
    {
        // The reset is for tokened commands only: an operator may reach a private mirror through a helper of their own.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        var untokened = WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = ctx.Remote.Url });

        await Should.ThrowAsync<WorkspaceException>(() => ctx.Provider.PrepareAsync(untokened, CancellationToken.None), "fixture check: the remote refuses a clone that presents no credential");

        ctx.SeedTheOperatorsStore();
        await using var handle = await ctx.Provider.PrepareAsync(untokened, CancellationToken.None);

        File.ReadAllText(Path.Combine(handle.Directory, "README.md")).ShouldBe("base, revised\n", "the clone authenticated through the operator's store helper");
        ctx.Runner.Specs.ShouldAllBe(s => TokenedGitControls.RunsUntokened(s), "an untokened command keeps the operator's helpers and trace2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clone")]   // positive control: the grader's clone without the reset
    public async Task Grading_leaves_no_token_on_an_argv_on_disk_or_in_the_operators_credential_store(string? unreset)
    {
        // The acceptance grader clones the base itself — a base sha is no ref the provider's clone takes — then checks it out,
        // applies the candidate's patch and runs the check in that clone. Every command that reaches the remote carries the
        // token in its environment alone.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Runner.Unreset = unreset;
        ctx.Remote.ResponseDelay = SlowRemote;

        await using var watch = new TokenOnDiskWatch(GitPublishRemoteFixture.FakeToken, LocalGitWorkspaceProvider.WorkspacesRoot, "grade-");
        var grade = await ctx.GradePatchAsync(ctx.Remote.BaseSha, ReadmePatch, "test \"$(cat README.md)\" = integrated");
        await watch.StopAsync();

        grade.Passed.ShouldBeTrue(grade.Detail);
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ShouldNeverHaveSeenTheToken(watch, "the grading clone");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clone")]      // positive controls: the clone without the reset,
    [InlineData("checkout")]   //   the base checkout, whose LFS download asks the helpers through git-lfs,
    [InlineData("apply")]      //   or the apply, which downloads the object the patch points at
    public async Task Grading_at_an_lfs_base_downloads_through_origin_and_stores_nothing(string? unreset)
    {
        // The grading clone's origin carries no credential, so the base checkout and the apply download their LFS objects
        // through it with the token in their environment: git-lfs asks the credential helpers for it, and tells them when it
        // worked. Untokened, the checkout would find no credential, and the grade would fail as an unknown base.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync() || !await GitLfsAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        var lfs = await ctx.Remote.AddLfsHistoryAsync();
        ctx.InstallLfsFilters();
        ctx.Runner.Unreset = unreset;

        var grade = await ctx.GradePatchAsync(lfs.BaseSha, LfsPointerPatch(lfs.AtBase, lfs.Unreferenced), "test \"$(cat big.bin)\" = \"lfs payload v3\"");

        grade.Passed.ShouldBeTrue(grade.Detail);
        ctx.Remote.DownloadedLfsOids.ShouldContain(lfs.AtBase.Oid, "fixture check: the base checkout downloaded its LFS object through origin");
        ctx.Remote.DownloadedLfsOids.ShouldContain(lfs.Unreferenced.Oid, "fixture check: the apply downloaded the patched LFS object through origin");
        ctx.Runner.Ran(unreset ?? "apply").ShouldBeTrue("fixture check: the command under test ran");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData("publish", true)]
    [InlineData("clone", true)]
    [InlineData("publish", false)]   // positive controls: with the helper ignoring erase, git-lfs retries the refused token
    [InlineData("clone", false)]     //   until the command is killed
    public async Task A_refused_token_fails_an_lfs_command_at_once(string operation, bool recordsRefusal)
    {
        // A token the remote refuses — one past its lifetime, a revoked one — makes git-lfs tell the helpers to erase it and
        // ask them again, with no limit; git itself stops after one refusal. The helper records the refusal and answers no
        // more, so the LFS upload of a publish, or the LFS download of a clone's checkout, fails at once with the reason —
        // not after its whole timeout, tens of failed logins a second against the provider.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync() || !await GitLfsAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync(authenticateReads: false);
        ctx.Runner.HelperIgnoresErase = !recordsRefusal;
        ctx.Runner.TimeoutCapSeconds = recordsRefusal ? null : 10;

        var elapsed = Stopwatch.StartNew();
        var failure = await Should.ThrowAsync<WorkspaceException>(() => ctx.WithARefusedTokenAsync(operation));
        elapsed.Stop();

        ctx.Runner.Ran("lfs").ShouldBe(operation == "publish", $"fixture check: the {operation} itself failed");

        if (!recordsRefusal)
        {
            // How long git-lfs keeps asking differs by version (3.7.1 retries until the command is killed; the one CI's
            // runner ships gave up after a few requests); each asks the helpers again after erasing, so count the re-offers.
            ctx.Remote.LfsBatchRefusedCredentials.ShouldBeGreaterThan(1, "positive control: a helper that answers every get offers the refused token again after git-lfs erased it");
            return;
        }

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), $"the refused token was retried instead of failing the {operation}");
        ctx.Remote.LfsBatchRefusedCredentials.ShouldBe(1, "the refused token was offered once and never again");
        ctx.Remote.LfsBatchRequests.ShouldBeInRange(1, 10, "fixture check: git-lfs asked the remote, and stopped once it refused");
        failure.Message.ShouldContain("the remote refused this credential", Case.Sensitive, "the failure says why");
        ctx.ShouldKeepTheTokenOffEveryArgv(OperatorHostContext.RefusedToken);
    }

    [Fact]
    public async Task Operator_url_rewrites_never_move_a_tokened_remote()
    {
        // The operator's global config sends the remote's authority to a local mirror for fetches and to a sink for pushes —
        // the shape of url."git@host:".insteadOf=https://host/, or a rule carrying an operator's own token. A tokened command's
        // credential is bound to its remote, so the command maps that URL to itself, the longest prefix any rule can match:
        // the launch-base probe, the clone and its pin, the publish (LFS included) and the integration all reach the remote.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        var lfs = await GitLfsAvailableAsync();
        await using var ctx = await OperatorHostContext.StartAsync();
        using var sink = new LoopbackSink();
        var mirrorSha = await ctx.RewriteTheRemoteAsync(sink);

        (await ctx.ResolveLaunchBaseAsync()).ShouldBe(ctx.Remote.BaseSha, "the launch-base probe reached the remote, not the mirror");

        await using var handle = await ctx.ProvisionAsync(GitPublishRemoteFixture.FakeToken);
        handle.Repositories.Single().BaseSha.ShouldBe(ctx.PinnedSha, "the soft-ref probe, the clone and the pin's fetch reached the remote");

        var oid = lfs ? await ctx.AgentCommitsLfsFileAsync(handle.Directory) : null;
        await ctx.AgentCommitsAsync(handle.Directory);
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldBe(await ctx.RemoteShaAsync(ctx.BranchName), "the push and its readback reached the remote");
        if (oid is not null) ctx.Remote.HasLfsObject(oid).ShouldBeTrue("the LFS upload reached the remote");

        var integrated = await ctx.IntegrateAsync("codespace/integration/" + Guid.NewGuid().ToString("N"), ctx.Remote.BaseSha, ReadmePatch);
        integrated.Status.ShouldBe(IntegrationStatus.Clean, integrated.Reason);

        mirrorSha.ShouldNotBe(ctx.Remote.BaseSha, "fixture check: the mirror's history is not the remote's");
        sink.Connections.ShouldBe(0, "a push was rewritten to the operator's sink");
        ctx.ShouldKeepTheTokenOffEveryArgv();
        ctx.ShouldHoldTheToken(stored: false);
    }

    [Fact]
    public async Task The_operators_url_rewrites_move_a_tokened_command_without_the_pin()
    {
        // Positive control for the rewrite row: the same rules move the same tokened commands once the pin is taken from
        // their environment — the launch-base probe to the mirror, the publish push to the sink — as they moved every
        // tokened command once the token left the URL, and as they move any untokened one.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        using var sink = new LoopbackSink();
        var mirrorSha = await ctx.RewriteTheRemoteAsync(sink);

        ctx.Runner.Unpinned = "ls-remote";
        (await ctx.ResolveLaunchBaseAsync()).ShouldBe(mirrorSha, "positive control: without the pin the probe goes to the mirror");

        ctx.Runner.Unpinned = null;
        await using var handle = await ctx.ProvisionAsync(GitPublishRemoteFixture.FakeToken);
        await ctx.AgentCommitsAsync(handle.Directory);

        ctx.Runner.Unpinned = "push";
        await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        sink.Connections.ShouldBeGreaterThan(0, "positive control: without the pin the push goes to the sink");
    }

    private static void ShouldNeverHaveSeenTheToken(TokenOnDiskWatch watch, string what)
    {
        watch.Scans.ShouldBeGreaterThan(10, $"fixture check: the watch was scanning while {what} was written");
        watch.Hits.ShouldBeEmpty($"the token reached the disk under {what} at some point while it ran");
    }

    private static Task<bool> GitAvailableAsync() => ToolAvailableAsync(new[] { "--version" });

    private static Task<bool> GitLfsAvailableAsync() => ToolAvailableAsync(new[] { "lfs", "version" });

    /// <summary>An agent's patch repointing <c>big.bin</c> from one LFS object to another, as a capture records it: the pointer text.</summary>
    private static string LfsPointerPatch(LfsObject from, LfsObject to) =>
        $"diff --git a/big.bin b/big.bin\n--- a/big.bin\n+++ b/big.bin\n@@ -1,3 +1,3 @@\n version https://git-lfs.github.com/spec/v1\n-oid sha256:{from.Oid}\n-size {from.Size}\n+oid sha256:{to.Oid}\n+size {to.Size}\n";

    private static async Task<bool> ToolAvailableAsync(IReadOnlyList<string> args)
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>The git subcommand, past any leading <c>-c key=value</c> and <c>-C dir</c>.</summary>
    private static string Subcommand(IReadOnlyList<string> args)
    {
        var i = 0;
        while (i + 1 < args.Count && args[i] is "-c" or "-C") i += 2;
        return i < args.Count ? args[i] : "";
    }

    /// <summary>The remote, a scratch operator host (HOME, global config with the two store helpers, system config off) and the production classes running on it.</summary>
    private sealed class OperatorHostContext : IAsyncDisposable
    {
        private readonly Dictionary<string, string> _environment;

        private OperatorHostContext(bool authenticateReads)
        {
            Remote = new GitPublishRemoteFixture { AuthenticateReads = authenticateReads };
            Directory.CreateDirectory(Home);
            _environment = new Dictionary<string, string> { ["HOME"] = Home, ["GIT_CONFIG_GLOBAL"] = GlobalConfig, ["GIT_CONFIG_NOSYSTEM"] = "1" };
            Runner = new OperatorConfigRunner(_environment);
            Registry = new SandboxRunnerRegistry(new ISandboxRunner[] { Runner });
            Provider = new LocalGitWorkspaceProvider(Registry, NullLogger<LocalGitWorkspaceProvider>.Instance, WorkspacesRoot);
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cs-credstore-" + Guid.NewGuid().ToString("N"));
        public GitPublishRemoteFixture Remote { get; }
        public OperatorConfigRunner Runner { get; }
        public SandboxRunnerRegistry Registry { get; }
        public LocalGitWorkspaceProvider Provider { get; }
        public string BranchName { get; } = "codespace/agent/" + Guid.NewGuid().ToString("N");

        /// <summary>A token the remote refuses: not <see cref="GitPublishRemoteFixture.FakeToken"/>.</summary>
        public const string RefusedToken = "refused-token-0123456789";

        /// <summary>The provider's own workspaces root: its clones and its publish repos.</summary>
        public string WorkspacesRoot => Path.Combine(Root, "workspaces");

        /// <summary>The remote's URL with the fake token in its userinfo — the shape git was handed before the token left the URL.</summary>
        public string TokenedUrl => Remote.Url.Replace("http://", $"http://x-access-token:{GitPublishRemoteFixture.FakeToken}@", StringComparison.Ordinal);

        /// <summary>The base's parent: absent from a depth-1 clone of the tip, so pinning it walks the fetch rungs through origin.</summary>
        public string PinnedSha { get; private set; } = "";

        private string Home => Path.Combine(Root, "home");
        private string GlobalConfig => Path.Combine(Home, ".gitconfig");
        private string GlobalStore => Path.Combine(Root, "store-global");
        private string ScopedStore => Path.Combine(Root, "store-scoped");

        /// <summary>The scratch host and its remote, which demands the token for reads too unless <paramref name="authenticateReads"/> is false.</summary>
        public static async Task<OperatorHostContext> StartAsync(bool authenticateReads = true)
        {
            var ctx = new OperatorHostContext(authenticateReads);

            try
            {
                await ctx.Remote.StartAsync();
                ctx.PinnedSha = (await GitPublishRemoteFixture.GitAsync(ctx.Remote.Root, new[] { "--git-dir", ctx.Remote.Remote, "rev-parse", "main~1" })).Trim();
                ctx.WriteOperatorConfig();
                return ctx;
            }
            catch
            {
                await ctx.DisposeAsync();
                throw;
            }
        }

        /// <summary>A global store helper, and a second store scoped to the remote's URL — the reset must silence both.</summary>
        private void WriteOperatorConfig()
        {
            var scope = new Uri(Remote.Url).GetLeftPart(UriPartial.Authority);

            File.WriteAllText(GlobalConfig, $"[credential]\n\thelper = store --file={GlobalStore}\n[credential \"{scope}\"]\n\thelper = store --file={ScopedStore}\n");
        }

        /// <summary>
        /// Send every git request through <paramref name="proxy"/>, which global config names by its user alone and whose
        /// password the operator's store holds — a proxy behind a credential helper. The child sees an empty NO_PROXY, so
        /// even loopback goes through it.
        /// </summary>
        public void RouteThroughProxy(AuthenticatingProxy proxy)
        {
            File.AppendAllText(GlobalConfig, $"[http]\n\tproxy = http://{AuthenticatingProxy.User}@127.0.0.1:{proxy.Port}\n");
            File.WriteAllText(GlobalStore, $"http://{AuthenticatingProxy.User}:{AuthenticatingProxy.Password}@127.0.0.1:{proxy.Port}\n");
            _environment["NO_PROXY"] = "";
            _environment["no_proxy"] = "";
        }

        /// <summary>Point the operator's trace2 targets — normal, event and perf — at scratch files, recording the values of <paramref name="envVars"/> too.</summary>
        public void TraceToOperatorTargets(string envVars) =>
            File.AppendAllText(GlobalConfig, $"[trace2]\n\tnormalTarget = {TraceFile("normal")}\n\teventTarget = {TraceFile("event")}\n\tperfTarget = {TraceFile("perf")}\n\tenvVars = {envVars}\n");

        /// <summary>Everything the operator's trace2 targets recorded.</summary>
        public string OperatorTrace() => string.Concat(new[] { "normal", "event", "perf" }.Select(TraceFile).Where(File.Exists).Select(File.ReadAllText));

        private string TraceFile(string target) => Path.Combine(Root, "trace2-" + target);

        /// <summary>The LFS filters <c>git lfs install</c> puts in global config, so a checkout smudges LFS pointers into their objects.</summary>
        public void InstallLfsFilters() =>
            File.AppendAllText(GlobalConfig, "[filter \"lfs\"]\n\tclean = git-lfs clean -- %f\n\tsmudge = git-lfs smudge -- %f\n\tprocess = git-lfs filter-process\n\trequired = true\n");

        /// <summary>A watch for the token over the provider's workspaces root — every clone and publish repo it makes.</summary>
        public TokenOnDiskWatch WatchTheWorkspaces()
        {
            Directory.CreateDirectory(WorkspacesRoot);
            return new TokenOnDiskWatch(GitPublishRemoteFixture.FakeToken, WorkspacesRoot);
        }

        /// <summary>Integrate one contribution — <paramref name="patch"/> over <paramref name="baseSha"/> — into <paramref name="branch"/> with the real integrator and the fake token.</summary>
        public Task<IntegrationResult> IntegrateAsync(string branch, string baseSha, string patch) =>
            new LocalGitBranchIntegrator(Registry, new InlineOffloader(), NullLogger<LocalGitBranchIntegrator>.Instance).IntegrateAsync(new IntegrationRequest
            {
                TeamId = Guid.NewGuid(), RepositoryUrl = Remote.Url, BaseSha = baseSha, Token = GitPublishRemoteFixture.FakeToken, TokenUsername = "x-access-token", IntegrationBranch = branch,
                Contributions = new[] { new BranchContribution { Label = "agent", BaseSha = baseSha, Patch = patch } },
            }, CancellationToken.None);

        /// <summary><paramref name="operation"/> against the remote once it redirects to the harvester: a workspace clone, a publish push from a workspace cloned before the redirect, or an integration clone.</summary>
        public async Task RedirectedAsync(string operation)
        {
            if (operation == "push")
            {
                await using var cloned = await ProvisionAsync(GitPublishRemoteFixture.FakeToken);
                await AgentCommitsAsync(cloned.Directory);
                Remote.RedirectsToHarvester = true;
                await ((IWorkspacePushHandle)cloned).PushChangesAsync(BranchName, CancellationToken.None);
                return;
            }

            Remote.RedirectsToHarvester = true;

            if (operation == "integrate")
            {
                await IntegrateAsync(BranchName, Remote.BaseSha, ReadmePatch);
                return;
            }

            await using var handle = await Provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = Remote.Url, Token = GitPublishRemoteFixture.FakeToken, TokenUsername = "x-access-token" }), CancellationToken.None);
        }

        /// <summary>The real grader, its base clone resolved to the remote with the fake token, grading <paramref name="patch"/> over <paramref name="baseSha"/> with <paramref name="check"/> as the acceptance command.</summary>
        public Task<BenchmarkGrade> GradePatchAsync(string baseSha, string patch, string check)
        {
            var clone = new WorkspaceRequest { RepositoryUrl = Remote.Url, Token = GitPublishRemoteFixture.FakeToken, TokenUsername = "x-access-token" };
            var grader = new SupervisorAcceptanceGrader(new FixedResolver(clone), new WorkspaceProviderRegistry(new IWorkspaceProvider[] { Provider }), Registry, new BenchmarkGraderRegistry(new IBenchmarkGrader[] { new TestsPassGrader() }), new InlineOffloader(), new DiscardingArtifactStore(), null!, NullLogger<SupervisorAcceptanceGrader>.Instance);

            return grader.GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), baseSha, patch, null, new SupervisorAcceptanceSpec { Command = new[] { "/bin/sh", "-c", check } }, 60, CancellationToken.None);
        }

        /// <summary>The launch-base probe for main, with the fake token.</summary>
        public Task<string?> ResolveLaunchBaseAsync() =>
            new RemoteTipResolver(Registry).ResolveTipShaAsync(new WorkspaceRequest { RepositoryUrl = Remote.Url, Token = GitPublishRemoteFixture.FakeToken, TokenUsername = "x-access-token", Ref = "main" }, refRequired: true, CancellationToken.None);

        /// <summary>
        /// <paramref name="operation"/> with a token the remote refuses: a publish whose LFS upload the remote refuses (the clone
        /// before it reads anonymously), or a clone whose checkout downloads an LFS object the remote refuses.
        /// </summary>
        public async Task WithARefusedTokenAsync(string operation)
        {
            if (operation == "clone")
            {
                await Remote.AddLfsHistoryAsync();
                InstallLfsFilters();
            }

            await using var handle = await Provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = Remote.Url, Token = RefusedToken, TokenUsername = "x-access-token" }), CancellationToken.None);

            await AgentCommitsLfsFileAsync(handle.Directory);
            await ((IWorkspacePushHandle)handle).PushChangesAsync(BranchName, CancellationToken.None);
        }

        /// <summary>
        /// Rewrite the remote's authority in the operator's global config — to a local mirror with a history of its own for
        /// fetches, to <paramref name="sink"/> for pushes — and return the mirror's tip.
        /// </summary>
        public async Task<string> RewriteTheRemoteAsync(LoopbackSink sink)
        {
            var mirrors = Path.Combine(Root, "mirrors");
            var seed = Path.Combine(Root, "mirror-seed");
            Directory.CreateDirectory(seed);

            await GitAsync(Root, "init", "-q", "--bare", "-b", "main", Path.Combine(mirrors, "remote.git"));
            await GitAsync(seed, "init", "-q", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "from the mirror\n");
            await GitAsync(seed, "add", "-A");
            await GitAsync(seed, "-c", "user.name=Mirror", "-c", "user.email=mirror@example.test", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "mirror");
            await GitAsync(seed, "push", "-q", Path.Combine(mirrors, "remote.git"), "main");

            var authority = new Uri(Remote.Url).GetLeftPart(UriPartial.Authority);
            File.AppendAllText(GlobalConfig, $"[url \"{mirrors}/\"]\n\tinsteadOf = {authority}/\n[url \"http://127.0.0.1:{sink.Port}/\"]\n\tpushInsteadOf = {authority}/\n");

            return (await GitAsync(seed, "rev-parse", "HEAD")).Trim();
        }

        /// <summary>The operator's own credential for the remote, as <c>git credential-store</c> keeps it.</summary>
        public void SeedTheOperatorsStore() =>
            File.WriteAllText(GlobalStore, $"http://x-access-token:{GitPublishRemoteFixture.FakeToken}@{new Uri(Remote.Url).Authority}\n");

        /// <summary>A soft ref with a fallback (so the probe runs), a depth-1 clone, and a pin to the base's parent (so the fetch rungs run).</summary>
        public Task<IWorkspaceHandle> ProvisionAsync(string token) =>
            Provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest
            {
                RepositoryUrl = Remote.Url, Token = token, TokenUsername = "x-access-token", Ref = "main", DefaultRef = "trunk", PinnedSha = PinnedSha, Depth = 1,
            }), CancellationToken.None);

        public void ShouldHoldTheToken(bool stored)
        {
            foreach (var store in new[] { GlobalStore, ScopedStore })
            {
                var holds = File.Exists(store) && File.ReadAllText(store).Contains(GitPublishRemoteFixture.FakeToken, StringComparison.Ordinal);

                holds.ShouldBe(stored, stored ? $"positive control: without the reset the operator's helper writes the token to {store}" : $"the token reached the operator's credential store {store}");
            }
        }

        /// <summary>No argv the production code handed the runner carries the token or names a URL with userinfo — the clone, the probe, the push and the LFS upload included.</summary>
        public void ShouldKeepTheTokenOffEveryArgv(string token = GitPublishRemoteFixture.FakeToken)
        {
            Runner.Specs.ShouldNotBeEmpty("fixture check: the production code ran git through the runner");
            Runner.Specs.Where(s => TokenedGitControls.ArgvCarriesACredential(s, token)).Select(s => string.Join(' ', s.Args)).ShouldBeEmpty("an argv carried the token, or a URL's userinfo");
        }

        public async Task AgentCommitsAsync(string cloneDir)
        {
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "agent.txt"), "the agent's work\n");
            await GitAsync(cloneDir, "add", "-A");
            await GitAsync(cloneDir, "-c", "user.name=Agent", "-c", "user.email=agent@example.test", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "agent work");
        }

        /// <summary>The agent tracks and commits a real LFS file (filters configured locally, no hooks); returns its oid.</summary>
        public async Task<string> AgentCommitsLfsFileAsync(string cloneDir)
        {
            await GitAsync(cloneDir, "config", "filter.lfs.clean", "git-lfs clean -- %f");
            await GitAsync(cloneDir, "config", "filter.lfs.smudge", "git-lfs smudge -- %f");
            await GitAsync(cloneDir, "config", "filter.lfs.process", "git-lfs filter-process");
            await GitAsync(cloneDir, "config", "filter.lfs.required", "true");
            await GitAsync(cloneDir, "lfs", "track", "*.bin");
            await File.WriteAllBytesAsync(Path.Combine(cloneDir, "big.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096));
            await GitAsync(cloneDir, "add", "-A");
            await GitAsync(cloneDir, "-c", "user.name=Agent", "-c", "user.email=agent@example.test", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "agent lfs file");

            var pointer = await GitAsync(cloneDir, "show", "HEAD:big.bin");
            return pointer.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("oid sha256:", StringComparison.Ordinal))["oid sha256:".Length..];
        }

        public async Task<string> RemoteShaAsync(string branch) => (await GitPublishRemoteFixture.GitAsync(Remote.Root, new[] { "--git-dir", Remote.Remote, "rev-parse", $"refs/heads/{branch}" })).Trim();

        public Task<string> RemoteFileAsync(string branch, string file) => GitPublishRemoteFixture.GitAsync(Remote.Root, new[] { "--git-dir", Remote.Remote, "show", $"refs/heads/{branch}:{file}" });

        /// <summary>Test-side git on the same scratch host, hooks off, its result returned as it is; never recorded by the runner, never production code.</summary>
        public Task<SandboxResult> RawGitAsync(string workdir, params string[] args) =>
            new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "-c", "core.hooksPath=/dev/null" }.Concat(args).ToList(), WorkingDirectory = workdir, Environment = _environment, TimeoutSeconds = 120, AllowNetwork = true }, CancellationToken.None);

        /// <summary>Test-side git (the agent's own commits) on the same scratch host, hooks off; never recorded, never a positive control.</summary>
        private async Task<string> GitAsync(string workdir, params string[] args)
        {
            var result = await RawGitAsync(workdir, args);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public async ValueTask DisposeAsync()
        {
            await Remote.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The real local runner on the scratch operator host, recording every spec the production code submits. The positive
    /// controls: with <see cref="Unreset"/> set, that one subcommand runs without the credential-helper reset and with trace2
    /// on; with <see cref="Unpinned"/> set, that one runs without the pin that maps its remote to itself; with
    /// <see cref="HelperIgnoresErase"/>, every tokened command's helper answers every get, and <see cref="TimeoutCapSeconds"/>
    /// bounds how long each command may then retry.
    /// </summary>
    private sealed class OperatorConfigRunner(IReadOnlyDictionary<string, string> environment) : ISandboxRunner
    {
        private readonly LocalProcessRunner _inner = new();

        public string Kind => "local";
        public string? Unreset { get; set; }
        public string? Unpinned { get; set; }
        public bool HelperIgnoresErase { get; set; }
        public int? TimeoutCapSeconds { get; set; }
        public List<SandboxSpec> Specs { get; } = new();

        public bool Ran(string subcommand) => Specs.Any(s => Subcommand(s.Args) == subcommand);

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);

            var env = Controlled(Subcommand(spec.Args), spec.Environment);
            foreach (var (key, value) in environment) env[key] = value;

            var run = spec with { Environment = env };
            if (TimeoutCapSeconds is { } cap) run = run with { TimeoutSeconds = Math.Min(run.TimeoutSeconds ?? cap, cap) };

            return _inner.RunAsync(run, cancellationToken);
        }

        /// <summary><paramref name="env"/> with whatever part of it the control set for <paramref name="subcommand"/> takes away.</summary>
        private Dictionary<string, string> Controlled(string subcommand, IReadOnlyDictionary<string, string> env)
        {
            var controlled = subcommand == Unreset ? TokenedGitControls.WithoutTheReset(env) : new Dictionary<string, string>(env);
            if (subcommand == Unpinned) controlled = TokenedGitControls.WithoutThePin(controlled);

            return HelperIgnoresErase ? TokenedGitControls.WithTheHelperIgnoringErase(controlled) : controlled;
        }
    }

    private sealed class FixedResolver(WorkspaceRequest request) : IAgentWorkspaceResolver
    {
        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null) => Task.FromResult<WorkspaceRequest?>(request);
    }

    /// <summary>Takes the grade's evidence and keeps nothing.</summary>
    private sealed class DiscardingArtifactStore : IArtifactStore
    {
        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class InlineOffloader : IArtifactOffloader
    {
        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken) => Task.FromResult(inline ?? "");

        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
