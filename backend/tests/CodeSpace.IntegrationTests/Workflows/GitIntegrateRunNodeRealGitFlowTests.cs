using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Integrators;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High-fidelity Integration: <see cref="GitIntegrateRunNode"/> against a REAL Postgres ledger AND the REAL
/// <see cref="LocalGitBranchIntegrator"/> on a REAL git remote — the tier between <see cref="GitIntegrateRunNodeFlowTests"/>
/// (Medium-mock: real rows, git faked) and the full plan-map E2E (real engine + real agent CLI). It exists to catch a
/// defect in the seam BETWEEN the node's derivation (<see cref="RunIntegrationContributions"/> +
/// <see cref="IntegrationBaseAnchor"/>) and the integrator's OWN base-integrity guard
/// (<see cref="LocalGitBranchIntegrator"/>'s <c>BlockStaleBasesAsync</c>) that neither tier alone can see: the fake
/// integrator never exercises the guard, and the full E2E's fake CLI hides which layer produced a wrong result.
///
/// <para>Filed against a reported false conflict: two independent map-fanout cells each ADD a different, non-overlapping
/// new file from the SAME recorded base, and the integrator reported <c>Conflicted (0/2 applied)</c> even though the
/// same two patches apply cleanly by hand. Crown jewel below pins that this exact shape is Clean on the real path.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GitIntegrateRunNodeRealGitFlowTests
{
    private readonly PostgresFixture _fixture;

    public GitIntegrateRunNodeRealGitFlowTests(PostgresFixture fixture) => _fixture = fixture;

    // ── Crown jewel: the reported shape — two disjoint new-file adds, same recorded base ────

    [Fact]
    public async Task Two_map_cells_each_adding_a_disjoint_new_file_from_the_same_base_integrate_clean()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new RealRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["seed.txt"] = "seed" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "b.txt"), "b"));

        var first = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 9, patch: patchA);
        var second = await SeedAgentRunAsync(teamId, runId, "map#1", minutesAgo: 3, patch: patchB);
        await SeedAgentManifestAsync(teamId, runId, first, repositoryId, "codespace/agent/a", baseSha);
        await SeedAgentManifestAsync(teamId, runId, second, repositoryId, "codespace/agent/b", baseSha);

        var result = await RunNodeAsync(remote, repositoryId, teamId, runId);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["status"].GetString().ShouldBe("Clean", customMessage: $"reason: {ReasonOf(result)}");
        result.Outputs["appliedCount"].GetInt32().ShouldBe(2);

        var branch = result.Outputs["integratedBranch"].GetString()!;
        (await remote.FileAsync(branch, "a.txt")).Trim().ShouldBe("a");
        (await remote.FileAsync(branch, "b.txt")).Trim().ShouldBe("b");
    }

    // ── Same shape, with an older WITHHELD no-op probe row ahead of both in the ledger ───────

    /// <summary>
    /// A THIRD, older agent-kind manifest — a unit that cloned but captured no work (no branch, no patch) — sits in
    /// the ledger ahead of the two real contributions, all sharing the identical base. <see cref="IntegrationBaseAnchor"/>
    /// reads EVERY agent-kind row with a base (not only the surviving contributions), so this pins that a no-op
    /// probe never displaces the anchor away from the base every contribution actually shares.
    /// </summary>
    [Fact]
    public async Task A_no_op_probe_manifest_ahead_of_both_contributions_does_not_move_the_anchor()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new RealRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["seed.txt"] = "seed" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        var probe = await SeedAgentRunAsync(teamId, runId, "map#probe", minutesAgo: 20, patch: "");
        await SeedAgentManifestAsync(teamId, runId, probe, repositoryId, branch: null, baseSha, publishState: PublishState.None);

        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "b.txt"), "b"));

        var first = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 9, patch: patchA);
        var second = await SeedAgentRunAsync(teamId, runId, "map#1", minutesAgo: 3, patch: patchB);
        await SeedAgentManifestAsync(teamId, runId, first, repositoryId, "codespace/agent/a", baseSha);
        await SeedAgentManifestAsync(teamId, runId, second, repositoryId, "codespace/agent/b", baseSha);

        var result = await RunNodeAsync(remote, repositoryId, teamId, runId);

        result.Outputs["status"].GetString().ShouldBe("Clean", customMessage: $"reason: {ReasonOf(result)}");
        result.Outputs["appliedCount"].GetInt32().ShouldBe(2);
    }

    // ── Dependency-staged new-file adds: a dependent's base is its producer's real pushed head ──

    /// <summary>Both add brand-new (disjoint) files, but the dependent is rooted at the PRODUCER's real head (a descendant of the run's anchor) rather than at the shared request base — the shape <c>IntegrationBaseAnchor</c> exists for, exercised with adds instead of edits.</summary>
    [Fact]
    public async Task A_dependent_new_file_add_rooted_at_its_producers_head_integrates_with_it()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new RealRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["seed.txt"] = "seed" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        var (producerPatch, producerHead) = await remote.MakePushedPatchAsync(baseSha, "codespace/agent/producer", d => File.WriteAllText(Path.Combine(d, "producer-new.txt"), "p"));
        var dependentPatch = await remote.MakePatchAsync(producerHead, d => File.WriteAllText(Path.Combine(d, "dependent-new.txt"), "d"));

        var producer = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 9, patch: producerPatch);
        var dependent = await SeedAgentRunAsync(teamId, runId, "map#1", minutesAgo: 3, patch: dependentPatch);
        await SeedAgentManifestAsync(teamId, runId, producer, repositoryId, "codespace/agent/producer", baseSha);
        await SeedAgentManifestAsync(teamId, runId, dependent, repositoryId, "codespace/agent/dependent", producerHead);

        var result = await RunNodeAsync(remote, repositoryId, teamId, runId);

        result.Outputs["status"].GetString().ShouldBe("Clean", customMessage: $"reason: {ReasonOf(result)}");
        result.Outputs["appliedCount"].GetInt32().ShouldBe(2);

        var branch = result.Outputs["integratedBranch"].GetString()!;
        (await remote.FileAsync(branch, "producer-new.txt")).Trim().ShouldBe("p");
        (await remote.FileAsync(branch, "dependent-new.txt")).Trim().ShouldBe("d");
    }

    // ── A GENUINE conflict still reports honestly (regression guard beside the false-conflict pins) ──

    [Fact]
    public async Task Two_contributions_that_really_conflict_still_report_conflicted()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new RealRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["shared.txt"] = "shared\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "shared.txt"), "A-change\n"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "shared.txt"), "B-change\n"));

        var first = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 9, patch: patchA);
        var second = await SeedAgentRunAsync(teamId, runId, "map#1", minutesAgo: 3, patch: patchB);
        await SeedAgentManifestAsync(teamId, runId, first, repositoryId, "codespace/agent/a", baseSha);
        await SeedAgentManifestAsync(teamId, runId, second, repositoryId, "codespace/agent/b", baseSha);

        var result = await RunNodeAsync(remote, repositoryId, teamId, runId);

        result.Outputs["status"].GetString().ShouldBe("Conflicted", "two edits to the same line really cannot auto-integrate");
        result.Outputs["appliedCount"].GetInt32().ShouldBe(0);
        (await remote.HasBranchAsync($"codespace/integration/{runId:N}")).ShouldBeFalse();
    }

    // ── CRLF / no-trailing-newline new files still integrate clean ──────────────────

    /// <summary>One agent's new file uses CRLF line endings, the other has NO trailing newline — both are still disjoint ADDS from the same base. Pins that the 3-way blob-reconstruction fallback (which only ever matters for an EXISTING file's pre-image) never gets in the way of a brand-new file, whatever its line endings.</summary>
    [Fact]
    public async Task Crlf_and_no_trailing_newline_new_files_still_integrate_clean()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new RealRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["seed.txt"] = "seed" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "line1\r\nline2\r\n"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "b.txt"), "no newline at end"));

        var first = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 9, patch: patchA);
        var second = await SeedAgentRunAsync(teamId, runId, "map#1", minutesAgo: 3, patch: patchB);
        await SeedAgentManifestAsync(teamId, runId, first, repositoryId, "codespace/agent/a", baseSha);
        await SeedAgentManifestAsync(teamId, runId, second, repositoryId, "codespace/agent/b", baseSha);

        var result = await RunNodeAsync(remote, repositoryId, teamId, runId);

        result.Outputs["status"].GetString().ShouldBe("Clean", customMessage: $"reason: {ReasonOf(result)}");
        result.Outputs["appliedCount"].GetInt32().ShouldBe(2);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────

    private async Task<NodeResult> RunNodeAsync(RealRemote remote, Guid repositoryId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var integrator = new LocalGitBranchIntegrator(new SandboxRunnerRegistry(new ISandboxRunner[] { new LocalProcessRunner() }), new InlineOnlyOffloader(), NullLogger<LocalGitBranchIntegrator>.Instance);
        var node = new GitIntegrateRunNode(integrator, new RealRemoteResolver(remote.Url), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        return await node.RunAsync(Context(repositoryId, teamId, runId), CancellationToken.None);
    }

    private static string? ReasonOf(NodeResult result) =>
        result.Outputs.TryGetValue("reason", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    // ─── Seeds (mirrors GitIntegrateRunNodeFlowTests' seeding, extended with a real base/patch) ───

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
            {
                Name = "integrate-realgit-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, Guid runId, string iterationKey, int minutesAgo, string patch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);

        db.AgentRun.Add(new AgentRun
        {
            Id = id, TeamId = teamId, WorkflowRunId = runId, NodeId = "agent", IterationKey = iterationKey,
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "do the work", Harness = "codex-cli" }, Core.Services.Agents.AgentJson.Options),
            ResultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = patch }, Core.Services.Agents.AgentJson.Options),
            CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedAgentManifestAsync(Guid teamId, Guid runId, Guid agentRunId, Guid repositoryId, string? branch, string baseSha, PublishState publishState = PublishState.Pushed)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId, WorkflowRunId = runId, RepositoryId = repositoryId, RepositoryAlias = "primary",
            BaseSha = baseSha, Branch = branch, PublishStateValue = publishState,
        }, CancellationToken.None);
    }

    private static NodeRunContext Context(Guid repositoryId, Guid teamId, Guid runId) => new()
    {
        Inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = JsonSerializer.SerializeToElement(repositoryId.ToString()) },
        Config = new Dictionary<string, JsonElement>(),
        ResumePayload = null,
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope
        {
            Trigger = new Dictionary<string, JsonElement>(),
            Sys = new Dictionary<string, JsonElement>
            {
                [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(teamId.ToString()),
                [SystemScopeKeys.WorkflowRunId] = JsonSerializer.SerializeToElement(runId.ToString()),
            },
        },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };

    /// <summary>An IArtifactOffloader that only ever sees inline text in this suite (no test seeds a PatchArtifactId) — passes it straight through.</summary>
    private sealed class InlineOnlyOffloader : IArtifactOffloader
    {
        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(artifactId is null ? inline ?? "" : throw new NotSupportedException("this suite never offloads a patch"));

        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException("this suite never offloads a patch");
    }

    private sealed class RealRemoteResolver : IAgentWorkspaceResolver
    {
        private readonly string _url;
        public RealRemoteResolver(string url) => _url = url;

        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null) =>
            Task.FromResult<WorkspaceRequest?>(new WorkspaceRequest { RepositoryUrl = _url, Ref = @ref ?? "main", Token = "integration-token", TokenUsername = "x-access-token" });
    }

    /// <summary>A REAL bare git remote on disk — the same shape as <c>LocalGitBranchIntegratorFlowTests.IntegratorTestContext</c>'s harness, kept file-local so each fidelity tier owns its own git plumbing.</summary>
    private sealed class RealRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-integrate-realgit-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public RealRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task<string> SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await ConfigAsync(seed);

            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);

            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");

            return (await Git(seed, "rev-parse", "HEAD")).Trim();
        }

        /// <summary>A unified diff rooted at <paramref name="baseSha"/>, produced the same way an agent's real capture does (checkout the base detached, mutate, stage, diff against the base).</summary>
        public async Task<string> MakePatchAsync(string baseSha, Action<string> mutate)
        {
            var work = Path.Combine(_root, "patch-" + Guid.NewGuid().ToString("N"));
            await Git(_root, "clone", _bare, work);
            await ConfigAsync(work);
            await Git(work, "checkout", "--detach", baseSha);
            mutate(work);
            await Git(work, "add", "-A");
            var patch = await Git(work, "diff", "--cached", "--no-color", baseSha);
            Directory.Delete(work, recursive: true);
            return patch;
        }

        /// <summary>A patch that is ALSO pushed to <paramref name="branch"/>, returning its head SHA — the dependency-staging shape (a dependent's recorded base is a producer's branch head, not the shared request base).</summary>
        public async Task<(string Patch, string HeadSha)> MakePushedPatchAsync(string baseSha, string branch, Action<string> mutate)
        {
            var work = Path.Combine(_root, "produce-" + Guid.NewGuid().ToString("N"));
            await Git(_root, "clone", _bare, work);
            await ConfigAsync(work);
            await Git(work, "checkout", "--detach", baseSha);
            mutate(work);
            await Git(work, "add", "-A");
            var patch = await Git(work, "diff", "--cached", "--no-color", baseSha);
            await Git(work, "commit", "-m", "producer");

            var head = (await Git(work, "rev-parse", "HEAD")).Trim();
            await Git(work, "push", "origin", $"HEAD:refs/heads/{branch}");
            Directory.Delete(work, recursive: true);

            return (patch, head);
        }

        public async Task<bool> HasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

        public Task<string> FileAsync(string branch, string file) =>
            Git(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        private static async Task ConfigAsync(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
