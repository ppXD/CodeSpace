using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Integrators;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): the REAL <see cref="LocalGitWorkspaceProvider"/>, <see cref="RemoteTipResolver"/> and
/// <see cref="LocalGitBranchIntegrator"/> on the real <see cref="LocalProcessRunner"/>, with real <c>git</c> and
/// <c>git-lfs</c>, against a loopback smart-HTTP remote (<see cref="GitPublishRemoteFixture"/>) that demands a FAKE token
/// for every request, read or write. The operator's host is simulated by a scratch <c>HOME</c> whose global config
/// (<c>GIT_CONFIG_GLOBAL</c>, with system config off) sets <c>credential.helper=store --file=&lt;scratch&gt;</c> plus a
/// second store scoped to the remote's URL — the setup under which every tokened run used to leave its token on disk.
///
/// <para>Each tokened operation must authenticate with the token in its URL and leave both store files without it. The
/// positive control runs the same production code with ONE named git subcommand as it ran before tokened commands were
/// marked — no credential-helper reset, trace2 on: that store then holds the token, so its absence in the real run is the
/// reset's doing, not a helper that never ran. An untokened clone must still authenticate through the operator's helper,
/// and a tokened one must still reach the remote through a proxy whose password that helper holds: the reset covers the
/// tokened remote only. The operator's trace2 targets must record no tokened command, and the LFS downloads an
/// integration makes through its tokened origin — a checkout and an apply that run as written — must store nothing.</para>
///
/// <para>Each test owns its remote, proxy and temp tree and removes them on every path; it skips on Windows or without
/// git, and the LFS rows without git-lfs. Nothing reads or writes the real global config, system config or keychain.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class TokenedGitCredentialHelperFlowTests
{
    private const string ReadmePatch = "diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-base, revised\n+integrated\n";

    [Theory]
    [InlineData(null)]          // every tokened command carries the reset
    [InlineData("ls-remote")]   // positive controls: the soft-ref probe without it,
    [InlineData("clone")]       //   the clone,
    [InlineData("fetch")]       //   or the pin's fetch rungs through the still-tokened origin
    public async Task Provisioning_a_tokened_workspace_leaves_no_token_in_the_operators_credential_store(string? unreset)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Runner.Unreset = unreset;

        await using var handle = await ctx.ProvisionAsync(GitPublishRemoteFixture.FakeToken);

        handle.Repositories.Single().BaseSha.ShouldBe(ctx.PinnedSha, "the probe, the clone and the pin's fetch all authenticated with the URL's token");
        ctx.Runner.Ran(unreset ?? "fetch").ShouldBeTrue("fixture check: the command under test ran");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lfs")]         // positive controls: the LFS upload without the reset,
    [InlineData("push")]        //   the push,
    [InlineData("ls-remote")]   //   or the readback
    public async Task Publishing_leaves_no_token_in_the_operators_credential_store(string? unreset)
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

        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        ctx.Remote.AuthenticatedPushRequests.ShouldBeGreaterThan(0, "the push authenticated with the URL's token");
        ((IWorkspacePushHandle)handle).LastPushedCommitSha().ShouldBe(await ctx.RemoteShaAsync(ctx.BranchName), "the readback authenticated and confirmed the pushed tip");
        if (oid is not null) ctx.Remote.HasLfsObject(oid).ShouldBeTrue("the LFS upload authenticated with the URL's token");
        ctx.Runner.Ran(unreset ?? "push").ShouldBeTrue("fixture check: the command under test ran");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ls-remote")]
    public async Task Resolving_the_launch_base_leaves_no_token_in_the_operators_credential_store(string? unreset)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Runner.Unreset = unreset;

        var tip = await new RemoteTipResolver(ctx.Registry).ResolveTipShaAsync(new WorkspaceRequest
        {
            RepositoryUrl = ctx.Remote.Url, Token = GitPublishRemoteFixture.FakeToken, TokenUsername = "x-access-token", Ref = "main",
        }, refRequired: true, CancellationToken.None);

        tip.ShouldBe(ctx.Remote.BaseSha, "the probe authenticated with the URL's token");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clone")]   // positive controls: the integration clone without the reset,
    [InlineData("push")]    //   or the push through its tokened origin
    public async Task Integrating_leaves_no_token_in_the_operators_credential_store(string? unreset)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.Runner.Unreset = unreset;

        var result = await ctx.IntegrateAsync(ctx.BranchName, ctx.Remote.BaseSha, ReadmePatch);

        result.Status.ShouldBe(IntegrationStatus.Clean, result.Reason);
        (await ctx.RemoteFileAsync(ctx.BranchName, "README.md")).ShouldBe("integrated\n", "the clone and the push authenticated with the URL's token");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clone")]   // positive control: the clone without the reset — the operator's store is live here
    public async Task Integrating_over_lfs_history_downloads_through_the_tokened_origin_and_stores_nothing(string? unreset)
    {
        // The integration clone keeps its tokened origin, and the base checkout and the apply download LFS objects through
        // it. git-lfs authenticates those downloads from the origin URL and neither asks nor tells a credential helper, so
        // they run as written; only git's own transport — the clone and the push — carries the reset.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync() || !await GitLfsAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        var lfs = await ctx.Remote.AddLfsHistoryAsync();
        ctx.InstallLfsFilters();
        ctx.Runner.Unreset = unreset;

        var result = await ctx.IntegrateAsync(ctx.BranchName, lfs.BaseSha, LfsPointerPatch(lfs.AtBase, lfs.Unreferenced));

        result.Status.ShouldBe(IntegrationStatus.Clean, result.Reason);
        (await ctx.RemoteFileAsync(ctx.BranchName, "big.bin")).ShouldBe(lfs.Unreferenced.Pointer, "the clone and the push authenticated with the URL's token");
        ctx.Remote.DownloadedLfsOids.ShouldContain(lfs.AtBase.Oid, "fixture check: the base checkout downloaded its LFS object through the tokened origin");
        ctx.Remote.DownloadedLfsOids.ShouldContain(lfs.Unreferenced.Oid, "fixture check: the apply downloaded the patched LFS object through the tokened origin");
        ctx.ShouldHoldTheToken(stored: unreset is not null);
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
        ctx.ShouldHoldTheToken(stored: false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clone")]   // positive controls: the clone as it ran before tokened commands were marked,
    [InlineData("push")]    //   or the publish push
    public async Task The_operators_trace2_targets_record_no_tokened_command(string? unreset)
    {
        // git writes every command's argv, and each child's (git remote-http <url>), to the trace2 targets in system and
        // global config — read before any -c can reach them — and git 2.33 writes the URL's password verbatim. A tokened
        // command runs with trace2 off, so the operator's targets never see a tokened URL; untokened commands still trace.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await OperatorHostContext.StartAsync();
        ctx.TraceToOperatorTargets();
        ctx.Runner.Unreset = unreset;

        await using var handle = await ctx.ProvisionAsync(GitPublishRemoteFixture.FakeToken);
        await ctx.AgentCommitsAsync(handle.Directory);
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        var trace = ctx.OperatorTrace();
        trace.ShouldContain("rev-parse", Case.Sensitive, "fixture check: the operator's trace2 targets are live, and untokened commands still trace");
        trace.Contains("x-access-token:", StringComparison.Ordinal).ShouldBe(unreset is not null, "a tokened URL (redacted or not, it keeps its username) in the operator's trace2 targets");
        if (unreset is null) trace.ShouldNotContain(GitPublishRemoteFixture.FakeToken);
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
        ctx.Runner.Specs.ShouldAllBe(s => !s.Args.Any(a => IsHelperReset(a)) && !s.Environment.Keys.Any(k => TraceOff.Contains(k)), "an untokened command keeps the operator's helpers and trace2");
    }

    private static Task<bool> GitAvailableAsync() => ToolAvailableAsync(new[] { "--version" });

    private static Task<bool> GitLfsAvailableAsync() => ToolAvailableAsync(new[] { "lfs", "version" });

    /// <summary>The environment a tokened command runs with, which turns off every trace2 target.</summary>
    private static readonly string[] TraceOff = { "GIT_TRACE2", "GIT_TRACE2_EVENT", "GIT_TRACE2_PERF" };

    /// <summary>A config that empties the credential helper list — <c>credential.helper=</c> or one scoped to a URL.</summary>
    private static bool IsHelperReset(string arg) => arg.StartsWith("credential.", StringComparison.Ordinal) && arg.EndsWith(".helper=", StringComparison.Ordinal);

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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-credstore-" + Guid.NewGuid().ToString("N"));

        private readonly Dictionary<string, string> _environment;

        private OperatorHostContext()
        {
            Directory.CreateDirectory(Home);
            _environment = new Dictionary<string, string> { ["HOME"] = Home, ["GIT_CONFIG_GLOBAL"] = GlobalConfig, ["GIT_CONFIG_NOSYSTEM"] = "1" };
            Runner = new OperatorConfigRunner(_environment);
            Registry = new SandboxRunnerRegistry(new ISandboxRunner[] { Runner });
            Provider = new LocalGitWorkspaceProvider(Registry, NullLogger<LocalGitWorkspaceProvider>.Instance, Path.Combine(_root, "workspaces"));
        }

        public GitPublishRemoteFixture Remote { get; } = new() { AuthenticateReads = true };
        public OperatorConfigRunner Runner { get; }
        public SandboxRunnerRegistry Registry { get; }
        public LocalGitWorkspaceProvider Provider { get; }
        public string BranchName { get; } = "codespace/agent/" + Guid.NewGuid().ToString("N");

        /// <summary>The base's parent: absent from a depth-1 clone of the tip, so pinning it walks the fetch rungs through origin.</summary>
        public string PinnedSha { get; private set; } = "";

        private string Home => Path.Combine(_root, "home");
        private string GlobalConfig => Path.Combine(Home, ".gitconfig");
        private string GlobalStore => Path.Combine(_root, "store-global");
        private string ScopedStore => Path.Combine(_root, "store-scoped");

        public static async Task<OperatorHostContext> StartAsync()
        {
            var ctx = new OperatorHostContext();

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

        /// <summary>Point the operator's trace2 targets — normal, event and perf — at scratch files.</summary>
        public void TraceToOperatorTargets() =>
            File.AppendAllText(GlobalConfig, $"[trace2]\n\tnormalTarget = {TraceFile("normal")}\n\teventTarget = {TraceFile("event")}\n\tperfTarget = {TraceFile("perf")}\n");

        /// <summary>Everything the operator's trace2 targets recorded.</summary>
        public string OperatorTrace() => string.Concat(new[] { "normal", "event", "perf" }.Select(TraceFile).Where(File.Exists).Select(File.ReadAllText));

        private string TraceFile(string target) => Path.Combine(_root, "trace2-" + target);

        /// <summary>The LFS filters <c>git lfs install</c> puts in global config, so a checkout smudges LFS pointers into their objects.</summary>
        public void InstallLfsFilters() =>
            File.AppendAllText(GlobalConfig, "[filter \"lfs\"]\n\tclean = git-lfs clean -- %f\n\tsmudge = git-lfs smudge -- %f\n\tprocess = git-lfs filter-process\n\trequired = true\n");

        /// <summary>Integrate one contribution — <paramref name="patch"/> over <paramref name="baseSha"/> — into <paramref name="branch"/> with the real integrator and the fake token.</summary>
        public Task<IntegrationResult> IntegrateAsync(string branch, string baseSha, string patch) =>
            new LocalGitBranchIntegrator(Registry, new InlineOffloader(), NullLogger<LocalGitBranchIntegrator>.Instance).IntegrateAsync(new IntegrationRequest
            {
                TeamId = Guid.NewGuid(), RepositoryUrl = Remote.Url, BaseSha = baseSha, Token = GitPublishRemoteFixture.FakeToken, TokenUsername = "x-access-token", IntegrationBranch = branch,
                Contributions = new[] { new BranchContribution { Label = "agent", BaseSha = baseSha, Patch = patch } },
            }, CancellationToken.None);

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

        /// <summary>Test-side git (the agent's own commits) on the same scratch host, hooks off; never recorded, never a positive control.</summary>
        private async Task<string> GitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "-c", "core.hooksPath=/dev/null" }.Concat(args).ToList(), WorkingDirectory = workdir, Environment = _environment, TimeoutSeconds = 120 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public async ValueTask DisposeAsync()
        {
            await Remote.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The real local runner on the scratch operator host, recording every spec the production code submits. With
    /// <see cref="Unreset"/> set, that one subcommand runs as it did before tokened commands were marked — without the
    /// credential-helper reset, with trace2 on — the positive control.
    /// </summary>
    private sealed class OperatorConfigRunner(IReadOnlyDictionary<string, string> environment) : ISandboxRunner
    {
        private readonly LocalProcessRunner _inner = new();

        public string Kind => "local";
        public string? Unreset { get; set; }
        public List<SandboxSpec> Specs { get; } = new();

        public bool Ran(string subcommand) => Specs.Any(s => Subcommand(s.Args) == subcommand);

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);

            var unreset = Subcommand(spec.Args) == Unreset;
            var env = new Dictionary<string, string>(spec.Environment);
            foreach (var (key, value) in environment) env[key] = value;
            if (unreset) foreach (var key in TraceOff) env.Remove(key);

            return _inner.RunAsync(spec with { Args = unreset ? WithoutTheReset(spec.Args) : spec.Args, Environment = env }, cancellationToken);
        }

        private static IReadOnlyList<string> WithoutTheReset(IReadOnlyList<string> args)
        {
            var at = Enumerable.Range(0, Math.Max(0, args.Count - 1)).FirstOrDefault(i => args[i] == "-c" && IsHelperReset(args[i + 1]), -1);

            return at < 0 ? args : args.Take(at).Concat(args.Skip(at + 2)).ToList();
        }
    }

    private sealed class InlineOffloader : IArtifactOffloader
    {
        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken) => Task.FromResult(inline ?? "");

        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
