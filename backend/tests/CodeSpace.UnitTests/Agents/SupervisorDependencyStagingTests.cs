using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the S1 handoff's PURE decision surface — which dependency a subtask's staging resolves against
/// (<see cref="RealSupervisorActionExecutor.DependsOnFor"/>) and the wire shape a staging BLOCK records
/// (<see cref="RealSupervisorActionExecutor.BuildBlockedSpawnOutcome"/>), round-tripped through the SAME
/// <see cref="SupervisorOutcome.ReadIntegration"/> a <c>merge</c> conflict is read through — the seam that
/// makes "a staging conflict is reconcilable by the EXISTING <c>resolve</c> verb" actually true. The async
/// manifest-read + real-git-integration half (<c>ResolveDependencyStagingAsync</c> / <c>IntegrateProducersAsync</c>)
/// is proven at the integration tier (real Postgres + a real bare git remote).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDependencyStagingTests
{
    // ── An empty staging verb is REFUSED, unless the server emptied it ──────────────────

    /// <summary>
    /// The decision schema requires only <c>kind</c>, so <c>{"kind":"spawn"}</c> with no spawn payload at all is
    /// schema-VALID; the projector substitutes an empty payload and the spawn used to be ACCEPTED, staging nothing
    /// and telling the model nothing. That dominated the decision-eval lane — every scenario looping plan→spawn×7
    /// into the turn cap with every spawn staging nothing. Its retry twin has always been refused
    /// (<see cref="RealSupervisorActionExecutor.BuildRejectedRetryOutcome"/>); this makes the pair symmetric, which
    /// is what lets the decider's existing correction block reach it.
    /// </summary>
    [Fact]
    public void A_spawn_naming_no_subtask_is_refused_in_the_same_shape_the_retry_twin_uses()
    {
        var spawn = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildRejectedSpawnOutcome(), AgentJson.Options);
        var retry = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildRejectedRetryOutcome(), AgentJson.Options);

        SupervisorOutcome.ReadRejectionReason(spawn).ShouldNotBeNull("the correction block the decider renders keys on this reader — an outcome it cannot read is invisible to the model");
        SupervisorOutcome.ReadRejectionReason(spawn)!.ShouldContain("named no subtaskIds");
        SupervisorOutcome.ReadStagedAgentCount(spawn).ShouldBe(0);

        SupervisorOutcome.ReadRejectionReason(retry).ShouldNotBeNull("the twin, unchanged — the two must stay symmetric or one of them silently stops being rendered");
    }

    /// <summary>
    /// The discriminator, pinned on the ONE fact that actually separates the two cases. The clamp writes
    /// <c>deferredSubtaskIds</c> exactly when it withheld something, and returns the payload untouched otherwise —
    /// so the key's presence IS "the server emptied this".
    ///
    /// <para>A first attempt asked the dependency FRONTIER ("a plan with nothing blocked cannot have been clamped")
    /// and silently did nothing: Blocked is every unfinished unit with an unmet edge, so ANY plan declaring an edge
    /// suppressed the refusal — which is every plan these scenarios author. That version shipped and run
    /// 31074294816 still recorded 120 accepted-empty spawns and zero refusals. The row below with a blocking edge
    /// but no stamp is the case it got wrong.</para>
    /// </summary>
    [Theory]
    [InlineData("""{"subtaskIds":[]}""", false)]
    [InlineData("""{"subtaskIds":[],"deferredSubtaskIds":[]}""", false)]
    [InlineData("""{"subtaskIds":[],"deferredSubtaskIds":["b"]}""", true)]
    [InlineData("""{"subtaskIds":["a"],"deferredSubtaskIds":["b"]}""", true)]
    [InlineData(null, false)]
    [InlineData("not json", false)]
    public void Only_the_clamps_own_stamp_marks_a_spawn_the_server_emptied(string? payloadJson, bool expectDeferred)
    {
        SupervisorOutcome.HasDeferredSubtasks(payloadJson).ShouldBe(expectDeferred);
    }

    [Fact]
    public void A_plan_with_a_blocking_edge_does_not_by_itself_excuse_an_empty_spawn()
    {
        // THE regression the frontier version shipped: an edge exists, so Frontier(...).Blocked is non-empty — yet
        // this spawn carries no stamp, so the clamp did not empty it and the model named nothing. It must be refused.
        var withEdge = ContextWithPlan(("a", null), ("b", new[] { "a" }));

        SupervisorDependencyGate.Frontier(withEdge).Blocked.ShouldNotBeEmpty("the plan does have a blocked unit");
        SupervisorOutcome.HasDeferredSubtasks("""{"subtaskIds":[]}""").ShouldBeFalse(
            "...but nothing was deferred INTO this spawn, so a blocked plan must not excuse a model that named no units");
    }

    private static SupervisorTurnContext ContextWithPlan(params (string Id, string[]? DependsOn)[] subtasks)
    {
        var payload = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = "g",
            Subtasks = subtasks.Select(x => new SupervisorPlannedSubtask { Id = x.Id, Title = x.Id, Instruction = "do", DependsOn = x.DependsOn }).ToList(),
        }, AgentJson.Options);

        return new SupervisorTurnContext
        {
            Goal = "g",
            SupervisorRunId = Guid.NewGuid(),
            TeamId = Guid.NewGuid(),
            NodeId = "sup",
            TurnNumber = 1,
            PriorDecisions = new[]
            {
                new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}" },
            },
        };
    }

    // ── DependencyStagingNoOpReason: every silent gate names itself ─────────────────────

    /// <summary>
    /// All three no-op arms return the SAME <see cref="DependencyStagingResult.NoOverride"/> singleton, so the
    /// only thing that can tell them apart after the fact is this reason. Each row must therefore name the gate it
    /// fell through — and, critically, be DISTINGUISHABLE from the others (pinned below). Ordering matters: a caller
    /// passes only the counts it has established so far, so an earlier gate must always win over a later one.
    /// </summary>
    [Theory]
    [InlineData(0, true, 0, 0, "declared no dependsOn edge")]                      // gate 1a — no edge
    [InlineData(2, false, 0, 0, "bound to no repository")]                         // gate 1b — no repo
    [InlineData(2, true, 0, 0, "non-rejected succeeded attempt")]                  // gate 2 — no usable producer
    [InlineData(2, true, 2, 0, "none recorded a publish manifest")]                // gate 3 — no manifest for this repo
    public void Each_silent_staging_gate_names_itself(int dependsOnCount, bool hasRepository, int producers, int manifests, string expected)
    {
        var dependsOn = Enumerable.Range(1, dependsOnCount).Select(i => $"dep{i}").ToList();
        var repositoryId = hasRepository ? Guid.NewGuid() : (Guid?)null;

        var reason = RealSupervisorActionExecutor.DependencyStagingNoOpReason(dependsOn, repositoryId, producers, manifests);

        reason.ShouldNotBeNull("a no-op arm that names no gate is exactly the state that made run 30775218538 undiagnosable");
        reason.ShouldContain(expected);
    }

    [Fact]
    public void Staging_proceeds_when_every_gate_is_satisfied()
    {
        RealSupervisorActionExecutor.DependencyStagingNoOpReason(new[] { "dep1" }, Guid.NewGuid(), 1, 1)
            .ShouldBeNull("a reason here would log a no-op for a handoff that actually resolved");
    }

    [Fact]
    public void The_four_gate_reasons_are_mutually_distinguishable()
    {
        var repo = Guid.NewGuid();
        var two = new[] { "dep1", "dep2" };

        var reasons = new[]
        {
            RealSupervisorActionExecutor.DependencyStagingNoOpReason(Array.Empty<string>(), repo, 0, 0),
            RealSupervisorActionExecutor.DependencyStagingNoOpReason(two, null, 0, 0),
            RealSupervisorActionExecutor.DependencyStagingNoOpReason(two, repo, 0, 0),
            RealSupervisorActionExecutor.DependencyStagingNoOpReason(two, repo, 2, 0),
        };

        reasons.Distinct().Count().ShouldBe(4, "two gates sharing a reason string are two gates a forensic reader cannot tell apart — the whole point of naming them");
    }

    // ── The producer patch source: staging must read BOTH carriers, never a hard-coded empty one ──

    [Fact]
    public void A_producers_patch_source_carries_every_coordinate_the_shared_reader_needs()
    {
        var agentRunId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var source = RealSupervisorActionExecutor.PatchSourceFor(new PublishManifest
        {
            AgentRunId = agentRunId, RepositoryAlias = "web", PatchArtifactId = artifactId,
        });

        source.AgentRunId.ShouldBe(agentRunId, "dropping the run id silently degrades every sub-threshold producer back to patch-less");
        source.RepositoryAlias.ShouldBe("web", "the alias selects the matching per-repo entry of a multi-repo result — 'primary' would read the wrong repository's diff");
        source.PatchArtifactId.ShouldBe(artifactId);
    }

    /// <summary>
    /// The regression pin (drift detector, Rule 12.5 shape): staging used to build every contribution with
    /// <c>ResolveRequiredAsync(context.TeamId, "", producer.PatchArtifactId, …)</c> — a HARD-CODED empty inline
    /// argument. Because patch offload is size-gated, that resolved every sub-8KB producer to an empty patch, which
    /// <c>LocalGitBranchIntegrator</c> correctly refuses and <c>Spawn</c> correctly turns into a whole-turn abort:
    /// one small diff anywhere in a multi-producer handoff blocked all of it.
    ///
    /// <para>Reverting to that shape leaves every unit test on the pure surfaces green (they never reach the
    /// contribution build) and only the real-Postgres integration tier red — so this cheap source-level pin exists to
    /// fail in the same second the literal comes back, on a machine with no database at all.</para>
    /// </summary>
    [Fact]
    public void Dependency_staging_never_resolves_a_producers_patch_from_a_hard_coded_empty_inline_argument()
    {
        var source = File.ReadAllText(LocateDependencyStagingSource());

        HardCodedEmptyInlinePatch.IsMatch(source).ShouldBeFalse(
            "a producer's diff must be resolved through the shared IAgentPatchReader (artifact OR the producing run's inline patch), never by handing the offloader an empty inline argument and reading the manifest's artifact id alone");

        source.ShouldContain(nameof(AgentPatchSource),
            customMessage: "the contribution build must go through the shared patch seam — if this identifier is gone, staging has grown its own private patch resolution again");
    }

    /// <summary>Matches <c>ResolveRequiredAsync(&lt;anything&gt;, "", …)</c>. In a verbatim string a doubled quote is ONE literal quote, so <c>""""</c> is the empty-string argument.</summary>
    private static readonly Regex HardCodedEmptyInlinePatch = new(@"ResolveRequiredAsync\([^,)]+,\s*""""\s*,", RegexOptions.Compiled);

    private static string LocateDependencyStagingSource()
    {
        const string relative = "backend/src/CodeSpace.Core/Services/Supervisor/Executors/RealSupervisorActionExecutor.DependencyStaging.cs";

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"{relative} not found walking up from {AppContext.BaseDirectory}");
    }

    // ── HandoffIntegrationBranch: one branch per PRODUCER SET, never per turn ───────────

    private static readonly Guid Run = Guid.Parse("3f4b1c2d-0000-4000-8000-abcdefabcdef");
    private static readonly Guid Repo = Guid.Parse("11111111-2222-4333-8444-555555555555");

    /// <summary>
    /// The P1 collision. Keyed on run + turn alone, EVERY dependent staged in one turn asked for the SAME branch
    /// name — so the second one's integration found a remote branch carrying the FIRST one's (different) tree, and
    /// <c>LocalGitBranchIntegrator.ReconcileExistingBranchAsync</c> correctly refused to clobber it. That refusal is
    /// a non-Clean staging, which <c>Spawn</c> correctly turns into a whole-turn abort — so a manufactured name
    /// collision staged ZERO agents, including the dependent whose own staging was clean.
    ///
    /// <para>Every row is a set that DIFFERS from <c>{p1, p2}</c> in some way — disjoint, overlapping, a strict
    /// subset, a strict superset — because each of those integrates to a different tree and so must never contend
    /// for one ref.</para>
    /// </summary>
    [Theory]
    [InlineData("p3", "p4")]         // disjoint
    [InlineData("p1", "p3")]         // overlapping
    [InlineData("p1")]               // a strict subset
    [InlineData("p1", "p2", "p3")]   // a strict superset
    public void A_different_producer_set_never_contends_for_the_same_handoff_branch(params string[] otherProducers)
    {
        var mine = RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, new[] { "p1", "p2" });
        var theirs = RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, otherProducers);

        theirs.ShouldNotBe(mine, "two dependents staged in the SAME turn over different producers must not collide — the collision is what trips the (correct) no-clobber reconcile and aborts the whole turn's spawn");
    }

    /// <summary>
    /// The SAME collision, one axis over: a dependent's target repository is resolved PER SUBTASK
    /// (<c>.Spawn.cs</c>'s <c>ResolveTargetRepositoryId</c>), so two dependents in one turn can declare the IDENTICAL
    /// <c>dependsOn</c> and still target DIFFERENT repositories. The producer identity is repository-agnostic (an
    /// agent run id), but <c>ResolveProducerManifestsAsync</c> selects a different manifest ROW per repository — so
    /// the same labels stand for different patch bytes and integrate to a different tree. Two repository rows may
    /// carry one clone URL (the repositories table is unique on provider instance + external id, not on URL), and
    /// there the two trees meet on one remote: the second push is a clobber refusal, and one blocked subtask aborts
    /// the whole turn. Folding the resolved repository into the digest is what keeps the name a name for the TREE.
    /// </summary>
    [Fact]
    public void The_same_producer_set_resolved_against_a_different_repository_gets_its_own_handoff_branch()
    {
        var mine = RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, new[] { "p1", "p2" });
        var otherRepo = RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Guid.Parse("99999999-8888-4777-8666-555555555555"), new[] { "p1", "p2" });

        otherRepo.ShouldNotBe(mine, "the same producers staged against a different repository integrate a different manifest row's patches onto a different tree — if the two repositories share a clone URL, an identical name puts both trees on one ref and the second staging is refused");
    }

    /// <summary>
    /// The idempotent-re-push pin, and the reason the discriminator is a HASH of the producer identities rather than
    /// anything per-dependent (a subtask id, an index, a fresh guid): an identical producer set must still yield an
    /// identical name, so it integrates to an identical tree, so the unchanged no-clobber reconcile short-circuits on
    /// tree equality instead of blocking. Two dependents inheriting the SAME work legitimately share one branch.
    /// </summary>
    [Fact]
    public void An_identical_producer_set_yields_the_identical_branch_so_the_no_clobber_reconcile_still_short_circuits()
    {
        RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, new[] { "p1", "p2" })
            .ShouldBe(RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, new[] { "p1", "p2" }),
                "a per-dependent discriminator would fork a second branch for the same work AND break re-execution of the same turn — the reconcile can only short-circuit on a name it already pushed");
    }

    /// <summary>
    /// The NAME is order-invariant: the digest is over the sorted set, so the incidental order a plan declared its
    /// <c>dependsOn</c> in — which <c>SupervisorDependencyGate.LatestSucceededAgentRunIds</c> re-derives on every
    /// turn — never moves it. That is all this pins, and all that is true.
    ///
    /// <para>The INTEGRATION is NOT order-invariant, and nothing here claims it is:
    /// <c>LocalGitBranchIntegrator.ApplyAllAsync</c> applies the contributions in list order through
    /// <c>git apply --index --3way</c>, which does not commute in general (verified: one producer renaming a file a
    /// second one appends to applies cleanly in one order and conflicts in the other). What makes a SHARED name safe
    /// is not commutativity but <c>ReconcileExistingBranchAsync</c>, which is gated on TREE equality, never on the
    /// name: a dependent inherits an existing handoff branch only when its own integration independently reproduced
    /// the byte-identical tree, and otherwise the push is refused and the staging BLOCKS — the same degradation the
    /// pre-change per-turn name already produced, never a graft of a tree this dependent's producers did not
    /// produce.</para>
    /// </summary>
    [Fact]
    public void Shuffling_the_producer_ids_does_not_move_the_handoff_branch()
    {
        var declared = RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, new[] { "p-a", "p-b", "p-c" });

        RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, new[] { "p-c", "p-a", "p-b" }).ShouldBe(declared);
        RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, new[] { "p-b", "p-c", "p-a" }).ShouldBe(declared, "the same producers in a different order inherit the same work — an order-sensitive name would fork a redundant branch for it");
    }

    /// <summary>
    /// The digest must be stable ACROSS PROCESSES, not merely within one. <c>string.GetHashCode()</c> is randomized
    /// per process in .NET, so a hash built on it would give a run resuming on a different worker a DIFFERENT branch
    /// for the identical producer set — forking the handoff and defeating the idempotent re-push above, while every
    /// single-process test stayed green. Pinning the literal is what makes that substitution fail loudly.
    /// </summary>
    [Fact]
    public void The_producer_digest_is_a_stable_content_hash_not_a_per_process_one()
    {
        RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, 4, Repo, new[] { "p1", "p2" })
            .ShouldBe($"codespace/handoff/{Run:N}/turn4-3a832230b4a5", "SHA-256 over the repository id (N format) then the newline-joined ordinal-sorted labels, truncated to 12 lowercase hex chars — reproduce with: printf '11111111222243338444555555555555\\np1\\np2' | shasum -a 256 | cut -c1-12");
    }

    /// <summary>
    /// The name is pushed as a real ref and read by a real operator, so it must stay both legal and legible. The turn
    /// number stays in it deliberately: it tells the operator which turn staged the handoff, and it keeps this change
    /// purely ADDITIVE — a name written before this change (<c>…/turn4</c>) can never equal one written after
    /// (<c>…/turn4-{12 hex}</c>), so a run resuming across the change creates its branch instead of colliding with the
    /// one its own earlier turn already pushed.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(97)]
    public void The_handoff_branch_is_a_valid_git_ref_that_still_names_the_run_and_the_turn(int turn)
    {
        var branch = RealSupervisorActionExecutor.HandoffIntegrationBranch(Run, turn, Repo, new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() });

        branch.ShouldStartWith($"codespace/handoff/{Run:N}/turn{turn}-", customMessage: "the run and the turn stay readable at the front — the digest is a suffix, not a replacement");
        RejectedByGitRefFormat(branch).ShouldBeNull("a name git refuses is a handoff that can never be pushed at all");
        branch.Length.ShouldBeLessThan(255, "every ref component and the whole name stay far inside git's loose-ref path limits");
    }

    /// <summary>The subset of <c>git check-ref-format</c>'s rules a generated branch name could plausibly break, as a NAMED failure (null ⇒ accepted). Encoded here rather than shelled out so the unit tier stays git-free — a real <c>git push</c> of this exact name is exercised at the integration tier.</summary>
    private static string? RejectedByGitRefFormat(string name)
    {
        if (name.Length == 0) return "an empty name";
        if (name.StartsWith('/') || name.EndsWith('/') || name.Contains("//", StringComparison.Ordinal)) return "an empty path component";
        if (name.Contains("..", StringComparison.Ordinal) || name.EndsWith('.')) return "a dot sequence";
        if (name.Contains("@{", StringComparison.Ordinal)) return "an @{ sequence";
        if (name.Any(c => char.IsControl(c) || " ~^:?*[\\".Contains(c))) return "a forbidden character";

        return name.Split('/').FirstOrDefault(c => c.StartsWith('.') || c.EndsWith(".lock", StringComparison.Ordinal)) is { } bad ? $"the component '{bad}'" : null;
    }

    /// <summary>The producer identity the digest is taken over is the SAME one the contribution is labelled with — a digest over a different projection could name a set the integrator never actually applied.</summary>
    [Fact]
    public void A_producer_without_an_agent_run_still_contributes_a_stable_identity()
    {
        var agentRunId = Guid.NewGuid();
        var rowId = Guid.NewGuid();

        RealSupervisorActionExecutor.ProducerLabel(new PublishManifest { Id = rowId, AgentRunId = agentRunId }).ShouldBe(agentRunId.ToString());
        RealSupervisorActionExecutor.ProducerLabel(new PublishManifest { Id = rowId, AgentRunId = null }).ShouldBe(rowId.ToString(), "a manifest row with no agent run still identifies its own contribution — falling back to a constant would collapse two such producers onto one branch");
    }

    // ── DependsOnFor: BaseSubtaskId override precedence ─────────────────────────────────

    [Fact]
    public void A_dispatch_base_subtask_id_override_wins_over_the_plans_depends_on()
    {
        var planned = new SupervisorPlannedSubtask { Id = "b", Title = "b", Instruction = "do", DependsOn = new[] { "a1", "a2" } };
        var spec = new SupervisorAgentDispatch { SubtaskId = "b", BaseSubtaskId = "a2" };

        RealSupervisorActionExecutor.DependsOnFor(planned, spec).ShouldBe(new[] { "a2" }, "the model-authored override narrows to ONE producer for this spawn, even though the plan declared two");
    }

    [Fact]
    public void With_no_dispatch_override_the_plans_depends_on_stands()
    {
        var planned = new SupervisorPlannedSubtask { Id = "b", Title = "b", Instruction = "do", DependsOn = new[] { "a1", "a2" } };

        RealSupervisorActionExecutor.DependsOnFor(planned, spec: null).ShouldBe(new[] { "a1", "a2" });
        RealSupervisorActionExecutor.DependsOnFor(planned, new SupervisorAgentDispatch { SubtaskId = "b" }).ShouldBe(new[] { "a1", "a2" }, "a dispatch with no BaseSubtaskId leaves the plan's DependsOn untouched");
    }

    [Fact]
    public void With_neither_a_plan_dependency_nor_an_override_there_is_nothing_to_stage()
    {
        RealSupervisorActionExecutor.DependsOnFor(planned: null, spec: null).ShouldBeEmpty();
        RealSupervisorActionExecutor.DependsOnFor(new SupervisorPlannedSubtask { Id = "a", Title = "a", Instruction = "do" }, spec: null).ShouldBeEmpty("a flat subtask with no DependsOn resolves no dependency — byte-identical no-override path");
    }

    // ── PreferPriorAttemptStaging: retry world-state conservation's precedence (P0-1) ──

    [Fact]
    public void A_resolved_prior_attempt_ref_wins_over_a_plan_dependency_ref()
    {
        var priorAttempt = new DependencyStagingResult { Ref = "codespace/agent/prior-attempt", GoalFoldText = "prior attempt fold" };
        var dependency = new DependencyStagingResult { Ref = "codespace/agent/producer", GoalFoldText = "producer fold" };

        RealSupervisorActionExecutor.PreferPriorAttemptStaging(priorAttempt, dependency).ShouldBe(priorAttempt, "the retried subtask's OWN committed work is more specific than a plan dependency's handoff");
    }

    [Fact]
    public void With_no_prior_attempt_ref_the_dependency_staging_stands_unchanged()
    {
        var dependency = new DependencyStagingResult { Ref = "codespace/agent/producer", GoalFoldText = "producer fold" };

        RealSupervisorActionExecutor.PreferPriorAttemptStaging(DependencyStagingResult.NoOverride, dependency).ShouldBe(dependency, "no prior-attempt continuity → byte-identical to pre-P0-1 dependency staging");
    }

    [Fact]
    public void With_neither_resolved_the_result_is_still_NoOverride()
    {
        RealSupervisorActionExecutor.PreferPriorAttemptStaging(DependencyStagingResult.NoOverride, DependencyStagingResult.NoOverride).ShouldBe(DependencyStagingResult.NoOverride, "a genuine cold-start retry with no declared dependency clones the default branch");
    }

    // ── ApplyResumeRecord: the resume hint's TRUTH VALUE must match the actual git state (P0-1) ──

    [Fact]
    public void A_workspace_pinned_to_prior_work_carries_no_honest_redo_hint()
    {
        var task = new AgentTask { Goal = "do the thing", Harness = "codex-cli" };
        var prior = new ResumableSession(Guid.NewGuid(), "sess-1", "transcript", null);

        var resumed = RealSupervisorActionExecutor.ApplyResumeRecord(task, prior, workspaceHasPriorWork: true);

        resumed.ResumeFromSessionId.ShouldBe("sess-1");
        resumed.RestoredTranscript.ShouldBe("transcript");
        resumed.Goal.ShouldBe("do the thing", "the workspace genuinely contains the prior branch — no honesty caveat needed");
    }

    [Fact]
    public void A_workspace_with_no_preserved_git_state_gets_the_honest_redo_hint()
    {
        var task = new AgentTask { Goal = "do the thing", Harness = "codex-cli" };
        var prior = new ResumableSession(Guid.NewGuid(), "sess-1", "transcript", null);

        var resumed = RealSupervisorActionExecutor.ApplyResumeRecord(task, prior, workspaceHasPriorWork: false);

        resumed.ResumeFromSessionId.ShouldBe("sess-1", "the conversation is still restored");
        resumed.Goal.ShouldBe($"do the thing\n\n{RealSupervisorActionExecutor.HonestNoContinuityHint}", "the goal now HONESTLY says the git changes are NOT present, so the agent never trusts a restored conversation implying work it can't see");
    }

    // ── BuildBlockedSpawnOutcome: the wire shape resolve's conflict reader consumes ─────

    [Fact]
    public void A_missing_manifest_block_records_no_integration_detail()
    {
        var blocked = new[] { new RealSupervisorActionExecutor.DependencyBlock("b", "producer x recorded a diff but no patch was captured for it", Array.Empty<string>(), Array.Empty<string>()) };

        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildBlockedSpawnOutcome(blocked), AgentJson.Options);

        SupervisorOutcome.ReadStagedAgentCount(json).ShouldBe(0, "a blocked spawn stages zero agents");
        SupervisorOutcome.ReadIntegration(json).ShouldBeNull("a missing-manifest block is a data-integrity guard, not a reconcilable conflict — resolve has nothing to act on");
    }

    [Fact]
    public void A_conflicted_integration_block_round_trips_through_the_shared_integration_reader()
    {
        var blocked = new[]
        {
            new RealSupervisorActionExecutor.DependencyBlock("c", "the producers' work could not be auto-integrated onto one branch", new[] { "src/app.py" }, new[] { "codespace/agent/p1", "codespace/agent/p2" }),
        };

        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildBlockedSpawnOutcome(blocked), AgentJson.Options);

        var integration = SupervisorOutcome.ReadIntegration(json);

        integration.ShouldNotBeNull("a conflict-carrying block records an integration detail block");
        integration!.IsConflicted.ShouldBeTrue("the SAME status string a merge conflict uses, so the existing resolve verb's skip check recognizes it");
        integration.ConflictedFiles.ShouldBe(new[] { "src/app.py" });
        integration.PreservedBranches.ShouldBe(new[] { "codespace/agent/p1", "codespace/agent/p2" }, "both producers' own branches are named for the resolver to reconcile");
    }

    [Fact]
    public void Every_blocked_subtask_names_its_own_reason()
    {
        var blocked = new[]
        {
            new RealSupervisorActionExecutor.DependencyBlock("b", "reason one", Array.Empty<string>(), Array.Empty<string>()),
            new RealSupervisorActionExecutor.DependencyBlock("c", "reason two", Array.Empty<string>(), Array.Empty<string>()),
        };

        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildBlockedSpawnOutcome(blocked), AgentJson.Options);
        using var doc = JsonDocument.Parse(json);

        var entries = doc.RootElement.GetProperty("blockedSubtasks").EnumerateArray().ToList();

        entries.Select(e => e.GetProperty("subtaskId").GetString()).ShouldBe(new[] { "b", "c" });
        entries.Select(e => e.GetProperty("reason").GetString()).ShouldBe(new[] { "reason one", "reason two" }, "every withheld subtask is named with its OWN reason, never collapsed into one");
    }

    // ── BuildRejectedRetryOutcome: P0-2 action schema validation ────────────────────────

    [Fact]
    public void The_rejected_retry_outcome_names_the_specific_defect()
    {
        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildRejectedRetryOutcome(), AgentJson.Options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("retry").GetString().ShouldBe("rejected");
        doc.RootElement.GetProperty("reason").GetString().ShouldBe("the retry decision named no subtaskId — a retry must name the plan-local subtask id to re-run");
    }

    [Fact]
    public void A_rejected_retry_outcome_carries_no_agent_results_so_the_no_progress_watchdog_still_counts_it()
    {
        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildRejectedRetryOutcome(), AgentJson.Options);

        SupervisorOutcome.HasSettledEvidence(SupervisorOutcome.ReadAgentResults(json)).ShouldBeFalse("the rejection reads as no settled evidence — the EXISTING no-progress streak already trips on this without any new counter");
    }

    // ── FindMostRecentConflictDecision: the widened Merge-OR-Spawn conflict source ─────

    [Fact]
    public void A_conflicted_spawn_decision_is_recognized_as_a_conflict_source()
    {
        var spawn = ConflictedDecision(SupervisorDecisionKinds.Spawn, sequence: 1);

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(spawn)).ShouldBe(spawn, "a staging-blocked spawn's integration block is a conflict source exactly like a merge's");
    }

    [Fact]
    public void A_conflicted_merge_decision_is_still_recognized_as_a_conflict_source()
    {
        var merge = ConflictedDecision(SupervisorDecisionKinds.Merge, sequence: 1);

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(merge)).ShouldBe(merge, "byte-identical to before the widening — a merge conflict is still recognized");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.Resolve)]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.AskHuman)]
    [InlineData(SupervisorDecisionKinds.Stop)]
    public void No_other_decision_kind_is_ever_misread_as_a_conflict_source_even_carrying_a_conflicted_integration_block(string kind)
    {
        // Defensive: only Merge and Spawn may ever surface a conflict. A conflicted-shaped integration block on any
        // OTHER kind (however it got there) must never be picked up — the widening is a NAMED allow-list, not "any
        // decision with this JSON shape".
        var decision = ConflictedDecision(kind, sequence: 1);

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(decision)).ShouldBeNull();
    }

    [Fact]
    public void The_most_recent_conflict_wins_across_a_mixed_merge_and_spawn_tape()
    {
        var olderMerge = ConflictedDecision(SupervisorDecisionKinds.Merge, sequence: 1);
        var newerSpawn = ConflictedDecision(SupervisorDecisionKinds.Spawn, sequence: 2);

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(olderMerge, newerSpawn)).ShouldBe(newerSpawn, "newest-first — the later staging block supersedes the earlier merge conflict");
    }

    [Fact]
    public void A_clean_spawn_with_no_integration_block_is_not_a_conflict_source()
    {
        var cleanSpawn = new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = "{\"agentRunIds\":[],\"agentCount\":0}" };

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(cleanSpawn)).ShouldBeNull("an ordinary successful spawn carries no integration block at all");
    }

    private static SupervisorPriorDecision ConflictedDecision(string kind, int sequence) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
        OutcomeJson = JsonSerializer.Serialize(new { integration = new { status = "Conflicted", outcomes = Array.Empty<object>() } }, AgentJson.Options),
    };

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) => new() { Goal = "g", PriorDecisions = prior };
}
