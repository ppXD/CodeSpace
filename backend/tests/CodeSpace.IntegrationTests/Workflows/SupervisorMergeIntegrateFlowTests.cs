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

    [Fact]
    public async Task Merge_after_a_verified_resolution_surfaces_the_resolver_branch_without_re_integrating()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new BareRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["f.txt"] = "shared\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // Two agents edited the SAME line — re-integrating them would CONFLICT. The verified resolver already
        // reconciled them into its own tested branch, so a merge MUST surface THAT branch, never re-run the integrator.
        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "A-change\n"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "B-change\n"));
        var idA = await SeedAgentRunAsync(runId, teamId, "do alpha", baseSha, patchA, "codespace/agent/a");
        var idB = await SeedAgentRunAsync(runId, teamId, "do beta", baseSha, patchB, "codespace/agent/b");

        const string resolverBranch = "codespace/resolve/reconciled";
        var outcome = await ExecuteMergeAfterResolveAsync(runId, teamId, repoId, resolverBranch, verified: true, idA, idB);

        var integration = outcome.GetProperty("integration");
        integration.GetProperty("status").GetString().ShouldBe("Clean",
            customMessage: "a VERIFIED resolution short-circuits — the integrator (which would CONFLICT on these same-line edits) is never run");
        integration.GetProperty("integratedBranch").GetString().ShouldBe(resolverBranch,
            customMessage: "the surfaced head is the resolver's OWN tested branch — exactly what a downstream git.open_pr targets");
        integration.GetProperty("via").GetString().ShouldBe("resolution", customMessage: "the block is honestly marked as resolution-sourced, not integrator-sourced");
        (await remote.RemoteHasBranchAsync($"codespace/integration/{runId:N}/turn3")).ShouldBeFalse(
            "the integrator was bypassed — no fresh integration branch was pushed to the remote");
    }

    [Fact]
    public async Task Merge_after_an_unverified_resolution_falls_through_to_the_integrator()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new BareRemote();
        var baseSha = await remote.SeedBaseAsync(new() { ["a.txt"] = "base-a\n", ["b.txt"] = "base-b\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // Disjoint files → the integrator integrates cleanly. The resolution is UNVERIFIED (tests red), so it must
        // NOT short-circuit: the merge runs the integrator and produces the INTEGRATOR's branch, proving the
        // unverified resolution was correctly ignored (the safety floor — never accept an unverified resolution).
        var patchA = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "edited-a\n"));
        var patchB = await remote.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "b.txt"), "edited-b\n"));
        var idA = await SeedAgentRunAsync(runId, teamId, "do alpha", baseSha, patchA, "codespace/agent/a");
        var idB = await SeedAgentRunAsync(runId, teamId, "do beta", baseSha, patchB, "codespace/agent/b");

        var outcome = await ExecuteMergeAfterResolveAsync(runId, teamId, repoId, "codespace/resolve/bad", verified: false, idA, idB);

        var integration = outcome.GetProperty("integration");
        integration.GetProperty("status").GetString().ShouldBe("Clean", "the integrator ran (the unverified resolution did not short-circuit)");
        integration.GetProperty("integratedBranch").GetString().ShouldBe($"codespace/integration/{runId:N}/turn3",
            customMessage: "the surfaced head is the INTEGRATOR's branch — an unverified resolution's branch must never be accepted");
        integration.TryGetProperty("via", out _).ShouldBeFalse("the integrator path records no resolution 'via' marker");
        (await remote.RemoteHasBranchAsync($"codespace/integration/{runId:N}/turn3")).ShouldBeTrue("the integrator really pushed its reviewable branch");
    }

    [Fact]
    public async Task Multi_repo_integrates_each_writable_repo_on_its_own_axis()
    {
        // Resolver loop #379 S7-C — a MULTI-repo run integrates EACH writable repo on its own axis: two agents, each
        // touching DISJOINT files in BOTH repos, integrate cleanly into a per-repo branch on each repo's OWN remote.
        // The aggregate status is Clean; the repositories[] array carries one block per repo, each really pushed.
        if (!await GitReadyAsync()) return;

        using var web = new BareRemote();
        using var api = new BareRemote();
        var webBase = await web.SeedBaseAsync(new() { ["web_a.txt"] = "wa\n", ["web_b.txt"] = "wb\n" });
        var apiBase = await api.SeedBaseAsync(new() { ["api_a.txt"] = "aa\n", ["api_b.txt"] = "ab\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId, web.Url, "main");
        var apiRepoId = await SeedBoundRepositoryAsync(teamId, api.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var webPatchA = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web_a.txt"), "web-A\n"));
        var apiPatchA = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "api_a.txt"), "api-A\n"));
        var webPatchB = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web_b.txt"), "web-B\n"));
        var apiPatchB = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "api_b.txt"), "api-B\n"));

        var idA = await SeedMultiRepoAgentRunAsync(runId, teamId, "alpha", ("web", webRepoId, webBase, webPatchA, "codespace/agent/a"), ("api", apiRepoId, apiBase, apiPatchA, "codespace/agent/a"));
        var idB = await SeedMultiRepoAgentRunAsync(runId, teamId, "beta", ("web", webRepoId, webBase, webPatchB, "codespace/agent/b"), ("api", apiRepoId, apiBase, apiPatchB, "codespace/agent/b"));

        var outcome = await ExecuteMultiRepoMergeAsync(runId, teamId, webRepoId, idA, idB);
        var integration = outcome.GetProperty("integration");

        integration.GetProperty("status").GetString().ShouldBe("Clean", "both repos integrate cleanly on disjoint files");
        var repos = integration.GetProperty("repositories").EnumerateArray().ToList();
        repos.Count.ShouldBe(2, "one integration block per writable repo");

        // The synthesis reduce narrates EVERY repo's diff (not just the primary's) — the deterministic fake echoes its
        // prompt, so a SECONDARY (api) hunk body proves the per-repo diffs were threaded in (S7-C synthesis fix).
        var synthesisText = outcome.GetProperty("synthesis").GetProperty("text").GetString();
        synthesisText.ShouldContain("api-A", customMessage: "the multi-repo synthesis prompt carried the SECONDARY api repo's real diff, not only the primary web repo's");

        var webBlock = repos.Single(r => r.GetProperty("alias").GetString() == "web");
        var apiBlock = repos.Single(r => r.GetProperty("alias").GetString() == "api");
        webBlock.GetProperty("repositoryId").GetGuid().ShouldBe(webRepoId, "each block names its repository — the per-repo key the resolution loop acts on");
        webBlock.GetProperty("status").GetString().ShouldBe("Clean");
        apiBlock.GetProperty("status").GetString().ShouldBe("Clean");

        var branch = $"codespace/integration/{runId:N}/turn1";
        (await web.RemoteFileAsync(branch, "web_a.txt")).ShouldContain("web-A", customMessage: "agent A's web change landed on the web integrated branch");
        (await web.RemoteFileAsync(branch, "web_b.txt")).ShouldContain("web-B", customMessage: "agent B's web change landed too — the web repo integrated on its own axis");
        (await api.RemoteFileAsync(branch, "api_a.txt")).ShouldContain("api-A", customMessage: "agent A's api change landed on the api integrated branch (its OWN remote)");
        (await api.RemoteFileAsync(branch, "api_b.txt")).ShouldContain("api-B", customMessage: "agent B's api change landed too — INTEGRATED per repo, not narrated");

        // S7-D1: the per-repo node-output reader surfaces these REAL integrated branches off a terminal tape — ties the
        // ReadFinalRepositoryBranches reader to the REAL multi-repo merge producer (catches any block-shape drift).
        var finalBranches = SupervisorOutcome.ReadFinalRepositoryBranches(new[]
        {
            new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome.GetRawText() },
        });
        finalBranches.Select(r => r.Alias).ShouldBe(new[] { "web", "api" }, ignoreOrder: true, "the S7-D1 node-output reader surfaces both repos' integrated branches off the real multi-repo merge");
        finalBranches.ShouldAllBe(r => r.SourceBranch == branch);
        finalBranches.ShouldAllBe(r => r.TargetBranch == "main", "S7-E: each per-repo branch carries its PR base (the ref it was rooted at) so git.open_change_set binds it as targetBranch");
        finalBranches.Single(r => r.Alias == "web").RepositoryId.ShouldBe(webRepoId, "the per-repo PR-open key round-trips through the reader's Guid parse off the REAL producer block");
        finalBranches.Single(r => r.Alias == "api").RepositoryId.ShouldBe(apiRepoId);
    }

    [Fact]
    public async Task Multi_repo_with_one_conflicting_repo_aggregates_to_Conflicted_and_isolates_the_clean_repo()
    {
        // S7-C — when ONE repo conflicts (both agents edit the same api file) but another is clean (disjoint web files),
        // the aggregate is Conflicted (so the decider resolves), the clean web repo STILL pushes its integrated branch
        // (per-repo fail-safe — one conflicting repo never sinks a clean sibling), and the conflicted api repo pushes
        // none. ReadIntegration surfaces the api conflict's files for the resolver.
        if (!await GitReadyAsync()) return;

        using var web = new BareRemote();
        using var api = new BareRemote();
        var webBase = await web.SeedBaseAsync(new() { ["web_a.txt"] = "wa\n", ["web_b.txt"] = "wb\n" });
        var apiBase = await api.SeedBaseAsync(new() { ["shared.txt"] = "shared\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId, web.Url, "main");
        var apiRepoId = await SeedBoundRepositoryAsync(teamId, api.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var webPatchA = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web_a.txt"), "web-A\n"));
        var webPatchB = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web_b.txt"), "web-B\n"));
        var apiPatchA = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "shared.txt"), "A-wins\n"));
        var apiPatchB = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "shared.txt"), "B-wins\n"));

        var idA = await SeedMultiRepoAgentRunAsync(runId, teamId, "alpha", ("web", webRepoId, webBase, webPatchA, "codespace/agent/a"), ("api", apiRepoId, apiBase, apiPatchA, "codespace/agent/a-api"));
        var idB = await SeedMultiRepoAgentRunAsync(runId, teamId, "beta", ("web", webRepoId, webBase, webPatchB, "codespace/agent/b"), ("api", apiRepoId, apiBase, apiPatchB, "codespace/agent/b-api"));

        var outcome = await ExecuteMultiRepoMergeAsync(runId, teamId, webRepoId, idA, idB);
        var integration = outcome.GetProperty("integration");

        integration.GetProperty("status").GetString().ShouldBe("Conflicted", "one conflicting repo makes the aggregate Conflicted");
        var repos = integration.GetProperty("repositories").EnumerateArray().ToList();
        repos.Single(r => r.GetProperty("alias").GetString() == "web").GetProperty("status").GetString().ShouldBe("Clean", "the clean web repo is isolated — a conflicting sibling never sinks it");
        repos.Single(r => r.GetProperty("alias").GetString() == "api").GetProperty("status").GetString().ShouldBe("Conflicted");

        var branch = $"codespace/integration/{runId:N}/turn1";
        (await web.RemoteHasBranchAsync(branch)).ShouldBeTrue("the clean web repo really pushed its integrated branch");
        (await api.RemoteHasBranchAsync(branch)).ShouldBeFalse("the conflicted api repo pushed NO branch — its agent branches remain for the resolver");

        // The conflict is legible off the SAME ReadIntegration the decider feeds — the api file is surfaced for the resolver.
        var read = SupervisorOutcome.ReadIntegration(outcome.GetRawText());
        read!.IsConflicted.ShouldBeTrue();
        read.ConflictedFiles.ShouldContain("shared.txt", customMessage: "the conflicted repo's file is surfaced across the multi-repo block for the decider/resolver");
    }

    [Fact]
    public async Task Multi_repo_disjoint_fan_out_integrates_clean_without_a_spurious_conflict()
    {
        // BLOCKER regression guard (S7-C review): the capture layer emits a RepositoryRunResult for EVERY writable repo,
        // even one an agent NEVER touched (empty patch, no branch). Agent A works ONLY on web, agent B ONLY on api — so
        // each has a vacuous no-op entry for the OTHER repo. Those must be DROPPED, not fed to the integrator (which
        // would refuse them as "no patch and no branch" → spurious Conflicted). The normal disjoint fan-out is Clean.
        if (!await GitReadyAsync()) return;

        using var web = new BareRemote();
        using var api = new BareRemote();
        var webBase = await web.SeedBaseAsync(new() { ["web.txt"] = "w\n" });
        var apiBase = await api.SeedBaseAsync(new() { ["api.txt"] = "a\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId, web.Url, "main");
        var apiRepoId = await SeedBoundRepositoryAsync(teamId, api.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var webPatchA = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web.txt"), "web-by-A\n"));
        var apiPatchB = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "api.txt"), "api-by-B\n"));

        // Agent A touched ONLY web (its api entry is a vacuous no-op: empty patch, no branch); agent B touched ONLY api.
        var idA = await SeedMultiRepoAgentRunAsync(runId, teamId, "alpha", ("web", webRepoId, webBase, webPatchA, "codespace/agent/a"), ("api", apiRepoId, apiBase, "", null));
        var idB = await SeedMultiRepoAgentRunAsync(runId, teamId, "beta", ("web", webRepoId, webBase, "", null), ("api", apiRepoId, apiBase, apiPatchB, "codespace/agent/b"));

        var integration = (await ExecuteMultiRepoMergeAsync(runId, teamId, webRepoId, idA, idB)).GetProperty("integration");

        integration.GetProperty("status").GetString().ShouldBe("Clean", "a disjoint fan-out has ZERO real conflicts — the untouched no-op entries are dropped, not refused");
        var repos = integration.GetProperty("repositories").EnumerateArray().ToList();
        repos.Single(r => r.GetProperty("alias").GetString() == "web").GetProperty("appliedCount").GetInt32().ShouldBe(1, "only agent A's real web change is integrated — agent B's no-op web entry is dropped, not a contribution");
        repos.Single(r => r.GetProperty("alias").GetString() == "api").GetProperty("appliedCount").GetInt32().ShouldBe(1, "only agent B's real api change is integrated");

        var branch = $"codespace/integration/{runId:N}/turn1";
        (await web.RemoteFileAsync(branch, "web.txt")).ShouldContain("web-by-A");
        (await api.RemoteFileAsync(branch, "api.txt")).ShouldContain("api-by-B", customMessage: "each repo integrated its ONE real contribution — no spurious conflict from a sibling's untouched entry");
    }

    [Fact]
    public async Task Multi_repo_names_a_no_base_contribution_in_the_repo_block_excludedAgents()
    {
        // Per-repo honesty (S7-C review): a contribution with NO base revision is EXCLUDED from that repo's integration
        // and NAMED in its block's excludedAgents — the per-repo analogue of the single-repo honesty invariant — while
        // the based contribution still applies. Proven per repo, where a silently-dropped contribution would hide.
        if (!await GitReadyAsync()) return;

        using var api = new BareRemote();
        var apiBase = await api.SeedBaseAsync(new() { ["api.txt"] = "a\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var apiRepoId = await SeedBoundRepositoryAsync(teamId, api.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var apiPatchA = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "api.txt"), "api-by-A\n"));

        // Agent A has a real api change with a base; agent B touched api (a real patch) but recorded NO base (a re-attached
        // run) → B is excluded + named, A applies. The multi-repo path runs because the agents carry per-repo results.
        var idA = await SeedMultiRepoAgentRunAsync(runId, teamId, "alpha", ("api", apiRepoId, apiBase, apiPatchA, "codespace/agent/a"));
        var idB = await SeedMultiRepoAgentRunAsync(runId, teamId, "beta", ("api", apiRepoId, null, "patch-with-no-base", "codespace/agent/b"));

        var integration = (await ExecuteMultiRepoMergeAsync(runId, teamId, apiRepoId, idA, idB)).GetProperty("integration");

        var apiBlock = integration.GetProperty("repositories").EnumerateArray().Single(r => r.GetProperty("alias").GetString() == "api");
        apiBlock.GetProperty("status").GetString().ShouldBe("Clean", "the no-base contribution is EXCLUDED, so the one based contribution still integrates cleanly");
        apiBlock.GetProperty("appliedCount").GetInt32().ShouldBe(1);
        apiBlock.GetProperty("excludedAgents").EnumerateArray().Select(e => e.GetString()).ShouldContain(idB.ToString(), customMessage: "the no-base agent is named per-repo — a dropped contribution is never silently hidden");
    }

    [Fact]
    public async Task Multi_repo_unresolvable_repo_degrades_to_Skipped_without_sinking_the_clean_sibling_or_stranding_the_turn()
    {
        // Per-repo fail-safe (S7-C review major): the resolver THROWS for a deleted / cross-team repo (never returns
        // null). A ghost repo id must degrade to a NAMED Skipped block — never abort the loop (sinking the clean web
        // sibling) nor escape and strand the merge decision Running. The merge completes; web still pushed its branch.
        if (!await GitReadyAsync()) return;

        using var web = new BareRemote();
        var webBase = await web.SeedBaseAsync(new() { ["web.txt"] = "w\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId, web.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var webPatch = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web.txt"), "web-edited\n"));
        var ghostRepoId = Guid.NewGuid();   // never bound — the real resolver throws "not found for this team"

        var idA = await SeedMultiRepoAgentRunAsync(runId, teamId, "alpha", ("web", webRepoId, webBase, webPatch, "codespace/agent/a"), ("ghost", ghostRepoId, "ghostbase", "patch-for-the-ghost-repo", "codespace/agent/a-ghost"));

        var integration = (await ExecuteMultiRepoMergeAsync(runId, teamId, webRepoId, idA)).GetProperty("integration");

        integration.GetProperty("repositories").EnumerateArray().Single(r => r.GetProperty("alias").GetString() == "web").GetProperty("status").GetString().ShouldBe("Clean", "the clean web repo is not sunk by the unresolvable ghost");
        var ghostBlock = integration.GetProperty("repositories").EnumerateArray().Single(r => r.GetProperty("alias").GetString() == "ghost");
        ghostBlock.GetProperty("status").GetString().ShouldBe("Skipped", "an unresolvable repo degrades to a named Skipped block, never throws");
        ghostBlock.GetProperty("repositoryId").GetGuid().ShouldBe(ghostRepoId);

        (await web.RemoteHasBranchAsync($"codespace/integration/{runId:N}/turn1")).ShouldBeTrue("the clean web repo still pushed its integrated branch — the turn completed, the ghost did not strand it");
    }

    [Fact]
    public async Task Multi_repo_names_null_repository_id_work_as_a_skipped_block()
    {
        // Per-repo honesty (S7-C review minor): per-repo work with a NULL repository id (a degraded capture with no
        // resolvable spec) can't be cloned — it is NAMED as a Skipped block (not silently dropped), so an operator
        // reading repositories[] sees the uncombined work and which agent produced it.
        if (!await GitReadyAsync()) return;

        using var web = new BareRemote();
        var webBase = await web.SeedBaseAsync(new() { ["web.txt"] = "w\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId, web.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var webPatch = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web.txt"), "web-edited\n"));

        // Agent A has a real web change PLUS a degraded "orphan" entry whose repository id is null (no resolvable spec).
        var idA = await SeedMultiRepoAgentRunAsync(runId, teamId, "alpha", ("web", webRepoId, webBase, webPatch, "codespace/agent/a"), ("orphan", null, "orphanbase", "patch-with-no-repo-id", "codespace/agent/a-orphan"));

        var integration = (await ExecuteMultiRepoMergeAsync(runId, teamId, webRepoId, idA)).GetProperty("integration");

        integration.GetProperty("repositories").EnumerateArray().Single(r => r.GetProperty("alias").GetString() == "web").GetProperty("status").GetString().ShouldBe("Clean");

        var orphanBlock = integration.GetProperty("repositories").EnumerateArray().Single(r => r.GetProperty("alias").GetString() == "orphan");
        orphanBlock.GetProperty("status").GetString().ShouldBe("Skipped", "null-id work is named Skipped, never silently omitted from the aggregate");
        orphanBlock.GetProperty("repositoryId").ValueKind.ShouldBe(JsonValueKind.Null);
        orphanBlock.GetProperty("excludedAgents").EnumerateArray().Select(e => e.GetString()).ShouldContain(idA.ToString(), customMessage: "the agent whose null-id work remains on its branch is named");
    }

    [Fact]
    public async Task Multi_repo_merge_after_a_verified_resolution_accepts_the_resolver_branch_per_repo_without_re_conflicting()
    {
        // Resolver loop #379 S7-D2 — the full multi-repo recovery loop's ACCEPTANCE: after a verified multi-repo
        // resolution, a merge accepts EACH resolved repo's reconciled branch (per-repo short-circuit — re-integrating
        // the original same-line api branches would re-conflict), while an originally-CLEAN repo (web, disjoint)
        // re-integrates normally. The resulting repositories[] is the COMPLETE set: web (integrator) + api (resolver).
        if (!await GitReadyAsync()) return;

        using var web = new BareRemote();
        using var api = new BareRemote();
        var webBase = await web.SeedBaseAsync(new() { ["web_a.txt"] = "wa\n", ["web_b.txt"] = "wb\n" });
        var apiBase = await api.SeedBaseAsync(new() { ["shared.txt"] = "shared\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId, web.Url, "main");
        var apiRepoId = await SeedBoundRepositoryAsync(teamId, api.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var webPatchA = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web_a.txt"), "web-A\n"));
        var webPatchB = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web_b.txt"), "web-B\n"));
        var apiPatchA = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "shared.txt"), "A-wins\n"));
        var apiPatchB = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "shared.txt"), "B-wins\n"));

        var idA = await SeedMultiRepoAgentRunAsync(runId, teamId, "alpha", ("web", webRepoId, webBase, webPatchA, "codespace/agent/a"), ("api", apiRepoId, apiBase, apiPatchA, "codespace/agent/a-api"));
        var idB = await SeedMultiRepoAgentRunAsync(runId, teamId, "beta", ("web", webRepoId, webBase, webPatchB, "codespace/agent/b"), ("api", apiRepoId, apiBase, apiPatchB, "codespace/agent/b-api"));

        const string apiResolved = "codespace/resolve/api-reconciled";
        var outcome = await ExecuteMultiRepoMergeAfterResolveAsync(runId, teamId, webRepoId, new[] { idA, idB }, verified: true, (apiRepoId, "api", apiResolved));
        var integration = outcome.GetProperty("integration");

        integration.GetProperty("status").GetString().ShouldBe("Clean", "web re-integrates clean + api is accepted from the verified resolution → the aggregate is Clean");
        var repos = integration.GetProperty("repositories").EnumerateArray().ToList();

        var apiBlock = repos.Single(r => r.GetProperty("alias").GetString() == "api");
        apiBlock.GetProperty("status").GetString().ShouldBe("Clean");
        apiBlock.GetProperty("integratedBranch").GetString().ShouldBe(apiResolved, "api surfaces the RESOLVER's reconciled branch — re-integrating the original same-line branches would re-conflict");
        apiBlock.GetProperty("via").GetString().ShouldBe("resolution", "the api block is honestly marked resolution-sourced");

        var webBlock = repos.Single(r => r.GetProperty("alias").GetString() == "web");
        webBlock.GetProperty("status").GetString().ShouldBe("Clean", "the originally-clean web repo re-integrates normally on disjoint files");
        webBlock.GetProperty("integratedBranch").GetString().ShouldBe($"codespace/integration/{runId:N}/turn4");

        (await web.RemoteHasBranchAsync($"codespace/integration/{runId:N}/turn4")).ShouldBeTrue("web really re-integrated + pushed its branch");
        (await api.RemoteHasBranchAsync($"codespace/integration/{runId:N}/turn4")).ShouldBeFalse("api was NOT re-integrated — the verified resolver branch is accepted, the integrator bypassed for api (it would re-conflict)");

        // S7-D1/D2 round-trip: the node-output reader surfaces BOTH heads off the REAL merge outcome — the re-integrated
        // web branch AND the accepted api resolver branch — tying ResolvedRepoBlock's shape to ReadFinalRepositoryBranches.
        var finalBranches = SupervisorOutcome.ReadFinalRepositoryBranches(new[]
        {
            new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome.GetRawText() },
        });
        var webFinal = finalBranches.Single(b => b.Alias == "web");
        webFinal.SourceBranch.ShouldBe($"codespace/integration/{runId:N}/turn4");
        webFinal.TargetBranch.ShouldBe("main", "S7-E: the re-integrated repo carries its PR base for git.open_change_set");

        var apiFinal = finalBranches.Single(b => b.Alias == "api");
        apiFinal.SourceBranch.ShouldBe(apiResolved, "the accepted resolver branch is the node's per-repo head for api");
        apiFinal.TargetBranch.ShouldBe("main", "S7-E: the accepted-resolution repo carries its PR base too");
    }

    [Fact]
    public async Task Multi_repo_merge_after_an_UNVERIFIED_resolution_re_integrates_and_re_conflicts_the_repo()
    {
        // The safety floor (S7-D2): an UNVERIFIED multi-repo resolution must NEVER be accepted. The merge re-integrates
        // the original same-line api branches (which re-conflict) instead of surfacing the resolver's branch; the clean
        // web repo still integrates. So the aggregate is Conflicted, api carries NO resolution marker, and the resolver
        // branch is not pushed/accepted — exactly the single-repo unverified-fall-through behaviour, per repo.
        if (!await GitReadyAsync()) return;

        using var web = new BareRemote();
        using var api = new BareRemote();
        var webBase = await web.SeedBaseAsync(new() { ["web_a.txt"] = "wa\n", ["web_b.txt"] = "wb\n" });
        var apiBase = await api.SeedBaseAsync(new() { ["shared.txt"] = "shared\n" });

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId, web.Url, "main");
        var apiRepoId = await SeedBoundRepositoryAsync(teamId, api.Url, "main");
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var webPatchA = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web_a.txt"), "web-A\n"));
        var webPatchB = await web.MakePatchAsync(webBase, d => File.WriteAllText(Path.Combine(d, "web_b.txt"), "web-B\n"));
        var apiPatchA = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "shared.txt"), "A-wins\n"));
        var apiPatchB = await api.MakePatchAsync(apiBase, d => File.WriteAllText(Path.Combine(d, "shared.txt"), "B-wins\n"));

        var idA = await SeedMultiRepoAgentRunAsync(runId, teamId, "alpha", ("web", webRepoId, webBase, webPatchA, "codespace/agent/a"), ("api", apiRepoId, apiBase, apiPatchA, "codespace/agent/a-api"));
        var idB = await SeedMultiRepoAgentRunAsync(runId, teamId, "beta", ("web", webRepoId, webBase, webPatchB, "codespace/agent/b"), ("api", apiRepoId, apiBase, apiPatchB, "codespace/agent/b-api"));

        var integration = (await ExecuteMultiRepoMergeAfterResolveAsync(runId, teamId, webRepoId, new[] { idA, idB }, verified: false, (apiRepoId, "api", "codespace/resolve/api-bad"))).GetProperty("integration");

        integration.GetProperty("status").GetString().ShouldBe("Conflicted", "an unverified resolution does not short-circuit — api re-integrates and re-conflicts");
        var repos = integration.GetProperty("repositories").EnumerateArray().ToList();

        var apiBlock = repos.Single(r => r.GetProperty("alias").GetString() == "api");
        apiBlock.GetProperty("status").GetString().ShouldBe("Conflicted", "the unverified api resolution is rejected — the integrator re-ran and the same-line branches re-conflicted");
        apiBlock.TryGetProperty("via", out _).ShouldBeFalse("a re-integrated (not accepted) api block carries no resolution 'via' marker");

        repos.Single(r => r.GetProperty("alias").GetString() == "web").GetProperty("status").GetString().ShouldBe("Clean", "the clean web repo still integrates");
        (await api.RemoteHasBranchAsync($"codespace/integration/{runId:N}/turn4")).ShouldBeFalse("a re-conflicted api pushes no branch; the unverified resolver branch is NOT accepted");
    }

    // ─── Drive the real executor ───────────────────────────────────────────────────

    /// <summary>Execute a multi-repo <c>merge</c> against a tape that already holds spawn → conflicted-multi-repo-merge → multi-repo-resolve (the resolver's RepositoryResults carry each resolved repo's reconciled branch). <paramref name="verified"/> flips the RESOLUTION_VERIFIED marker so a test can drive both the accept (verified → per-repo short-circuit) and the safety-floor (unverified → re-integrate + re-conflict) paths.</summary>
    private async Task<JsonElement> ExecuteMultiRepoMergeAfterResolveAsync(Guid runId, Guid teamId, Guid primaryRepoId, Guid[] agentRunIds, bool verified, params (Guid RepoId, string Alias, string Branch)[] resolverRepos)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var resolverResult = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(),
            Status = "Succeeded",
            Summary = verified ? $"reconciled each conflicted repository. {SupervisorResolverRecipe.TestsPassedMarker}" : "reconciled but the tests are still red",
            ProducedBranch = resolverRepos[0].Branch,
            RepositoryResults = resolverRepos.Select(r => new RepositoryRunResult { Alias = r.Alias, RepositoryId = r.RepoId, ProducedBranch = r.Branch, Access = WorkspaceAccess.Write }).ToArray(),
        };

        var conflictedMerge = JsonSerializer.Serialize(new
        {
            integration = new
            {
                status = "Conflicted",
                repositories = resolverRepos.Select(r => new { repositoryId = r.RepoId, alias = r.Alias, status = "Conflicted", outcomes = new[] { new { label = "agent", disposition = "Conflicted", conflictedFiles = new[] { "shared.txt" }, fallbackBranch = $"codespace/agent/{r.Alias}" } } }).ToArray(),
            },
        }, AgentJson.Options);

        var context = new SupervisorTurnContext
        {
            Goal = Goal,
            SupervisorRunId = runId,
            TeamId = teamId,
            NodeId = NodeId,
            TurnNumber = 4,
            PriorDecisions = new[]
            {
                new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = """{"subtaskIds":["s1","s2"]}""", OutcomeJson = JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options) },
                new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = conflictedMerge },
                new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = new[] { resolverResult.AgentRunId }, agentCount = 1, agentResults = new[] { resolverResult } }, AgentJson.Options) },
            },
            AgentProfile = new SupervisorAgentProfile { RepositoryId = primaryRepoId, IntegrateBranches = true },
        };

        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Merge, PayloadJson = JsonSerializer.Serialize(new SupervisorMergePayload { SynthesisInstruction = "finalize" }, AgentJson.Options) };

        var execution = await executor.ExecuteAsync(decision, context, CancellationToken.None);
        return JsonDocument.Parse(execution.OutcomeJson).RootElement.Clone();
    }

    /// <summary>Execute a multi-repo <c>merge</c>: the profile's primary repo is set explicitly (the agents carry per-repo results), the integrate gate on.</summary>
    private async Task<JsonElement> ExecuteMultiRepoMergeAsync(Guid runId, Guid teamId, Guid primaryRepoId, params Guid[] agentRunIds)
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
            AgentProfile = new SupervisorAgentProfile { RepositoryId = primaryRepoId, IntegrateBranches = true },
        };

        var decision = new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.Merge,
            PayloadJson = JsonSerializer.Serialize(new SupervisorMergePayload { SynthesisInstruction = "combine both branches" }, AgentJson.Options),
        };

        var execution = await executor.ExecuteAsync(decision, context, CancellationToken.None);
        return JsonDocument.Parse(execution.OutcomeJson).RootElement.Clone();
    }


    /// <summary>Execute a <c>merge</c> against a tape that already holds spawn → conflicted-merge → resolve (verified or not, with a pushed branch) — the resolver-loop shape S5's short-circuit (or fall-through) is asserted against.</summary>
    private async Task<JsonElement> ExecuteMergeAfterResolveAsync(Guid runId, Guid teamId, Guid repoId, string resolverBranch, bool verified, params Guid[] agentRunIds)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var resolverResult = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(),
            Status = "Succeeded",
            Summary = verified ? $"reconciled. {SupervisorResolverRecipe.TestsPassedMarker}" : "reconciled but tests still red",
            ProducedBranch = resolverBranch,
        };

        var context = new SupervisorTurnContext
        {
            Goal = Goal,
            SupervisorRunId = runId,
            TeamId = teamId,
            NodeId = NodeId,
            TurnNumber = 3,
            PriorDecisions = new[]
            {
                new SupervisorPriorDecision
                {
                    Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
                    PayloadJson = """{"subtaskIds":["s1","s2"]}""",
                    OutcomeJson = JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options),
                },
                new SupervisorPriorDecision
                {
                    Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded,
                    PayloadJson = "{}",
                    OutcomeJson = JsonSerializer.Serialize(new { integration = new { status = "Conflicted", integratedBranch = (string?)null } }, AgentJson.Options),
                },
                new SupervisorPriorDecision
                {
                    Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded,
                    PayloadJson = "{}",
                    OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = new[] { resolverResult.AgentRunId }, agentCount = 1, agentResults = new[] { resolverResult } }, AgentJson.Options),
                },
            },
            AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId, IntegrateBranches = true },
        };

        var decision = new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.Merge,
            PayloadJson = JsonSerializer.Serialize(new SupervisorMergePayload { SynthesisInstruction = "finalize" }, AgentJson.Options),
        };

        var execution = await executor.ExecuteAsync(decision, context, CancellationToken.None);
        return JsonDocument.Parse(execution.OutcomeJson).RootElement.Clone();
    }

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

    /// <summary>Seed a Succeeded MULTI-repo AgentRun whose ResultJson carries per-repo <see cref="RepositoryRunResult"/>s (each with its own base SHA + per-repo diff + produced branch), exactly as the multi-repo capture+offload path (S7-C0) persists. The top-level fields mirror the PRIMARY (first) repo. Nullable fields let a test express an UNTOUCHED repo (empty Patch + null Branch), a NO-BASE contribution (null BaseSha), or a degraded NULL-id entry (null RepoId).</summary>
    private async Task<Guid> SeedMultiRepoAgentRunAsync(Guid runId, Guid teamId, string summary, params (string Alias, Guid? RepoId, string? BaseSha, string Patch, string? Branch)[] perRepo)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var primary = perRepo[0];

        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = summary,
            Patch = primary.Patch,
            BaseSha = primary.BaseSha,
            ChangedFiles = new[] { $"{primary.Alias}.txt" },
            ProducedBranch = primary.Branch,
            ChangeSetId = $"cs-{id:N}",
            RepositoryResults = perRepo.Select(r => new RepositoryRunResult
            {
                Alias = r.Alias, RepositoryId = r.RepoId, BaseSha = r.BaseSha, BaseBranch = "main",
                Patch = r.Patch, ProducedBranch = r.Branch, ChangedFiles = new[] { $"{r.Alias}.txt" }, Access = WorkspaceAccess.Write,
            }).ToArray(),
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
        // Unique BaseUrl per call so seeding TWO repos for one team (a multi-repo run) doesn't collide on the
        // (team, provider, url) unique index; the integrator uses the repo's CloneUrlHttps, not this base url.
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

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
