using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Agents.Workspace.Integrators;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Diagnostics;

namespace CodeSpace.SandboxTests;

[Trait("Category", "Sandbox")]
public sealed class GitWorkspaceIsolationE2ETests
{
    [KernelFact]
    public async Task Killing_the_worker_process_still_terminates_its_confined_command()
    {
        var directory = Directory.CreateTempSubdirectory("cs-worker-death-").FullName;
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(SandboxTestHost).Assembly.Location, "--lifetime-host", directory }) info.ArgumentList.Add(arg);
        using var worker = Process.Start(info).ShouldNotBeNull();
        var stdout = worker.StandardOutput.ReadToEndAsync();
        var stderr = worker.StandardError.ReadToEndAsync();
        Process? sandbox = null;
        try
        {
            var ready = Path.Combine(directory, "ready");
            for (var attempt = 0; attempt < 200 && !File.Exists(ready) && !worker.HasExited; attempt++) await Task.Delay(25);
            File.Exists(ready).ShouldBeTrue("the worker must have started the real sandbox before the crash");
            var children = Directory.EnumerateDirectories($"/proc/{worker.Id}/task").SelectMany(task =>
            {
                try { return File.ReadAllText(Path.Combine(task, "children")).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); }
                catch (IOException) { return []; }
            }).Distinct().ToArray();
            children.Length.ShouldBe(1, "the dedicated launcher owns exactly this worker's one sandbox process");
            sandbox = Process.GetProcessById(children[0]);
            sandbox.HasExited.ShouldBeFalse();
            worker.Kill(entireProcessTree: false);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            for (var attempt = 0; attempt < 200 && !HasTerminated(sandbox); attempt++) await Task.Delay(25);
            HasTerminated(sandbox).ShouldBeTrue("keeping the native launch thread alive must preserve bwrap's worker-death kill guarantee; an unreaped zombie is terminated but a live process is not");
            var pulse = Path.Combine(directory, "pulse");
            var stoppedLength = new FileInfo(pulse).Length;
            await Task.Delay(300);
            new FileInfo(pulse).Length.ShouldBe(stoppedLength, "the confined writer must stop after its worker is killed");
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            if (sandbox is not null && !HasTerminated(sandbox)) sandbox.Kill(entireProcessTree: true);
            sandbox?.Dispose();
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool HasTerminated(Process process)
    {
        if (process.HasExited) return true;
        try
        {
            // After its parent is killed this is not our child to reap. Container PID 1 can retain a zombie;
            // kill(pid, 0), used by non-child Process observation, does not distinguish that from a live task.
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            return stat[stat.LastIndexOf(')') + 2] is 'Z' or 'X';
        }
        catch (DirectoryNotFoundException) { return true; }
        catch (FileNotFoundException) { return true; }
    }

    [KernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_command_survives_its_managed_launch_thread_while_the_worker_process_is_alive(bool stream)
    {
        var directory = Directory.CreateTempSubdirectory("cs-command-thread-").FullName;
        Task<SandboxResult>? operation = null;
        var lines = new List<string>();
        Exception? startFailure = null;
        var ready = Path.Combine(directory, "ready");
        var starter = new Thread(() =>
        {
            try
            {
                var runner = new LocalProcessRunner();
                var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "touch ready; sleep 2; printf 'finished\\n'" }, WorkingDirectory = directory, TimeoutSeconds = 10 };
                operation = stream ? runner.RunStreamingAsync(spec, (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None) : runner.RunAsync(spec, CancellationToken.None);
                SpinWait.SpinUntil(() => File.Exists(ready) || operation.IsCompleted, TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { startFailure = error; }
        });
        try
        {
            starter.Start();
            starter.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            startFailure.ShouldBeNull();
            File.Exists(ready).ShouldBeTrue("the sandbox must be alive before its managed launch thread exits");
            var result = await operation.ShouldNotBeNull();
            result.Status.ShouldBe(SandboxStatus.Success, $"the worker process is still alive; retiring a managed thread must not kill its command (exit={result.ExitCode}, stderr={result.Stderr})");
            if (stream) lines.ShouldBe(new[] { "finished" });
            else result.Stdout.ShouldBe("finished\n");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Pack_clone_uses_only_its_destination_and_reclaims_it()
    {
        RequireConfinementWhenConfigured();
        await using var origin = new GitHttpFixture();
        await origin.StartAsync();
        var runner = new RecordingRunner();
        var fetcher = new PackCloneFetcher(new FixtureAllowlist(origin.Url), new SandboxRunnerRegistry(new[] { runner }), NullLogger<PackCloneFetcher>.Instance);
        string directory;
        using (var checkout = await fetcher.FetchAsync(origin.Url, "main", CancellationToken.None))
        {
            directory = checkout.Directory;
            (await File.ReadAllTextAsync(Path.Combine(directory, "later.txt"))).ShouldBe("later\n");
            runner.Specs.Single().WorkingDirectory.ShouldBe(directory);
            runner.Specs.Single().ReadOnlyPaths.ShouldBeEmpty();
        }
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public async Task Branch_integration_clones_applies_and_pushes_the_exact_patch()
    {
        RequireConfinementWhenConfigured();
        await using var origin = new GitHttpFixture();
        await origin.StartAsync();
        var runner = new RecordingRunner();
        var integrator = new LocalGitBranchIntegrator(new SandboxRunnerRegistry(new[] { runner }), new InlineOffloader(), NullLogger<LocalGitBranchIntegrator>.Instance);
        var result = await integrator.IntegrateAsync(new IntegrationRequest
        {
            TeamId = Guid.NewGuid(), RepositoryUrl = origin.Url, BaseSha = origin.BaseSha, Token = "fixture-only-token", IntegrationBranch = "codespace/integration-test",
            Contributions = new[] { new BranchContribution { Label = "fixture", BaseSha = origin.BaseSha, Patch = ReadmePatch } },
        }, CancellationToken.None);
        result.Status.ShouldBe(IntegrationStatus.Clean, result.Reason);
        result.AppliedCount.ShouldBe(1);
        origin.AuthenticatedPushRequests.ShouldBeGreaterThan(0, "the remote must validate the actual push credential");
        (await GitHttpFixture.GitAsync(origin.Root, new[] { "--git-dir", origin.Remote, "show", "codespace/integration-test:README.md" })).ShouldBe("integrated\n");
        (await GitHttpFixture.GitAsync(origin.Root, new[] { "--git-dir", origin.Remote, "rev-parse", "main" })).Trim().ShouldBe(origin.TipSha);
        var directory = runner.Specs.Single(spec => spec.Args.Contains("clone")).WorkingDirectory.ShouldNotBeNull();
        runner.Specs.ShouldAllBe(spec => spec.WorkingDirectory == directory && spec.ReadOnlyPaths.Count == 0);
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public async Task Acceptance_patch_clone_runs_the_real_oracle_on_the_applied_base()
    {
        RequireConfinementWhenConfigured();
        await using var origin = new GitHttpFixture();
        await origin.StartAsync();
        var runner = new RecordingRunner();
        var runners = new SandboxRunnerRegistry(new[] { runner });
        var evidence = new EvidenceStore();
        var grader = new SupervisorAcceptanceGrader(new FixtureResolver(new WorkspaceRequest { RepositoryUrl = origin.Url, Token = "fixture-only-token" }), new WorkspaceProviderRegistry(new[] { NewProvider(runner) }), runners,
            new BenchmarkGraderRegistry(new[] { new TestsPassGrader() }), new InlineOffloader(), evidence, null!, NullLogger<SupervisorAcceptanceGrader>.Instance);
        var spec = new SupervisorAcceptanceSpec { Command = new[] { "/bin/sh", "-c", "test \"$(cat README.md)\" = integrated && test ! -e later.txt && printf oracle-verified" } };
        var result = await grader.GradePatchAsync(Guid.NewGuid(), Guid.NewGuid(), origin.BaseSha, ReadmePatch, null, spec, 30, CancellationToken.None);
        result.Passed.ShouldBeTrue(result.Detail);
        result.EvidenceArtifactId.ShouldNotBeNull();
        evidence.Text.ShouldContain("oracle-verified");
        var directory = runner.Specs.Single(command => command.Args.Contains("clone")).WorkingDirectory.ShouldNotBeNull();
        runner.Specs.ShouldAllBe(command => command.WorkingDirectory == directory && command.ReadOnlyPaths.Count == 0);
        runner.Specs.ShouldContain(command => command.Args.Contains("set-url"));
        Directory.Exists(directory).ShouldBeFalse();
        (await GitHttpFixture.GitAsync(origin.Root, new[] { "--git-dir", origin.Remote, "rev-parse", "main" })).Trim().ShouldBe(origin.TipSha);
    }

    private const string ReadmePatch = "diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-base\n+integrated\n";

    private static void RequireConfinementWhenConfigured()
    {
        if (BubblewrapSandbox.IsRequired) BubblewrapSandbox.Available.ShouldNotBeNull("this lane must exercise real confinement");
    }

    [Fact]
    public async Task Network_clone_pin_fetch_capture_and_authenticated_push_match_the_real_remote()
    {
        if (BubblewrapSandbox.IsRequired) BubblewrapSandbox.Available.ShouldNotBeNull("this lane must exercise real confinement");
        await using var origin = new GitHttpFixture();
        await origin.StartAsync();
        var recorder = new RecordingRunner();
        var provider = NewProvider(recorder);
        var request = new WorkspaceRequest { RepositoryUrl = origin.Url, Ref = "session-gone", DefaultRef = "main", PinnedSha = origin.BaseSha, Token = "fixture-only-token", Depth = 1 };
        string workspace;
        await using (var handle = await provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(request), CancellationToken.None))
        {
            workspace = handle.Directory;
            handle.Repositories.Single().BaseSha.ShouldBe(origin.BaseSha, "the pin requires fetching history absent from the shallow tip clone");
            File.Exists(Path.Combine(workspace, "later.txt")).ShouldBeFalse();
            var config = await File.ReadAllTextAsync(Path.Combine(workspace, ".git", "config"));
            config.ShouldContain(origin.Url);
            config.ShouldNotContain(request.Token!);
            await File.WriteAllTextAsync(Path.Combine(workspace, "produced.txt"), "artifact-from-real-workspace\n");
            var capture = await handle.CaptureChangesAsync(CancellationToken.None);
            capture.ChangedFiles.ShouldBe(new[] { "produced.txt" });
            capture.Patch.ShouldContain("+artifact-from-real-workspace");
            var reattached = await provider.CaptureChangesFromPathAsync(workspace, origin.BaseSha, CancellationToken.None);
            reattached.Patch.ShouldBe(capture.Patch);
            var push = (IWorkspacePushHandle)handle;
            (await push.PushChangesAsync("codespace/test-isolated", CancellationToken.None)).ShouldBe("codespace/test-isolated");
            var actualTip = (await GitHttpFixture.GitAsync(origin.Root, new[] { "--git-dir", origin.Remote, "rev-parse", "refs/heads/codespace/test-isolated" })).Trim();
            push.LastPushedCommitSha().ShouldBe(actualTip);
            origin.AuthenticatedPushRequests.ShouldBeGreaterThan(0, "the remote must validate the actual push credential");
            (await GitHttpFixture.GitAsync(origin.Root, new[] { "--git-dir", origin.Remote, "show", "codespace/test-isolated:produced.txt" })).ShouldBe("artifact-from-real-workspace\n");
            (await GitHttpFixture.GitAsync(origin.Root, new[] { "--git-dir", origin.Remote, "rev-parse", "main" })).Trim().ShouldBe(origin.TipSha);
            recorder.Specs.ShouldContain(spec => spec.Args.Contains("fetch"));
            recorder.Specs.ShouldContain(spec => spec.Args.Contains("set-url"));
            foreach (var spec in recorder.Specs)
            {
                spec.WorkingDirectory.ShouldBe(workspace, string.Join(' ', spec.Args));
                spec.ReadOnlyPaths.ShouldBeEmpty("a network repository does not grant host source access");
            }
        }
        Directory.Exists(workspace).ShouldBeFalse();
    }

    [KernelFact]
    public async Task Explicit_local_source_is_read_only_and_siblings_stay_invisible()
    {
        BubblewrapSandbox.Available.ShouldNotBeNull();
        await using var origin = new GitHttpFixture();
        await origin.StartAsync();
        var secret = Path.Combine(origin.Root, "sibling-secret");
        await File.WriteAllTextAsync(secret, "host-private-value");
        var recorder = new RecordingRunner();
        var provider = NewProvider(recorder);
        var request = new WorkspaceRequest { RepositoryUrl = new Uri(origin.Remote).AbsoluteUri, LocalSource = new WorkspaceLocalSource { Directory = origin.Remote }, PinnedSha = origin.BaseSha, Token = "fixture-only-token" };
        await using var handle = await provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(request), CancellationToken.None);
        handle.Repositories.Single().BaseSha.ShouldBe(origin.BaseSha);
        recorder.Specs.Where(spec => spec.Args.Contains("clone") || spec.Args.Contains("fetch")).ShouldAllBe(spec => spec.ReadOnlyPaths.SequenceEqual(new[] { origin.Remote }));
        var sourceHead = await File.ReadAllBytesAsync(Path.Combine(origin.Remote, "HEAD"));
        var result = await new LocalProcessRunner().RunAsync(new SandboxSpec
        {
            Command = "/bin/sh", Args = new[] { "-c", "test -f \"$1/HEAD\" && test ! -e \"$2\" && ! (printf changed > \"$1/HEAD\") && printf success > permitted.txt", "fixture", origin.Remote, secret },
            WorkingDirectory = handle.Directory, ReadOnlyPaths = new[] { origin.Remote }, TimeoutSeconds = 20,
        }, CancellationToken.None);
        result.Status.ShouldBe(SandboxStatus.Success, result.Stderr);
        (await File.ReadAllBytesAsync(Path.Combine(origin.Remote, "HEAD"))).ShouldBe(sourceHead);
        (await File.ReadAllTextAsync(Path.Combine(handle.Directory, "permitted.txt"))).ShouldBe("success");
        var push = (IWorkspacePushHandle)handle;
        await Should.ThrowAsync<WorkspaceException>(() => push.PushChangesAsync("codespace/local-write-denied", CancellationToken.None));
        push.LastPushedCommitSha().ShouldBeNull();
        (await GitHttpFixture.GitAsync(origin.Root, new[] { "--git-dir", origin.Remote, "for-each-ref", "refs/heads/codespace/local-write-denied" })).ShouldBeEmpty();
        await Should.ThrowAsync<WorkspaceException>(() => provider.PrepareAsync(WorkspaceProvisionRequest.FromSingle(request with { LocalSource = null }), CancellationToken.None));
    }

    private static LocalGitWorkspaceProvider NewProvider(ISandboxRunner runner) => new(new SandboxRunnerRegistry(new[] { runner }), NullLogger<LocalGitWorkspaceProvider>.Instance);

    private sealed class RecordingRunner : ISandboxRunner
    {
        private readonly LocalProcessRunner _inner = new();
        public string Kind => "local";
        public List<SandboxSpec> Specs { get; } = new();
        public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            spec.WorkingDirectory.ShouldNotBeNull("a filesystem command must explicitly bind its destination");
            Directory.Exists(spec.WorkingDirectory).ShouldBeTrue("the writable destination must exist before bubblewrap binds it");
            var result = await _inner.RunAsync(spec, cancellationToken);
            if (spec.Args.FirstOrDefault() == "ls-remote" && spec.Args.LastOrDefault() == "session-gone")
            {
                result.Status.ShouldBe(SandboxStatus.Success, $"the missing-ref probe must complete: exit={result.ExitCode}, stderr={result.Stderr.Replace("fixture-only-token", "[redacted]")}");
                result.Stdout.ShouldBeEmpty("the fixture never created session-gone; a successful probe must establish its absence");
            }
            return result;
        }
    }

    private sealed class FixtureAllowlist(string expected) : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => url == expected;
        public void EnsureAllowed(string url) => IsAllowed(url).ShouldBeTrue("the test grants only its own loopback fixture URL");
    }

    private sealed class InlineOffloader : IArtifactOffloader
    {
        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken)
        {
            artifactId.ShouldBeNull("this test exercises inline patches only");
            return Task.FromResult(inline ?? "");
        }
        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixtureResolver(WorkspaceRequest request) : IAgentWorkspaceResolver
    {
        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null) => Task.FromResult<WorkspaceRequest?>(request);
    }

    private sealed class EvidenceStore : IArtifactStore
    {
        public string Text { get; private set; } = "";
        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            Text = System.Text.Encoding.UTF8.GetString(bytes.Span);
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class KernelTheoryAttribute : TheoryAttribute
    {
        public KernelTheoryAttribute()
        {
            if (BubblewrapSandbox.Available is null && !BubblewrapSandbox.IsRequired)
                Skip = "Requires real Linux bubblewrap; the privileged GitHub Actions lane is authoritative.";
        }
    }

    private sealed class KernelFactAttribute : FactAttribute
    {
        public KernelFactAttribute()
        {
            if (BubblewrapSandbox.Available is null && !BubblewrapSandbox.IsRequired)
                Skip = "Requires real Linux bubblewrap; the privileged GitHub Actions lane is authoritative.";
        }
    }
}
