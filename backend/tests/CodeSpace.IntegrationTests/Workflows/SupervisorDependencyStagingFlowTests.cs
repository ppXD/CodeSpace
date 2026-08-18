using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): the S1 handoff — driven through the REAL <see cref="RealSupervisorActionExecutor"/>
/// against real Postgres, with REAL producer agents (real <see cref="AgentRunExecutor"/> + a real local bare git
/// remote) so the producers' <see cref="PublishManifest"/> rows carry GENUINE branches/patches, never hand-faked
/// ones. Proves the root cause of run 28fec923 is closed: a dependent subtask's clone ref is resolved from its
/// producer(s)' recorded manifest, never a fresh clone of the repository's default branch.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorDependencyStagingFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the coordinated feature";

    private readonly PostgresFixture _fixture;

    public SupervisorDependencyStagingFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_single_producer_with_a_pushed_branch_stages_the_dependent_from_that_branch()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (producerRunId, _) = await RunProducerAsync(teamId, repoId, "printf 'by producer\\n' > producer.txt; echo edited");
        var manifest = await SingleManifestAsync(producerRunId, teamId);
        manifest.Branch.ShouldNotBeNull("PublishMode=Branch + a bound credential → PR-2's default-on push actually pushed");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),
            priorSpawns: await SucceededSpawn(teamId, ("producer", producerRunId)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        task.Workspace.ShouldNotBeNull("a dependency handoff pins an explicit clone ref");
        task.Workspace!.Repositories.Single().Ref.ShouldBe(manifest.Branch, "the dependent clones the producer's OWN branch, never the repository default");
        task.Goal.ShouldContain(manifest.Branch!, customMessage: "the server-authored handoff block names the producer's branch in the agent's prompt");
    }

    /// <summary>
    /// The S1 probe must measure the HANDOFF, not the harness lottery. Which harness a live-brain run dispatches is
    /// the MODEL's choice (an authored <c>agents[].harness</c>, or an Anthropic-defaulted team pool that
    /// <c>HarnessModelReconciler</c> reconciles to claude-code on its own) — so a fake that stubs only codex leaves
    /// the dependent running the REAL claude CLI, which can never write <see cref="DependencyHandoffFakeCli.DependentMarker"/>.
    /// That made the live arm's hard assert UNSATISFIABLE while it blamed "a CODE regression in the dependency-staging
    /// resolver" (real-model run 30775218538: the one codex agent ran FIRST, the three dependents ran claude-code).
    /// Mutation check: restore the codex-only stub in <see cref="DependencyHandoffFakeCli"/> and the claude-code row
    /// goes RED.
    /// </summary>
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("claude-code")]
    public async Task The_handoff_markers_are_recorded_under_whichever_harness_the_model_dispatches(string harnessKind)
    {
        if (!await GitAvailableAsync()) return;

        using var cli = new DependencyHandoffFakeCli();

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);

        var producer = await RunFakeCliAgentAsync(teamId, repoId, harnessKind, checkoutRef: null);
        producer.Result.ChangedFiles.ShouldContain(DependencyHandoffFakeCli.ProducerMarker,
            $"the FIRST agent under '{harnessKind}' must run the fake and write the producer marker — if it did not, the harness ran a REAL CLI and this probe measures nothing");

        var producerBranch = (await SingleManifestAsync(producer.AgentRunId, teamId)).Branch;
        producerBranch.ShouldNotBeNull("PublishMode=Branch + a bound credential → the producer's work was pushed");

        var dependent = await RunFakeCliAgentAsync(teamId, repoId, harnessKind, checkoutRef: producerBranch);
        dependent.Result.ChangedFiles.ShouldContain(DependencyHandoffFakeCli.DependentMarker,
            $"a clone staged at the producer's branch already carries the producer marker, so under '{harnessKind}' the fake must take its dependent branch — this is the exact signal the live S1 arm asserts on");
    }

    /// <summary>
    /// The refusal, driven through the REAL executor — the only place the discriminator actually lives. A unit test
    /// on the reader cannot pin this: the first attempt at this refusal asked the dependency FRONTIER instead of the
    /// clamp's stamp, shipped, and did nothing, and reader-level tests stayed green throughout because they never
    /// exercised the branch.
    ///
    /// <para>Both rows are empty spawns under a plan that DECLARES AN EDGE — the shape the frontier version got
    /// wrong. Only the stamped one is excused: the clamp writes it exactly when it withheld something, so its
    /// absence means the model itself named no units.</para>
    /// </summary>
    [Theory]
    [InlineData(false, true)]   // no stamp → the model named nothing → REFUSED
    [InlineData(true, false)]   // the clamp emptied it → accepted, as before
    public async Task An_empty_spawn_is_refused_only_when_the_clamp_did_not_empty_it(bool clampEmptiedIt, bool expectRefusal)
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = ContextWith(runId, teamId, Guid.NewGuid(),
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),   // a real edge ⇒ the frontier reports blocked
            priorSpawns: await SucceededSpawn(teamId));

        var payload = clampEmptiedIt
            ? """{"subtaskIds":[],"deferredSubtaskIds":["dependent"]}"""
            : """{"subtaskIds":[]}""";

        using var scope = _fixture.BeginScope();
        var execution = await scope.Resolve<ISupervisorActionExecutor>().ExecuteAsync(
            new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = payload }, context, CancellationToken.None);

        var reason = SupervisorOutcome.ReadRejectionReason(execution.OutcomeJson);

        if (expectRefusal)
        {
            reason.ShouldNotBeNull("the model named no subtaskIds and the clamp withheld nothing — refusing is the only way it ever learns");
            reason!.ShouldContain("named no subtaskIds");
        }
        else
        {
            reason.ShouldBeNull("the SERVER emptied this spawn — accusing the model would send it to fix a defect it did not commit");
        }

        SupervisorOutcome.ReadStagedAgentCount(execution.OutcomeJson).ShouldBe(0, "either way nothing is staged — only the attribution differs");
    }

    /// <summary>
    /// The dual-dialect fake must fold the SAME Summary under both harnesses — a property that does NOT follow from
    /// emitting a 1:1 event sequence, because the two harnesses disagree on precedence: Codex takes
    /// <c>FinalSummary ?? AssistantMessage</c> (skipping Completed) while Claude takes
    /// <c>FinalSummary ?? Completed ?? AssistantMessage</c>, and neither can emit a FinalSummary. A claude
    /// <c>result</c> line saying "completed" therefore folds "completed" where codex folds the agent message.
    ///
    /// <para>Harmless for this fake (its consumers read ChangedFiles) but the trap for every fake this pattern gets
    /// ported to — <c>LiveBrainConflictFakeCli</c>'s RESOLUTION_VERIFIED and <c>ReviewVerdictFakeCli</c>'s VERDICT:
    /// payload are both read OFF THE SUMMARY, so porting the pattern without this fix would silently erase them.
    /// Pinning it here, on the reference implementation, is what makes that port safe.</para>
    /// </summary>
    [Fact]
    public async Task The_two_dialects_fold_the_same_summary()
    {
        if (!await GitAvailableAsync()) return;

        using var cli = new DependencyHandoffFakeCli();

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);

        var codex = await RunFakeCliAgentAsync(teamId, repoId, "codex-cli", checkoutRef: null);
        var claude = await RunFakeCliAgentAsync(teamId, repoId, "claude-code", checkoutRef: null);

        codex.Result.Summary.ShouldNotBeNullOrWhiteSpace("a fake that folds no summary would make this comparison vacuous");
        claude.Result.Summary.ShouldBe(codex.Result.Summary,
            "the two dialects must fold the SAME summary — Claude prefers the Completed event's `result` text while Codex skips Completed entirely, so the claude result line must echo the codex agent_message rather than say 'completed'");
    }

    /// <summary>
    /// The one staging arm with no coverage at any tier: a producer that genuinely SUCCEEDED but recorded nothing
    /// this repository can hand off. A manifest row is written only when there is something to record
    /// (<c>AgentRunExecutor.HasPublishCapture</c>: changed files, a patch artifact, or a pushed branch), so a
    /// no-op producer — an investigate-only unit, or one whose work landed in a different repo — leaves the
    /// dependency satisfied with zero manifests behind it.
    ///
    /// <para>The correct behaviour is to FALL THROUGH to the repository default branch: there is genuinely nothing
    /// to inherit, so this is the one silent no-override that is not a defect. Pinning it matters in both
    /// directions — it must not harden into a BLOCK (which would strand every dependent of a legitimately
    /// no-op unit), and it must not reach past the empty manifest set for some other ref.</para>
    /// </summary>
    [Fact]
    public async Task A_producer_that_succeeded_but_recorded_nothing_leaves_the_dependent_on_the_default_branch()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // Succeeds, touches nothing — the exact shape that satisfies the dependency gate while writing no manifest.
        var (producerRunId, _) = await RunProducerAsync(teamId, repoId, "echo 'investigated, changed nothing'");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(producerRunId, teamId, CancellationToken.None))
                .ShouldBeEmpty("a no-op producer records no manifest — the precondition this test exists to exercise; if this ever becomes non-empty the test is silently covering a different gate");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),
            priorSpawns: await SucceededSpawn(teamId, ("producer", producerRunId)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        task.Workspace.ShouldBeNull("nothing was produced to hand off, so the dependent legitimately clones the repository default branch — this arm must stay a fall-through, never a block");
        task.Goal.ShouldNotContain("building on prior work", Case.Insensitive, "no handoff block may claim inherited work that does not exist");
    }

    [Fact]
    public async Task A_single_patch_only_producer_still_hands_off_via_integration()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        // PatchOnly blocks the push (PR-2's RepositoryPolicyPublishGuard) — the producer's work lives ONLY in its recorded patch.
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.PatchOnly);
        var runId = await SeedSupervisorRunAsync(teamId);

        // A diff over the 8KB inline-offload threshold, so the manifest genuinely carries a PatchArtifactId (a
        // small diff never gets offloaded — the artifact-store round-trip is what this test proves).
        var (producerRunId, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'x' > patch-only.txt; echo edited");
        var manifest = await SingleManifestAsync(producerRunId, teamId);
        manifest.Branch.ShouldBeNull("the repo policy blocked the push");
        manifest.PatchArtifactId.ShouldNotBeNull("the diff exceeded the inline-offload threshold, so it was captured as an artifact");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),
            priorSpawns: await SucceededSpawn(teamId, ("producer", producerRunId)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        var integratedRef = task.Workspace!.Repositories.Single().Ref!;
        integratedRef.ShouldNotBe(manifest.Branch);

        (await remote.FileOnBranchAsync(integratedRef, "patch-only.txt")).Trim().Length.ShouldBe(9000,
            "the producer's RECORDED PATCH (resolved back from the artifact store) was applied onto a fresh integration branch even though it never pushed a branch of its own");
    }

    /// <summary>
    /// The size-gated twin of the test above, and the one the handoff used to lose: patch offload is SIZE-gated
    /// (<c>ArtifactOffloader.OffloadIfLargeAsync</c> returns no artifact id at or below the 8KB inline threshold), so a
    /// SMALL diff exists nowhere but the producer's own <c>agent_run.result_jsonb</c>. Staging read only the manifest's
    /// <c>PatchArtifactId</c> and passed a hard-coded empty inline argument, so every sub-8KB patch-only producer
    /// resolved to an empty patch and blocked the whole spawn — the exact case a cheap one-file fix produces.
    /// </summary>
    [Fact]
    public async Task A_small_patch_only_producer_stages_from_the_inline_patch_the_manifest_never_carries()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.PatchOnly);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (producerRunId, _) = await RunProducerAsync(teamId, repoId, "printf 'by a small producer\\n' > small.txt; echo edited");
        var manifest = await SingleManifestAsync(producerRunId, teamId);
        manifest.Branch.ShouldBeNull("the repo policy blocked the push");
        manifest.PatchArtifactId.ShouldBeNull("a sub-threshold diff is never offloaded — the precondition this test exists to exercise; if this ever becomes non-null the test is silently covering the artifact path instead");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),
            priorSpawns: await SucceededSpawn(teamId, ("producer", producerRunId)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        task.Workspace.ShouldNotBeNull("a producer whose only surviving artifact is an inline patch still has real work to hand off — blocking here strands every dependent of a small diff");

        (await remote.FileOnBranchAsync(task.Workspace!.Repositories.Single().Ref!, "small.txt")).Trim()
            .ShouldBe("by a small producer", "the producer's INLINE patch was applied onto a fresh integration branch");
    }

    /// <summary>
    /// The multi-producer shape the size gate breaks: the integrator is all-or-nothing, so ONE contribution that
    /// resolves to no patch aborts the whole set (<c>LocalGitBranchIntegrator.Preflight</c>) and blocks the spawn.
    /// Mixing one sub-threshold and one offloaded producer therefore proves the two patch sources compose — reading
    /// only the artifact half would leave this permanently blocked no matter how large the other diff is.
    /// </summary>
    [Fact]
    public async Task A_small_and_an_offloaded_producer_integrate_together()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (small, _) = await RunProducerAsync(teamId, repoId, "printf 'tiny\\n' > small.txt; echo edited");
        var (large, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'q' > large.txt; echo edited");

        (await SingleManifestAsync(small, teamId)).PatchArtifactId.ShouldBeNull("the small producer's diff stayed inline");
        (await SingleManifestAsync(large, teamId)).PatchArtifactId.ShouldNotBeNull("the large producer's diff was offloaded");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("small", null), ("large", null), ("dependent", new[] { "small", "large" })),
            priorSpawns: await SucceededSpawn(teamId, ("small", small), ("large", large)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        var integratedRef = task.Workspace!.Repositories.Single().Ref!;

        (await remote.FileOnBranchAsync(integratedRef, "small.txt")).Trim().ShouldBe("tiny", "the inline-patch producer contributed alongside the offloaded one — one empty contribution would have aborted the whole set");
        (await remote.FileOnBranchAsync(integratedRef, "large.txt")).Trim().ShouldBe(new string('q', 9000));
    }

    /// <summary>
    /// The shared seam itself, driven directly against real producers instead of through staging: whichever carrier a
    /// producer's diff actually landed in, <see cref="IAgentPatchReader"/> returns THAT one and only that one. The two
    /// producers run the identical production capture path with only the diff SIZE changed, which is the whole
    /// variable — the offload gate decides the carrier, and a reader that knows one carrier is blind to half of them.
    /// </summary>
    [Fact]
    public async Task The_shared_patch_reader_returns_whichever_carrier_the_offload_gate_chose()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.PatchOnly);

        var (small, _) = await RunProducerAsync(teamId, repoId, "printf 'inline only\\n' > small.txt; echo edited");
        var (large, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'z' > large.txt; echo edited");

        var inlineSource = await PatchSourceOfAsync(small, teamId);
        var offloadedSource = await PatchSourceOfAsync(large, teamId);

        inlineSource.PatchArtifactId.ShouldBeNull("the sub-threshold diff was not offloaded — the carrier this seam exists to reach");
        offloadedSource.PatchArtifactId.ShouldNotBeNull();

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IAgentPatchReader>();

        (await reader.ReadAsync(teamId, inlineSource, CancellationToken.None))
            .ShouldContain("small.txt", customMessage: "no artifact id ⇒ the diff lives in the producing run's result, which is exactly what the manifest cannot tell you");

        (await reader.ReadAsync(teamId, offloadedSource, CancellationToken.None))
            .ShouldContain("large.txt", customMessage: "an artifact id ⇒ the artifact store holds the whole diff — unchanged from the pre-fix behaviour");

        (await reader.ReadAsync(Guid.NewGuid(), inlineSource, CancellationToken.None))
            .ShouldBeEmpty("the inline read is team-scoped — another team's id must never resolve this run's diff");
    }

    /// <summary>The producer's manifest row projected onto the reader's coordinates, exactly as dependency staging projects it.</summary>
    private async Task<AgentPatchSource> PatchSourceOfAsync(Guid agentRunId, Guid teamId)
    {
        var manifest = await SingleManifestAsync(agentRunId, teamId);

        return new AgentPatchSource { AgentRunId = manifest.AgentRunId, RepositoryAlias = manifest.RepositoryAlias, PatchArtifactId = manifest.PatchArtifactId };
    }

    [Fact]
    public async Task Two_disjoint_producers_integrate_onto_one_branch_the_dependent_clones()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // The integrator is PATCH-based (a pushed branch is informational-only, never fetched from) — each diff must
        // exceed the 8KB inline-offload threshold so the manifest genuinely carries a PatchArtifactId to integrate from.
        var (p1, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'p' > p1.txt; echo edited");
        var (p2, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'q' > p2.txt; echo edited");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("p1", null), ("p2", null), ("dependent", new[] { "p1", "p2" })),
            priorSpawns: await SucceededSpawn(teamId, ("p1", p1), ("p2", p2)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        var integratedRef = task.Workspace!.Repositories.Single().Ref!;

        (await remote.FileOnBranchAsync(integratedRef, "p1.txt")).Trim().ShouldBe(new string('p', 9000), "both disjoint producers' changes are combined onto the one branch the dependent clones");
        (await remote.FileOnBranchAsync(integratedRef, "p2.txt")).Trim().ShouldBe(new string('q', 9000));
    }

    /// <summary>
    /// The P1 live-run break, at the only tier that can show it: TWO dependents staged in the SAME turn over
    /// DIFFERENT producer sets. With the handoff branch keyed on run + turn alone both asked for one name, so the
    /// second integration found a remote branch carrying the first's (different) tree,
    /// <c>LocalGitBranchIntegrator.ReconcileExistingBranchAsync</c> correctly refused to clobber it, and
    /// <c>Spawn</c>'s correct abort-on-blocked rule staged ZERO agents — including <c>d1</c>, whose own staging
    /// had already completed cleanly. None of those rules changed; the manufactured collision did.
    ///
    /// <para>Mutation check: revert the branch name to <c>…/turn{N}</c> and this goes RED with zero staged tasks.
    /// No unit test can reach it — the collision only exists once a real integrator really pushes a real ref.</para>
    /// </summary>
    [Fact]
    public async Task Two_dependents_over_different_producer_sets_both_stage_in_one_turn()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // Each dependent needs ≥2 producers to reach the INTEGRATION arm (a lone branch-producer takes the verbatim
        // branch and never touches the integrator), and each diff must clear the 8KB inline-offload threshold.
        var (p1, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'a' > p1.txt; echo edited");
        var (p2, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'b' > p2.txt; echo edited");
        var (p3, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'c' > p3.txt; echo edited");
        var (p4, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'd' > p4.txt; echo edited");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("p1", null), ("p2", null), ("p3", null), ("p4", null), ("d1", new[] { "p1", "p2" }), ("d2", new[] { "p3", "p4" })),
            priorSpawns: await SucceededSpawn(teamId, ("p1", p1), ("p2", p2), ("p3", p3), ("p4", p4)));

        await ExecuteSpawnAsync(context, new[] { "d1", "d2" });

        var tasks = await StagedTasksAsync(runId);
        tasks.Count.ShouldBe(2, "both dependents stage — a collision on one branch name blocked the turn's spawn entirely, staging neither");

        var d1Ref = tasks.Single(t => t.SubtaskId == "d1").Workspace!.Repositories.Single().Ref!;
        var d2Ref = tasks.Single(t => t.SubtaskId == "d2").Workspace!.Repositories.Single().Ref!;

        d1Ref.ShouldNotBe(d2Ref, "two different producer sets integrate to two different trees, so they must never contend for one ref");

        (await remote.FileOnBranchAsync(d1Ref, "p1.txt")).Trim().ShouldBe(new string('a', 9000), "d1's branch carries exactly ITS producers' work");
        (await remote.FileOnBranchAsync(d1Ref, "p2.txt")).Trim().ShouldBe(new string('b', 9000));
        (await remote.FileOnBranchAsync(d2Ref, "p3.txt")).Trim().ShouldBe(new string('c', 9000), "d2's branch carries exactly ITS producers' work");
        (await remote.FileOnBranchAsync(d2Ref, "p4.txt")).Trim().ShouldBe(new string('d', 9000));
    }

    /// <summary>
    /// The idempotent-re-push pin at the real-git tier: two dependents whose producer sets are EQUAL but DECLARED IN
    /// OPPOSITE ORDER. The digest is taken over the sorted set, so both resolve the same NAME; these two producers
    /// touch disjoint files, so the second integration reproduces the identical tree, and the UNCHANGED no-clobber
    /// reconcile short-circuits on tree equality instead of refusing.
    ///
    /// <para>Both failure modes this fix must not introduce are visible here: an order-SENSITIVE digest forks a
    /// second branch (the remote-branch count goes to 2), and a discriminator that broke idempotence — anything
    /// per-dependent, or a per-process hash — makes the second dependent's push a clobber refusal, which blocks the
    /// spawn and leaves ZERO staged tasks.</para>
    ///
    /// <para>What this does NOT pin, because it is not true: that INTEGRATING a set is order-invariant.
    /// <c>LocalGitBranchIntegrator.ApplyAllAsync</c> applies in the declared order via <c>git apply --3way</c>, which
    /// does not commute in general — these two producers write disjoint new files, i.e. the case that commutes by
    /// construction. A non-commuting set presented in two orders shares this name and then diverges, at which point
    /// the tree-equality reconcile refuses the second push and the staging BLOCKS — today's behaviour under the
    /// per-turn name, and never a graft. Pinning that case needs its own row (a producer renaming a file another
    /// appends to); it is not pinned here and this comment must not be read as if it were.</para>
    /// </summary>
    [Fact]
    public async Task Two_dependents_over_the_same_producer_set_share_one_branch_and_the_second_push_short_circuits()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (p1, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'a' > p1.txt; echo edited");
        var (p2, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'b' > p2.txt; echo edited");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("p1", null), ("p2", null), ("d1", new[] { "p1", "p2" }), ("d2", new[] { "p2", "p1" })),
            priorSpawns: await SucceededSpawn(teamId, ("p1", p1), ("p2", p2)));

        await ExecuteSpawnAsync(context, new[] { "d1", "d2" });

        var tasks = await StagedTasksAsync(runId);
        tasks.Count.ShouldBe(2, "the second dependent re-pushed the IDENTICAL tree under the identical name — the reconcile short-circuits on that, it does not refuse it");

        var d1Ref = tasks.Single(t => t.SubtaskId == "d1").Workspace!.Repositories.Single().Ref!;
        tasks.Single(t => t.SubtaskId == "d2").Workspace!.Repositories.Single().Ref.ShouldBe(d1Ref, "the same producers in a different declared order inherit the same work, so they legitimately share one branch");

        (await remote.BranchesAsync()).Count(b => b.StartsWith("codespace/handoff/", StringComparison.Ordinal))
            .ShouldBe(1, "exactly ONE handoff branch exists on the remote — an order-sensitive digest would have forked a second, redundant one for the identical set");
    }

    [Fact]
    public async Task Two_conflicting_producers_block_the_spawn_and_the_existing_resolve_verb_can_reconcile_it()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync("shared.txt", "original\n");
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // Each producer replaces the WHOLE file with a large, mutually-conflicting blob — over the 8KB inline-offload
        // threshold, so both manifests genuinely carry a PatchArtifactId the integrator can apply (patch-based, never
        // fetched from the pushed branch), and the two patches conflict on the very same lines.
        var (p1, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'p' > shared.txt; echo edited");
        var (p2, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'q' > shared.txt; echo edited");

        var priorSpawn = await SucceededSpawn(teamId, ("p1", p1), ("p2", p2));
        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("p1", null), ("p2", null), ("dependent", new[] { "p1", "p2" })),
            priorSpawns: priorSpawn);

        var spawnDecision = await ExecuteSpawnAsync(context, "dependent");

        (await StagedAgentRunsAsync(runId)).ShouldBeEmpty("a conflict-blocked spawn stages ZERO agents — never a partial fan-out");

        var integration = SupervisorOutcome.ReadIntegration(spawnDecision.OutcomeJson);
        integration.ShouldNotBeNull();
        integration!.IsConflicted.ShouldBeTrue();
        integration.ConflictedFiles.ShouldContain("shared.txt");

        var p1Manifest = await SingleManifestAsync(p1, teamId);
        var p2Manifest = await SingleManifestAsync(p2, teamId);
        // All-or-nothing apply: the FIRST contribution (p1) applies cleanly in the trial before the SECOND (p2) hits
        // the textual conflict and the whole set rolls back — only the contribution that actually conflicted gets a
        // FallbackBranch (mirrors LocalGitBranchIntegratorFlowTests' own conflict assertions, which check the SAME
        // shape). The resolver's actual reconciliation input (asserted below) is unaffected either way — it reads
        // EVERY prior spawn's branches via CollectAgentBranches, not just this set.
        integration.PreservedBranches.ShouldContain(p2Manifest.Branch!, customMessage: "the contribution that actually hit the textual conflict is preserved for review");

        // The blocked SPAWN decision (not a merge) is now on the tape — resolve must reconcile it via the SAME
        // widened conflict reader, proving "conflicts → the EXISTING resolve verb" is genuinely wired, not just documented.
        var resolveContext = context with { PriorDecisions = new[] { priorSpawn, spawnDecision } };
        await ExecuteResolveAsync(resolveContext);

        var resolverTasks = await StagedTasksAsync(runId);
        resolverTasks.Count.ShouldBe(1, "resolve stages exactly ONE resolver agent (the K=1 shape)");
        resolverTasks[0].Goal.ShouldContain(p1Manifest.Branch!, customMessage: "the resolver's goal names BOTH conflicting producers' branches — assembled deterministically, not model-authored");
        resolverTasks[0].Goal.ShouldContain(p2Manifest.Branch!);
    }

    [Fact]
    public async Task A_producer_manifest_missing_a_patch_blocks_the_spawn_never_silently_defaulting()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // A defensive, should-never-happen state per I1: a manifest row recording a diff but with NO branch, NO patch
        // artifact — and no agent run at all behind it, so no inline patch either. Seeded directly (bypassing the
        // normal capture path) to prove the fail-closed guard now that a sub-threshold inline patch is a real carrier.
        var producerRunId = Guid.NewGuid();
        await SeedAnomalousManifestAsync(teamId, producerRunId, repoId);

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),
            priorSpawns: await SucceededSpawn(teamId, ("producer", producerRunId)));

        var spawnDecision = await ExecuteSpawnAsync(context, "dependent");

        (await StagedAgentRunsAsync(runId)).ShouldBeEmpty("a missing-patch manifest blocks the spawn rather than silently cloning the repository default");

        using var doc = JsonDocument.Parse(spawnDecision.OutcomeJson!);
        doc.RootElement.GetProperty("blockedSubtasks").EnumerateArray().Single().GetProperty("reason").GetString()
            .ShouldContain("no branch, no patch artifact and no inline patch", customMessage: "the loud reason names every carrier that was actually consulted — the old wording implied a patch might exist that staging simply never read, which for a sub-threshold diff was exactly true");
    }

    [Fact]
    public async Task A_base_subtask_id_dispatch_override_narrows_a_multi_dependency_subtask_to_one_producer()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (p1, _) = await RunProducerAsync(teamId, repoId, "printf 'from p1\\n' > p1.txt; echo edited");
        var (p2, _) = await RunProducerAsync(teamId, repoId, "printf 'from p2\\n' > p2.txt; echo edited");
        var p1Manifest = await SingleManifestAsync(p1, teamId);

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("p1", null), ("p2", null), ("dependent", new[] { "p1", "p2" })),
            priorSpawns: await SucceededSpawn(teamId, ("p1", p1), ("p2", p2)));

        // The plan declares BOTH as dependencies, but the model's per-agent dispatch narrows this spawn to p1 only.
        await ExecuteSpawnAsync(context, "dependent", agents: new[] { new SupervisorAgentDispatch { SubtaskId = "dependent", BaseSubtaskId = "p1" } });

        var task = await SingleStagedTaskAsync(runId);
        task.Workspace!.Repositories.Single().Ref.ShouldBe(p1Manifest.Branch, "the BaseSubtaskId override wins over the plan's two-producer DependsOn — no integration, just p1's own branch");
    }

    // ─── Drive the real executor ──────────────────────────────────────────────────

    /// <summary>Execute a Spawn decision through the real executor, returning it as a TERMINAL <see cref="SupervisorPriorDecision"/> — ready to both inspect (OutcomeJson) and feed back in as a later turn's prior tape (e.g. for resolve to read its conflict).</summary>
    private Task<SupervisorPriorDecision> ExecuteSpawnAsync(SupervisorTurnContext context, string subtaskId, IReadOnlyList<SupervisorAgentDispatch>? agents = null) => ExecuteSpawnAsync(context, new[] { subtaskId }, agents);

    /// <summary>The K-at-once shape: ONE turn's spawn fanning out over several subtask ids — the only way to exercise what two dependents staged in the SAME turn do to each other.</summary>
    private async Task<SupervisorPriorDecision> ExecuteSpawnAsync(SupervisorTurnContext context, IReadOnlyList<string> subtaskIds, IReadOnlyList<SupervisorAgentDispatch>? agents = null)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = subtaskIds, Agents = agents }, AgentJson.Options);
        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = payload };

        var execution = await executor.ExecuteAsync(decision, context, CancellationToken.None);

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = payload, OutcomeJson = execution.OutcomeJson,
        };
    }

    private async Task ExecuteResolveAsync(SupervisorTurnContext context)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Resolve, PayloadJson = "{}" };

        await executor.ExecuteAsync(decision, context, CancellationToken.None);
    }

    private async Task<AgentTask> SingleStagedTaskAsync(Guid runId) => (await StagedTasksAsync(runId)).ShouldHaveSingleItem();

    private async Task<IReadOnlyList<AgentTask>> StagedTasksAsync(Guid runId) =>
        (await StagedAgentRunsAsync(runId)).Select(r => JsonSerializer.Deserialize<AgentTask>(r.TaskJson, AgentJson.Options)!).ToList();

    private async Task<IReadOnlyList<AgentRun>> StagedAgentRunsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.NodeId == NodeId)
            .ToListAsync();
    }

    // ─── Context / decision-tape builders ─────────────────────────────────────────

    private static SupervisorTurnContext ContextWith(Guid runId, Guid teamId, Guid repositoryId, SupervisorPriorDecision plan, SupervisorPriorDecision priorSpawns) => new()
    {
        Goal = Goal,
        SupervisorRunId = runId,
        TeamId = teamId,
        NodeId = NodeId,
        TurnNumber = 2,
        PriorDecisions = new[] { plan, priorSpawns },
        AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = repositoryId },
    };

    private static SupervisorPriorDecision Plan(params (string Id, string[]? DependsOn)[] subtasks)
    {
        var payload = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = Goal,
            Subtasks = subtasks.Select(s => new SupervisorPlannedSubtask { Id = s.Id, Title = s.Id, Instruction = $"do {s.Id}", DependsOn = s.DependsOn }).ToList(),
        }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}" };
    }

    /// <summary>
    /// A prior Spawn decision recording each (subtaskId, REAL agentRunId) pair as a Succeeded producer — the
    /// positional subtaskIds[i] ↔ agentResults[i] shape <see cref="SupervisorDependencyGate"/> reads. Each entry's
    /// <c>ProducedBranch</c> is resolved off the producer's REAL manifest (null for a patch-only producer) — the
    /// resolver's OWN branch-collection (<c>CollectAgentBranches</c>) reads this same field, so a decision built
    /// without it would make <c>resolve</c> see no branches to reconcile even after a genuine conflict.
    /// </summary>
    private async Task<SupervisorPriorDecision> SucceededSpawn(Guid teamId, params (string SubtaskId, Guid AgentRunId)[] producers)
    {
        var results = new List<SupervisorAgentResult>();
        foreach (var p in producers)
        {
            using var scope = _fixture.BeginScope();
            var manifests = await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(p.AgentRunId, teamId, CancellationToken.None);
            results.Add(new SupervisorAgentResult { AgentRunId = p.AgentRunId, Status = "Succeeded", ProducedBranch = manifests.FirstOrDefault()?.Branch });
        }

        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = producers.Select(p => p.SubtaskId).ToList() }, AgentJson.Options);
        var outcome = JsonSerializer.Serialize(new { agentRunIds = producers.Select(p => p.AgentRunId).ToArray(), agentCount = producers.Length, agentResults = results }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcome };
    }

    // ─── Real producer execution (real AgentRunExecutor + real git) ───────────────

    /// <summary>Run ONE real producer agent (a scripted /bin/sh harness) through the REAL AgentRunExecutor against the real repo — a genuine PublishManifest row + (PublishMode-dependent) a genuine pushed branch results. Returns its AgentRunId + the terminal ResultJson.</summary>
    private async Task<(Guid AgentRunId, string ResultJson)> RunProducerAsync(Guid teamId, Guid repositoryId, string script)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "produce", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness(script) }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness(script) }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactStore>(),
            scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IArtifactManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<IEnumerable<IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(run.Id, CancellationToken.None);

        var finished = await scope.Resolve<IAgentRunService>().GetAsync(run.Id, CancellationToken.None);
        finished.Status.ShouldBe(AgentRunStatus.Succeeded, "the producer must genuinely succeed for its manifest to carry real work");

        return (run.Id, finished.ResultJson!);
    }

    /// <summary>
    /// Run ONE agent on a REAL registered harness (codex-cli / claude-code) through the fully DI-wired production
    /// executor — unlike <see cref="RunProducerAsync"/>, which substitutes a <c>ScriptedHarness</c> and so can never
    /// observe which CLI a harness actually spawns. <paramref name="checkoutRef"/> pins the clone (null → the
    /// repository default), standing in for what dependency staging resolves at spawn time.
    /// </summary>
    private async Task<(Guid AgentRunId, AgentRunResult Result)> RunFakeCliAgentAsync(Guid teamId, Guid repositoryId, string harnessKind, string? checkoutRef)
    {
        using var scope = _fixture.BeginScope();

        var workspace = new WorkspaceSpec
        {
            Repositories = new[] { new WorkspaceRepositorySpec { Alias = "repo", RepositoryId = repositoryId, Ref = checkoutRef, IsPrimary = true } },
        };

        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "do the unit", Harness = harnessKind, Model = null, RepositoryId = repositoryId, Workspace = workspace },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(run.Id, CancellationToken.None);

        var finished = await scope.Resolve<IAgentRunService>().GetAsync(run.Id, CancellationToken.None);
        finished.Status.ShouldBe(AgentRunStatus.Succeeded,
            $"the '{harnessKind}' harness must have spawned the FAKE cli (a real CLI would need credentials this test never seeds) — error: {finished.Error}");

        return (run.Id, JsonSerializer.Deserialize<AgentRunResult>(finished.ResultJson!, AgentJson.Options)!);
    }

    private async Task<PublishManifest> SingleManifestAsync(Guid agentRunId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
    }

    /// <summary>Seed a manifest row that violates I1 (a diff recorded, but neither a branch nor a patch artifact) — bypassing the normal capture path to prove the staging resolver's fail-closed guard.</summary>
    private async Task SeedAnomalousManifestAsync(Guid teamId, Guid agentRunId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
            RepositoryAlias = "primary", BaseSha = "deadbeef", ChangedFileCount = 1, PublishStateValue = PublishState.PatchOnly,
        });

        await db.SaveChangesAsync();
    }

    // ─── Seeding (team / credential / repository / supervisor run) ────────────────

    private async Task<Guid> SeedTeamAsync() => (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

    private async Task<Guid> SeedCredentialAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "dependency-staging-e2e-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        await db.SaveChangesAsync();
        return credentialId;
    }

    private async Task<Guid> FindOrCreateProviderInstanceAsync(CodeSpaceDbContext db, Guid teamId)
    {
        var existing = await db.ProviderInstance.Where(p => p.TeamId == teamId).Select(p => p.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty) return existing;

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });
        await db.SaveChangesAsync();
        return instanceId;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrl, Guid credentialId, RepositoryPublishMode publishMode)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
            PublishMode = publishMode,
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var (_, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scopeAsAdmin = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scopeAsAdmin.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-dep-staging-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" } },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    // ─── Git helpers ────────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the agents' remote, plus ref inspection via <c>git --git-dir</c> — real-git ground truth. Mirrors <see cref="PublishGuardChainFlowTests.BareRemote"/>.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-dep-staging-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedWithOneCommitAsync(string fileName = "README.md", string content = "base")
        {
            await RunGitAsync(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await RunGitAsync(seed, "clone", _bare, seed);
            await RunGitAsync(seed, "config", "user.email", "test@codespace.dev");
            await RunGitAsync(seed, "config", "user.name", "Test");
            await RunGitAsync(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, fileName), content);
            await RunGitAsync(seed, "add", ".");
            await RunGitAsync(seed, "commit", "-m", "seed");
            await RunGitAsync(seed, "push", "origin", "main");
        }

        public Task<string> FileOnBranchAsync(string branch, string file) => RunGitAsync(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        /// <summary>Every branch the remote actually carries — real-git ground truth for "how many handoff branches did this turn create".</summary>
        public async Task<IReadOnlyList<string>> BranchesAsync() =>
            (await RunGitAsync(_root, "--git-dir", _bare, "for-each-ref", "--format=%(refname:short)", "refs/heads")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static async Task<string> RunGitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A CLI-less test harness: builds a /bin/sh invocation from a fixed script. Mirrors <see cref="PublishGuardChainFlowTests.ScriptedHarness"/>.</summary>
    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });
    }
}
