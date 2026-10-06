using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): drives the REAL <see cref="LocalGitWorkspaceProvider"/> + real <c>git</c> against a real
/// local bare-repo remote. After the clone — as a tampering agent could during its turn — each test plants one git
/// code-execution vector in the clone, then runs the production capture, the re-attach capture and the branch
/// checkout/commit, and asserts the vector did not run while the agent's work still reaches the remote.
///
/// <para>The vectors are the ones that fire during capture, commit or checkout: the client hooks those commands run
/// (a vetoing pre-commit among them, and pre-auto-gc, which a commit triggers once the agent lowers <c>gc.auto</c> and
/// enables the maintenance gc task that git 2.54+ leaves off by default), an
/// agent-set <c>core.hooksPath</c>, an agent-set <c>core.fsmonitor</c> program, and textconv / external-diff drivers
/// selected through <c>.gitattributes</c> or <c>diff.external</c>. A positive-control theory over the same list runs the
/// commands the platform used to run, unhardened, and asserts every plant fires — so the hardened theory's silence is
/// the hardening, never a plant that does nothing. The pre-push hook and the remote-redirect config ride the
/// authenticated push, which these tests do not cover.</para>
///
/// <para>One vector is accepted rather than closed: a repo <c>filter.&lt;driver&gt;.clean</c> has no command-line off
/// switch, so it still runs on <c>add -A</c>. It runs with no credential (asserted on the filter's own environment) and
/// on a spec that declares no network; the kernel-enforced half of that (the filter cannot reach a host listener) is
/// asserted where bubblewrap really confines, in <c>GitWorkspaceIsolationE2ETests</c>.</para>
///
/// <para>Every marker lives inside the clone's <c>.git</c>, which capture never stages. Each test owns a GUID-named root
/// and the provider's clone, both removed on every path; tests skip on Windows or without git.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentCloneGitHookIsolationFlowTests
{
    public static TheoryData<string> Vectors => new()
    {
        "pre-commit", "prepare-commit-msg", "commit-msg", "post-commit", "post-checkout", "post-index-change", "reference-transaction", "pre-auto-gc",
        "hooksPath-redirect", "fsmonitor", "diff-textconv", "diff-external", "diff-driver-command",
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task Capture_reattach_capture_and_commit_run_no_agent_planted_git_code(string vector)
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new HookVectorContext();
        await ctx.SeedBareRemoteAsync();
        await using var handle = await ctx.CloneWithTokenAsync();
        await ctx.PlantAsync(vector, handle.Directory);
        await ctx.WriteAgentChangeAsync(handle.Directory);

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);
        var reattached = await ctx.Provider.CaptureChangesFromPathAsync(handle.Directory, handle.Repositories.Single().BaseSha!, CancellationToken.None);
        var branch = await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        changes.ChangedFiles.ShouldContain(HookVectorContext.AgentFile, $"[{vector}] the hardened capture still diffs the agent's change");
        changes.Patch.ShouldContain(HookVectorContext.AgentContent, customMessage: $"[{vector}] the patch carries the real content, not a textconv or external-diff rendering");
        reattached.Patch.ShouldBe(changes.Patch, $"[{vector}] the re-attach capture reads the same change");
        branch.ShouldBe(ctx.BranchName, $"[{vector}] a planted hook or veto must not lose the produced branch");
        (await ctx.RemoteFileAsync(ctx.BranchName, HookVectorContext.AgentFile)).ShouldBe(HookVectorContext.AgentContent, $"[{vector}] the pushed branch carries the agent's file");
        ctx.FiredVectors().ShouldBeEmpty($"[{vector}] agent-planted git code ran during capture / commit / checkout — see {ctx.MarkerPath}");
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task Each_planted_vector_fires_under_the_unhardened_commands_the_platform_used_to_run(string vector)
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new HookVectorContext();
        await ctx.SeedBareRemoteAsync();
        await using var handle = await ctx.CloneWithTokenAsync();
        await ctx.PlantAsync(vector, handle.Directory);
        await ctx.WriteAgentChangeAsync(handle.Directory);

        await ctx.RunUnhardenedCaptureAndCommitAsync(handle.Directory, handle.Repositories.Single().BaseSha!);

        ctx.FiredVectors().ShouldContain(vector, $"[{vector}] the plant must fire under plain git, or the hardened theory proves nothing — see {ctx.MarkerPath}");
    }

    [Fact]
    public async Task A_repo_clean_filter_still_runs_on_capture_but_with_no_credential_and_no_declared_network()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new HookVectorContext();
        await ctx.SeedBareRemoteAsync();
        await using var handle = await ctx.CloneWithTokenAsync();
        await ctx.PlantCleanFilterAsync(handle.Directory);
        await ctx.WriteAgentChangeAsync(handle.Directory);

        var changes = await handle.CaptureChangesAsync(CancellationToken.None);
        (await ((IWorkspacePushHandle)handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        changes.Patch.ShouldContain(HookVectorContext.AgentContent, customMessage: "the clean filter passes content through, so the diff is intact");

        // Accepted: filter.<driver>.clean has no -c off switch, so it still runs on `add -A` — at the agent's own uid,
        // which already controls those bytes. What must hold is that it gets nothing the agent does not already have.
        var filterSaw = ctx.CleanFilterRecord();
        filterSaw.ShouldNotBeEmpty("the repo-local clean filter runs on add -A — this vector is accepted, not closed");
        filterSaw.ShouldNotContain(HookVectorContext.Token, customMessage: "no clone credential reaches the filter's argv or environment");
        filterSaw.ShouldNotContain(Uri.EscapeDataString(HookVectorContext.Token), customMessage: "nor its URL-encoded form");

        var adds = ctx.RecordedSpecs.Where(s => s.Args.Contains("add")).ToList();
        adds.Count.ShouldBe(2, "capture and commit each stage once");
        adds.ShouldAllBe(s => !s.AllowNetwork && s.Environment.Count == 0, "the add -A a surviving filter rides declares no network and carries no credential");
    }

    [Fact]
    public async Task The_hardened_argv_starts_no_fsmonitor_program_not_even_one_named_false()
    {
        // `core.fsmonitor=false` disables the monitor only on git 2.36+; older git reads the value as a program and runs
        // `false` from PATH on every index refresh. The empty value the hardening uses disables it on every version — a
        // PATH shim named `false` must never run.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new HookVectorContext();
        await ctx.SeedBareRemoteAsync();
        await using var handle = await ctx.CloneWithTokenAsync();
        var shim = ctx.PlantPathShim("false");
        await ctx.WriteAgentChangeAsync(handle.Directory);

        var spec = AgentCloneGitCommand.Build(new[] { "add", "-A" }, handle.Directory, Array.Empty<string>(), 60);
        var result = await new LocalProcessRunner().RunAsync(spec with { Environment = new Dictionary<string, string> { ["PATH"] = shim + ":" + Environment.GetEnvironmentVariable("PATH") } }, CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.Success, result.Stderr);
        ctx.FiredVectors().ShouldBeEmpty($"the hardened add -A started a program named `false` — see {ctx.MarkerPath}");
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare remote and the provider's clone of it, with plant helpers that write every vector's executable inside the clone's <c>.git</c> and have it append its vector name to <see cref="MarkerPath"/>. Records the specs the provider hands the runner.</summary>
    private sealed class HookVectorContext : IDisposable
    {
        public const string Token = "fake-push-token-0123456789";
        public const string AgentFile = "agent-change.txt";
        public const string AgentContent = "produced by the agent\n";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-hookvec-" + Guid.NewGuid().ToString("N"));
        private readonly string _bareRemote;
        private readonly RecordingRunner _runner = new();
        private string? _clone;

        public HookVectorContext()
        {
            Directory.CreateDirectory(_root);
            _bareRemote = Path.Combine(_root, "remote.git");
            Provider = new LocalGitWorkspaceProvider(new SandboxRunnerRegistry(new ISandboxRunner[] { _runner }), NullLogger<LocalGitWorkspaceProvider>.Instance);
        }

        public LocalGitWorkspaceProvider Provider { get; }

        public string BranchName { get; } = "codespace/agent/" + Guid.NewGuid().ToString("N");

        public IReadOnlyList<SandboxSpec> RecordedSpecs => _runner.Specs;

        public string MarkerPath => Path.Combine(GitDir, "vector-fired");

        private string GitDir => Path.Combine(_clone ?? throw new InvalidOperationException("clone first"), ".git");

        private string CleanFilterRecordPath => Path.Combine(GitDir, "clean-filter-saw");

        public IReadOnlyList<string> FiredVectors() => File.Exists(MarkerPath) ? File.ReadAllLines(MarkerPath).Where(l => l.Length > 0).Distinct().ToList() : Array.Empty<string>();

        public string CleanFilterRecord() => File.Exists(CleanFilterRecordPath) ? File.ReadAllText(CleanFilterRecordPath) : "";

        public async Task SeedBareRemoteAsync()
        {
            await GitAsync(_root, "init", "--bare", "-b", "main", _bareRemote);

            var seed = Path.Combine(_root, "seed");
            await GitAsync(_root, "clone", _bareRemote, seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "base\n");
            await GitAsync(seed, "add", ".");
            await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@codespace.dev", "-c", "commit.gpgsign=false", "commit", "-m", "seed");
            await GitAsync(seed, "push", "origin", "main");
        }

        public async Task<IWorkspaceHandle> CloneWithTokenAsync()
        {
            var handle = await Provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = new Uri(_bareRemote).AbsoluteUri, Token = Token, TokenUsername = "x-access-token" }), CancellationToken.None);
            _clone = handle.Directory;
            return handle;
        }

        public Task WriteAgentChangeAsync(string cloneDir) => File.WriteAllTextAsync(Path.Combine(cloneDir, AgentFile), AgentContent);

        /// <summary>Plant one vector into the clone exactly as a tampering agent could between the clone and the platform's capture.</summary>
        public async Task PlantAsync(string vector, string cloneDir)
        {
            switch (vector)
            {
                case "hooksPath-redirect":
                    var redirect = Path.Combine(GitDir, "agent-hooks");
                    WriteScript(Path.Combine(redirect, "pre-commit"), vector, "exit 1");   // a veto in the agent-chosen hooks dir
                    await GitAsync(cloneDir, "config", "core.hooksPath", redirect);
                    break;

                case "fsmonitor":
                    await GitAsync(cloneDir, "config", "core.fsmonitor", WriteScript(Path.Combine(GitDir, "fsmonitor.sh"), vector, "exit 0"));
                    break;

                case "diff-textconv":
                    await File.WriteAllTextAsync(Path.Combine(cloneDir, ".gitattributes"), "* diff=agent\n");
                    await GitAsync(cloneDir, "config", "diff.agent.textconv", WriteScript(Path.Combine(GitDir, "textconv.sh"), vector, "cat \"$1\""));
                    break;

                case "diff-external":
                    await GitAsync(cloneDir, "config", "diff.external", WriteScript(Path.Combine(GitDir, "extdiff.sh"), vector, "exit 0"));
                    break;

                case "diff-driver-command":
                    await File.WriteAllTextAsync(Path.Combine(cloneDir, ".gitattributes"), "* diff=agent\n");
                    await GitAsync(cloneDir, "config", "diff.agent.command", WriteScript(Path.Combine(GitDir, "driverdiff.sh"), vector, "exit 0"));
                    break;

                case "pre-auto-gc":
                    // A second pack + gc.autoPackLimit=1 makes the next commit run `gc --auto`, synchronously. Git 2.54+
                    // defaults auto maintenance to the geometric strategy, which skips the gc task, so the agent turns it
                    // back on with maintenance.gc.enabled, which overrides the strategy and which older git honours too.
                    await GitAsync(cloneDir, "-c", "user.name=Agent", "-c", "user.email=agent@example.test", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", "agent commit");
                    await GitAsync(cloneDir, "repack", "-q");
                    foreach (var (key, value) in new[] { ("gc.auto", "1"), ("gc.autoPackLimit", "1"), ("gc.autoDetach", "false"), ("maintenance.autoDetach", "false"), ("maintenance.gc.enabled", "true") })
                        await GitAsync(cloneDir, "config", key, value);
                    WriteScript(Path.Combine(GitDir, "hooks", vector), vector, "exit 0");
                    break;

                default:   // a client hook by name; pre-commit vetoes to prove a bypassed veto never loses the branch
                    WriteScript(Path.Combine(GitDir, "hooks", vector), vector, vector == "pre-commit" ? "exit 1" : "exit 0");
                    break;
            }
        }

        /// <summary>A repo clean filter that records its argv + environment and passes content through.</summary>
        public async Task PlantCleanFilterAsync(string cloneDir)
        {
            await File.WriteAllTextAsync(Path.Combine(cloneDir, ".gitattributes"), "* filter=agent\n");
            var script = Path.Combine(GitDir, "clean.sh");
            File.WriteAllText(script, $"#!/bin/sh\n{{ echo \"argv: $*\"; env; }} >> '{CleanFilterRecordPath}'\ncat\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await GitAsync(cloneDir, "config", "filter.agent.clean", script);
        }

        /// <summary>A directory inside the clone's <c>.git</c> holding one executable named <paramref name="program"/> that records its name when run; returns the directory for PATH.</summary>
        public string PlantPathShim(string program)
        {
            var shim = Path.Combine(GitDir, "path-shim");
            WriteScript(Path.Combine(shim, program), program, "exit 1");
            return shim;
        }

        /// <summary>The argv the platform ran over the clone before the hardening, run as plain git. Failures are ignored: a veto aborting the commit still fired.</summary>
        public async Task RunUnhardenedCaptureAndCommitAsync(string cloneDir, string baseSha)
        {
            var steps = new[]
            {
                new[] { "add", "-A" },
                new[] { "diff", "--cached", "--no-color", baseSha },
                new[] { "diff", "--cached", "--name-only", baseSha },
                new[] { "diff", "--cached", "--numstat", baseSha },
                new[] { "checkout", "-B", BranchName },
                new[] { "add", "-A" },
                new[] { "-c", "commit.gpgsign=false", "-c", "user.name=CodeSpace", "-c", "user.email=agent@codespace.local", "commit", "-m", $"Agent run {BranchName}" },
            };

            foreach (var args in steps)
                await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = cloneDir, TimeoutSeconds = 60 }, CancellationToken.None);
        }

        public async Task<string> RemoteFileAsync(string branch, string file) => await GitAsync(_root, "--git-dir", _bareRemote, "show", $"{branch}:{file}");

        /// <summary>Write an executable shell script that appends <paramref name="name"/> to the marker, then runs <paramref name="tail"/>; returns its path.</summary>
        private string WriteScript(string path, string name, string tail)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"#!/bin/sh\necho '{name}' >> '{MarkerPath}'\n{tail}\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        /// <summary>Test-side git with hooks off, so planting never fires a vector early.</summary>
        private static async Task<string> GitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "-c", "core.hooksPath=/dev/null" }.Concat(args).ToList(), WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        /// <summary>Records every spec the provider submits, then runs it on the real local runner.</summary>
        private sealed class RecordingRunner : ISandboxRunner
        {
            private readonly LocalProcessRunner _inner = new();
            public string Kind => "local";
            public List<SandboxSpec> Specs { get; } = new();
            public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
            {
                Specs.Add(spec);
                return _inner.RunAsync(spec, cancellationToken);
            }
        }
    }
}
