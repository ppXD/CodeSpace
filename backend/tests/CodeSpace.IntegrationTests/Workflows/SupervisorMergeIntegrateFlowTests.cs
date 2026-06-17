using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 SOTA #3 step 7 — the supervisor INTEGRATE + SYNTHESIS lane, driven against the REAL
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> resolved from DI, over REAL Postgres
/// + REAL git (a seeded <see cref="Repository"/> + a bare-repo remote) + the real <c>LocalGitBranchIntegrator</c> + the
/// honest <see cref="DeterministicSynthLlmClient"/> at the <see cref="Core.Services.Workflows.Llm.ILLMClient"/> seam.
/// The K terminal agent results are SEEDED directly (with real unified diffs + base SHA + produced branch), exactly as
/// the proven plan→spawn→barrier arc (<c>SupervisorMergeFoldFlowTests</c> / <c>SupervisorRealAgentE2ETests</c>) would
/// produce them — so this test focuses on the NEW augment path without re-running the already-covered agent execution.
///
/// <para>Crown jewels: with the integrate opt-in ON, the merge outcome carries an <c>integration</c> key (Status=Clean
/// + an integrated branch on the remote — NOT only the side-by-side fold) AND a <c>synthesis</c> key whose prompt
/// (echoed by the deterministic fake) contains the REAL diff hunk bodies (proving the reduce reads diffs, not
/// summaries); with the gate OFF the outcome is byte-identical to the deterministic fold (no integration / synthesis
/// keys); two real agents editing the SAME line fall back SAFE (Conflicted, no branch). Skips on Windows / no git.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
public sealed class SupervisorMergeIntegrateFlowTests : IDisposable
{
    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    private readonly PostgresFixture _fixture;
    private readonly string? _flagBefore;

    public SupervisorMergeIntegrateFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        // Drive the gate purely by the per-run profile opt-in: ensure the ambient flag is OFF so the gate-OFF test is deterministic.
        _flagBefore = Environment.GetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar);
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, _flagBefore);

    [Fact]
    public async Task Integrate_optIn_produces_a_clean_integrated_branch_and_a_synthesis_over_the_real_diffs()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new BareRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["a.txt"] = "base-a\n", ["b.txt"] = "base-b\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "agent-a-edited-LINE\n"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "b.txt"), "agent-b-edited-LINE\n"));

        var idA = await SeedAgentRunAsync(runId, teamId, "do alpha", baseSha, patchA, "codespace/agent/a");
        var idB = await SeedAgentRunAsync(runId, teamId, "do beta", baseSha, patchB, "codespace/agent/b");

        var outcome = await ExecuteMergeAsync(runId, teamId, integrate: true, idA, idB);

        var integration = outcome.GetProperty("integration");
        integration.GetProperty("status").GetString().ShouldBe("Clean", "two disjoint-file agents integrate cleanly into one branch");
        var branch = integration.GetProperty("integratedBranch").GetString();
        branch.ShouldBe($"codespace/integration/{runId:N}/turn1", "the integrated branch is the run-id + merge-turn-derived reviewable branch");

        (await remote.RemoteFileAsync(branch!, "a.txt")).ShouldContain("agent-a-edited-LINE", customMessage: "agent A's change landed on the integrated branch");
        (await remote.RemoteFileAsync(branch!, "b.txt")).ShouldContain("agent-b-edited-LINE", customMessage: "agent B's change landed on the integrated branch — INTEGRATED, not narrated");

        // The synthesis reduce READ the real diffs: the deterministic fake echoes its prompt, so a real hunk body line proves the diffs (not just summaries) were threaded in.
        var synthesis = outcome.GetProperty("synthesis");
        var synthesisText = synthesis.GetProperty("text").GetString();
        synthesisText.ShouldContain("agent-a-edited-LINE", customMessage: "the synthesis prompt carried agent A's real diff hunk body, not just its summary");
        synthesisText.ShouldContain("+agent-b-edited-LINE", customMessage: "the synthesis prompt carried agent B's real unified-diff add line");
        synthesis.GetProperty("model").GetString().ShouldBe("claude-sonnet-4-5", customMessage: "a blank profile model resolves to a REAL model id — never the literal \"default\" that 400s the real API");
    }

    [Fact]
    public async Task Gate_off_is_byte_identical_to_the_deterministic_fold()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new BareRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["a.txt"] = "base-a\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "edited\n"));
        var idA = await SeedAgentRunAsync(runId, teamId, "do alpha", baseSha, patchA, "codespace/agent/a");

        var raw = await ExecuteMergeRawAsync(runId, teamId, integrate: false, idA);

        // TRUE byte-identity: the gate-OFF outcome must equal, character-for-character, what the pre-SOTA-#3 fold
        // produced — the anonymous { merged:[{8 fields}], count, synthesisInstruction } serialized with AgentJson.
        // A key reorder, a null-handling flip, a renamed/added field, or a stray top-level key all fail HERE.
        var expected = JsonSerializer.Serialize(new
        {
            merged = new[]
            {
                new { agentRunId = idA, status = "Succeeded", summary = "do alpha", changedFiles = new[] { "a.txt" }, producedBranch = "codespace/agent/a", patch = patchA, patchArtifactId = (Guid?)null, error = (string?)null },
            },
            count = 1,
            synthesisInstruction = "combine both branches",
        }, AgentJson.Options);

        raw.ShouldBe(expected, customMessage: "gate OFF must be byte-identical to the pre-SOTA-#3 deterministic fold — no integration / synthesis key, no shape drift");
        (await remote.RemoteHasBranchAsync($"codespace/integration/{runId:N}/turn1")).ShouldBeFalse("nothing is pushed when the gate is off");
    }

    [Fact]
    public async Task Conflicting_agents_fall_back_safe_no_branch_pushed()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new BareRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["f.txt"] = "shared\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "A-change\n"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "B-change\n"));

        var idA = await SeedAgentRunAsync(runId, teamId, "do alpha", baseSha, patchA, "codespace/agent/a");
        var idB = await SeedAgentRunAsync(runId, teamId, "do beta", baseSha, patchB, "codespace/agent/b");

        var outcome = await ExecuteMergeAsync(runId, teamId, integrate: true, idA, idB);

        outcome.GetProperty("integration").GetProperty("status").GetString().ShouldBe("Conflicted", "two edits to the same line cannot auto-integrate");
        outcome.GetProperty("integration").GetProperty("integratedBranch").ValueKind.ShouldBe(JsonValueKind.Null);
        (await remote.RemoteHasBranchAsync($"codespace/integration/{runId:N}")).ShouldBeFalse("a conflict pushes NO branch — the K agent branches remain for human review");
        outcome.GetProperty("merged").GetArrayLength().ShouldBe(2, "the side-by-side fold (each agent's work) is still recorded as the fallback");
    }

    [Fact]
    public async Task A_failed_agent_among_the_set_is_excluded_not_a_set_sinker()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new BareRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["a.txt"] = "base-a\n", ["b.txt"] = "base-b\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "agent-a\n"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "b.txt"), "agent-b\n"));

        var idA = await SeedAgentRunAsync(runId, teamId, "do alpha", baseSha, patchA, "codespace/agent/a");
        var idFailed = await SeedFailedAgentRunAsync(runId, teamId, "build broke");   // no base, no result
        var idB = await SeedAgentRunAsync(runId, teamId, "do beta", baseSha, patchB, "codespace/agent/b");

        var outcome = await ExecuteMergeAsync(runId, teamId, integrate: true, idA, idFailed, idB);

        var integration = outcome.GetProperty("integration");
        integration.GetProperty("status").GetString().ShouldBe("Clean", "the failed (no-base) agent is EXCLUDED, so the two good diffs still integrate cleanly — one failed sibling never sinks the set");
        integration.GetProperty("appliedCount").GetInt32().ShouldBe(2);
        integration.GetProperty("excludedAgents").EnumerateArray().Select(e => e.GetString()).ShouldContain(idFailed.ToString(), customMessage: "the excluded failed agent is named honestly in the outcome");
        outcome.GetProperty("merged").GetArrayLength().ShouldBe(3, "the side-by-side fold still records ALL three agents (incl. the failed one)");
    }

    // ─── Drive the real executor ───────────────────────────────────────────────────

    private async Task<JsonElement> ExecuteMergeAsync(Guid runId, Guid teamId, bool integrate, params Guid[] agentRunIds) =>
        JsonDocument.Parse(await ExecuteMergeRawAsync(runId, teamId, integrate, agentRunIds)).RootElement.Clone();

    private async Task<string> ExecuteMergeRawAsync(Guid runId, Guid teamId, bool integrate, params Guid[] agentRunIds)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var context = new SupervisorTurnContext
        {
            Goal = Goal,
            SupervisorRunId = runId,
            TeamId = teamId,
            NodeId = NodeId,
            TurnNumber = 1,
            PriorDecisions = new[]
            {
                new SupervisorPriorDecision
                {
                    Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
                    PayloadJson = """{"subtaskIds":["s1","s2"]}""",
                    OutcomeJson = JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options),
                },
            },
            AgentProfile = new SupervisorAgentProfile { RepositoryId = integrate ? Repo(scope, teamId) : null, IntegrateBranches = integrate },
        };

        var decision = new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.Merge,
            PayloadJson = JsonSerializer.Serialize(new SupervisorMergePayload { SynthesisInstruction = "combine both branches" }, AgentJson.Options),
        };

        var execution = await executor.ExecuteAsync(decision, context, CancellationToken.None);

        return execution.OutcomeJson;
    }

    /// <summary>The repository this run's team owns (seeded just before) — the profile's RepositoryId for the integrate path. Team-scoped because the Postgres fixture is shared across tests.</summary>
    private static Guid Repo(ILifetimeScope scope, Guid teamId) =>
        scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().Single(r => r.TeamId == teamId).Id;

    // ─── Seed ──────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-integrate-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Seed a Succeeded AgentRun whose ResultJson carries the agent's REAL unified diff + recorded base SHA + produced branch — exactly the shape the proven spawn arc persists.</summary>
    private async Task<Guid> SeedAgentRunAsync(Guid runId, Guid teamId, string summary, string baseSha, string patch, string producedBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = summary,
            Patch = patch,
            BaseSha = baseSha,
            ChangedFiles = new[] { "a.txt" },
            ProducedBranch = producedBranch,
        }, AgentJson.Options);

        db.AgentRun.Add(new AgentRun
        {
            Id = id, TeamId = teamId, WorkflowRunId = runId, NodeId = NodeId, IterationKey = $"{NodeId}#turn0#{id:N}",
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}", ResultJson = resultJson,
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Seed a Failed AgentRun with NO ResultJson (so no base, no diff) — the shape a failed / abandoned / analysis-only spawn persists. The merge folds it into the side-by-side array but the integration step excludes it (no base).</summary>
    private async Task<Guid> SeedFailedAgentRunAsync(Guid runId, Guid teamId, string error)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.AgentRun.Add(new AgentRun
        {
            Id = id, TeamId = teamId, WorkflowRunId = runId, NodeId = NodeId, IterationKey = $"{NodeId}#turn0#{id:N}",
            Harness = "codex-cli", Status = AgentRunStatus.Failed, Error = error, TaskJson = "{}", ResultJson = null,
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Seed a Repository bound to a PAT credential pointing at the bare remote — so ResolveByRepositoryIdAsync yields a clone URL + a (file://-ignored) token, and the integrator takes the authenticated push path.</summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var serializer = scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new CodeSpace.Messages.Credentials.PatPayload { Token = "integration-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the remote, with base-seeding, patch-making (rooted at a SHA the same way an agent produces it), and ref inspection. GUID-suffixed; IDisposable best-effort cleans.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-integrate-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
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
            await Config(seed);
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
            return (await Git(seed, "rev-parse", "HEAD")).Trim();
        }

        public async Task<string> MakePatchAsync(string baseSha, Action<string> mutate)
        {
            var work = Path.Combine(_root, "patch-" + Guid.NewGuid().ToString("N"));
            await Git(_root, "clone", _bare, work);
            await Config(work);
            await Git(work, "checkout", "--detach", baseSha);
            mutate(work);
            await Git(work, "add", "-A");
            var patch = await Git(work, "diff", "--cached", "--no-color", baseSha);
            Directory.Delete(work, recursive: true);
            return patch;
        }

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

        public Task<string> RemoteFileAsync(string branch, string file) => Git(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        private static async Task Config(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
