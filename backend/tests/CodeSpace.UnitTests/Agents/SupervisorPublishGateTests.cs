using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: I3 (publish-or-park) — <see cref="SupervisorPublishGate.Validate"/>, pinned WITHOUT a DB. Proves the
/// substitution ladder owner-locked for the stop hard gate: (1) a stop with NO accepted work at all is untouched
/// (I3 out of scope); (2) a stop whose frontier unit produced no real work (e.g. investigate-only) is untouched;
/// (3) a stop with accepted-but-unpublished work is substituted to ONE server-authored <c>merge</c>
/// (<see cref="SupervisorMergePayload.ForcedByPublishGate"/>) the FIRST time; (4) once a merge already ran after
/// that frontier and STILL produced no clean publish (conflict / policy block / any other reason), the NEXT stop
/// is substituted to <c>ask_human</c> instead of retrying forever; (5) a stop with genuinely published output but
/// a blank summary is substituted to <c>ask_human</c> too; (6) a stop with published output AND a summary passes
/// through untouched; (7) every non-stop decision kind is always untouched, regardless of tape state.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPublishGateTests
{
    // ── (1)/(2): I3 does not apply ────────────────────────────────────────────────────

    [Fact]
    public void A_stop_with_no_prior_decisions_at_all_is_untouched() =>
        SupervisorPublishGate.Validate(Context(), StopDecision("done")).ShouldBeNull("nothing was ever produced — I3 is out of scope for an empty-handed stop");

    [Fact]
    public void A_stop_after_a_spawn_that_produced_no_real_work_is_untouched()
    {
        var context = Context(Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: false)));

        SupervisorPublishGate.Validate(context, StopDecision("investigated, nothing to change")).ShouldBeNull(
            "a read-only/investigate-only unit produced no real work — I3 does not manufacture a publish requirement out of nothing");
    }

    [Fact]
    public void A_stop_with_no_summary_but_also_no_accepted_work_is_untouched() =>
        SupervisorPublishGate.Validate(Context(), StopDecision("")).ShouldBeNull("the summary requirement is scoped to a run WITH accepted work, per I3's own text");

    // ── (3): first attempt — auto-substitute to a server-authored merge ───────────────

    [Fact]
    public void A_stop_with_accepted_unpublished_work_and_no_prior_merge_substitutes_a_forced_merge()
    {
        var context = Context(Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision("done"));

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Merge, "I3 auto-integrates BEFORE ever rejecting the run to a park");

        var payload = JsonSerializer.Deserialize<SupervisorMergePayload>(substituted.PayloadJson, AgentJson.Options)!;
        payload.ForcedByPublishGate.ShouldBe(true, "the merge is SERVER-authored for I3, never mistaken for a model choice");
    }

    [Fact]
    public void The_forced_merge_ignores_the_stops_own_summary_and_acceptance()
    {
        // A model that authored a rich stop payload (summary, acceptance) alongside an unpublished frontier still
        // gets the SAME forced-merge substitution — I3 does not try to preserve/merge fields from the rejected stop.
        var context = Context(Decision(SupervisorDecisionKinds.Retry, 1, SpawnOutcome(hasWork: true)));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision("ship it", acceptanceCommand: new[] { "sh", "check.sh" }));

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Merge);
    }

    // ── (4): a merge already ran after the frontier and still isn't published — park ──

    [Theory]
    [InlineData("Conflicted", "a contribution conflicted while integrating")]
    [InlineData("Skipped", "publish policy: repository is patch-only")]
    [InlineData("Failed", "git clone failed")]
    public void A_stop_after_a_failed_or_blocked_merge_parks_to_ask_human_naming_the_reason(string integrationStatus, string reason)
    {
        var context = Context(
            Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome(integrationStatus, integratedBranch: null, reason: reason)));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision("done"));

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a merge already tried and failed — I3 never retries automatically, it parks");

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(substituted.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain(reason, Case.Insensitive, "the park names WHY the run could not publish, so a human/next turn knows what to fix");
    }

    [Fact]
    public void A_stop_never_re_substitutes_a_second_forced_merge_after_the_first_one_failed()
    {
        // Exactly ONE auto-merge attempt per frontier — the second stop attempt must park, never loop merge→merge→merge.
        var context = Context(
            Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", integratedBranch: null, reason: "conflict")));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision("done"));

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);
    }

    [Fact]
    public void Fresh_work_staged_after_a_failed_merge_gets_its_own_forced_merge_attempt()
    {
        // A NEW spawn after the failed merge is a NEW frontier — I3 must not permanently park a run that later made
        // genuine fresh progress just because an EARLIER wave once conflicted.
        var context = Context(
            Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", integratedBranch: null, reason: "conflict")),
            Decision(SupervisorDecisionKinds.Retry, 3, SpawnOutcome(hasWork: true)));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision("done"));

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Merge, "fresh unconsolidated work after the failed merge is a NEW frontier, worth one more auto-merge attempt");
    }

    // ── P0-5: the frontier's OWN contributor already has a genuinely published manifest ──

    [Fact]
    public void A_frontiers_already_published_contributor_satisfies_i3_without_any_merge_at_all()
    {
        // Real bug (run 96695645): a single accepted unit's own AgentRunId already had a Pushed PublishManifest
        // row, but no SEPARATE Integration-kind manifest ever existed (the model's own ordinary merge never
        // triggered the opt-in-gated integrate-at-stop augmentation) — I3 must recognize the already-published
        // contributor directly, never force a redundant merge or park on a human forever with nothing that can change.
        var agentRunId = Guid.NewGuid();
        var context = Context(published: new[] { agentRunId }, Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true, agentRunId)));

        SupervisorPublishGate.Validate(context, StopDecision("shipped the fix")).ShouldBeNull(
            "the frontier's own contributor is already published on the canonical ledger — no merge is needed at all");
    }

    [Fact]
    public void A_frontiers_already_published_contributor_with_a_blank_summary_still_parks_to_ask_human()
    {
        var agentRunId = Guid.NewGuid();
        var context = Context(published: new[] { agentRunId }, Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true, agentRunId)));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision(""));

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "published work still needs a summary before the run can complete — the same rule as an integration-published run");
    }

    [Fact]
    public void A_forced_stop_is_exempt_from_the_summary_requirement()
    {
        // requireSummary:false is the FORCED-stop bar (GateForcedStop): a bound authored the stop, not the model —
        // its recorded reason IS the explanation, and parking a published run on "provide a summary" no human owes
        // would ask forever. The publish-or-park ladder itself still applies to forced stops unchanged.
        var agentRunId = Guid.NewGuid();
        var context = Context(published: new[] { agentRunId }, Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true, agentRunId)));

        SupervisorPublishGate.Validate(context, StopDecision(""), requireSummary: false)
            .ShouldBeNull("the raw-push shortcut arm honours the forced-stop exemption");
    }

    [Fact]
    public void A_different_agents_publication_never_satisfies_a_different_frontiers_i3_check()
    {
        // The published set must be checked against THIS frontier's own contributor, never any agent the run
        // happens to have published — a false positive here would let I3 wave through genuinely unpublished work.
        var agentRunId = Guid.NewGuid();
        var someOtherPublishedAgent = Guid.NewGuid();
        var context = Context(published: new[] { someOtherPublishedAgent }, Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true, agentRunId)));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision("done"));

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Merge, "the frontier's own agent is NOT in the published set — I3 falls through to the ordinary forced-merge ladder exactly as before this fix");
    }

    [Fact]
    public void A_pushed_but_acceptance_rejected_contributor_never_satisfies_i3_via_the_published_shortcut()
    {
        // A raw push happens BEFORE the per-unit acceptance grade folds (AgentRunExecutor pushes at execution time;
        // FoldUnitAcceptanceGradeAsync grades later) — so a REJECTED unit can still show up as Pushed in the ledger.
        // The published shortcut must exclude it, the same "局部綠≠整合綠" bar the merge + resolver doors already
        // enforce (SupervisorOutcome.IsAcceptanceRejected) — otherwise I3 lets a run complete with ONLY rejected work.
        var agentRunId = Guid.NewGuid();
        var context = Context(published: new[] { agentRunId }, Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true, agentRunId, acceptancePassed: false)));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision("done"));

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Merge, "the contributor's push does NOT count as published once its own acceptance grade rejected it — I3 falls through to the ordinary ladder");
    }

    [Fact]
    public void A_frontiers_published_contributor_never_overrides_a_later_diagnosed_merge_conflict()
    {
        // "Pushed" and "cleanly integrates" are INDEPENDENT facts about the same branch — the contributor's own raw
        // push happens at agent-completion time, strictly BEFORE any later merge/integrate attempt over that branch.
        // A later merge that ran a real integrate step and genuinely conflicted must never be silently overridden by
        // the raw-push shortcut — that would let I3 wave a stop through with a real, already-diagnosed integration
        // failure still unresolved underneath it.
        var agentRunId = Guid.NewGuid();
        var context = Context(
            published: new[] { agentRunId },
            Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true, agentRunId)),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", integratedBranch: null, reason: "base SHA mismatch")));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision("shipped the fix"));

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a later diagnosed conflict outranks the raw-push shortcut — the run must park, not silently complete");

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(substituted.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("base SHA mismatch", Case.Insensitive, "the park names the diagnosed reason, not a generic message");
    }

    // ── (5)/(6): published — the summary requirement ───────────────────────────────────

    [Fact]
    public void A_stop_with_a_clean_merge_and_a_summary_passes_through_untouched()
    {
        var context = Context(
            Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Clean", integratedBranch: "codespace/integration/x")));

        SupervisorPublishGate.Validate(context, StopDecision("shipped the fix")).ShouldBeNull("published + summarized — the stop proceeds exactly as authored");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_stop_with_published_output_but_a_blank_summary_parks_to_ask_human(string? summary)
    {
        var context = Context(
            Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Clean", integratedBranch: "codespace/integration/x")));

        var substituted = SupervisorPublishGate.Validate(context, StopDecision(summary!));

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(substituted.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("no summary", Case.Insensitive);
    }

    [Fact]
    public void A_verified_resolution_counts_as_published_even_without_a_later_merge()
    {
        var context = Context(
            Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", integratedBranch: null, reason: "conflict")),
            Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch(VerifiedSummary, "codespace/resolve/r")));

        SupervisorPublishGate.Validate(context, StopDecision("reconciled")).ShouldBeNull("a verified resolver's own tested branch IS a publish — the SAME reader the stop-acceptance grade already trusts");
    }

    // ── Regression: an UNVERIFIED resolve must NEVER be treated as accepted-unpublished work ──

    [Fact]
    public void A_stop_after_an_unverified_resolution_is_untouched_never_auto_merged()
    {
        // Real bug caught by the whole-loop E2E: an unverified resolve (the resolver wrote real changes, but its OWN
        // tests still failed) was being read as "accepted work nothing later merged" and getting auto-substituted to
        // a forced merge — silently overriding the model's own correct "stop, this couldn't be verified" report and
        // re-merging conflicting branches that would just conflict again. The resolver loop's own "局部綠≠整合綠"
        // withhold contract already excludes an unverified resolve's work from ANY merge — I3 must respect that,
        // not fight it.
        var context = Context(
            Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", integratedBranch: null, reason: "conflict")),
            Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch("tests still red", "codespace/resolve/r")));

        SupervisorPublishGate.Validate(context, StopDecision("the resolution did not pass its own tests")).ShouldBeNull(
            "an unverified resolution's work was never ACCEPTED in the first place — I3 must never auto-merge it or park on it, the model's own stop stands");
    }

    [Fact]
    public void A_stop_after_an_unverified_resolution_with_no_summary_is_still_untouched()
    {
        // Distinct from the "published but no summary" ask_human case (5): an unverified resolve is NOT published,
        // but it's ALSO not in-scope accepted work — so the summary requirement (scoped to accepted work) never fires.
        var context = Context(Decision(SupervisorDecisionKinds.Resolve, 1, ResolveOutcomeWithBranch("tests still red", "codespace/resolve/r")));

        SupervisorPublishGate.Validate(context, StopDecision("")).ShouldBeNull("an unverified resolution is out of I3's scope entirely — including its summary requirement");
    }

    // ── (7): every non-stop decision is always untouched ───────────────────────────────

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.Merge)]
    [InlineData(SupervisorDecisionKinds.Resolve)]
    [InlineData(SupervisorDecisionKinds.AskHuman)]
    public void A_non_stop_decision_is_always_untouched_regardless_of_tape_state(string kind)
    {
        var context = Context(Decision(SupervisorDecisionKinds.Spawn, 1, SpawnOutcome(hasWork: true)));

        SupervisorPublishGate.Validate(context, new SupervisorDecision { Kind = kind, PayloadJson = "{}" }).ShouldBeNull("I3 gates ONLY the stop verb — every other decision passes through this rung unchanged");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────

    private static readonly string VerifiedSummary = $"reconciled cleanly. {SupervisorResolverRecipe.TestsPassedMarker}";

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) => Context(published: null, prior);

    private static SupervisorTurnContext Context(IReadOnlyCollection<Guid>? published, params SupervisorPriorDecision[] prior) => new()
    {
        Goal = "ship", SupervisorRunId = Guid.NewGuid(), TeamId = Guid.NewGuid(), NodeId = "sup", TurnNumber = prior.Length + 1, PriorDecisions = prior,
        PublishedAgentRunIds = published is null ? new HashSet<Guid>() : new HashSet<Guid>(published),
    };

    private static SupervisorPriorDecision Decision(string kind, long seq, string? outcomeJson) =>
        new() { Id = Guid.NewGuid(), Sequence = seq, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcomeJson };

    private static SupervisorDecision StopDecision(string summary, IReadOnlyList<string>? acceptanceCommand = null) => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload
        {
            Outcome = "completed",
            Summary = summary,
            Acceptance = acceptanceCommand is null ? null : new SupervisorAcceptanceSpec { Command = acceptanceCommand },
        }, AgentJson.Options),
    };

    private static string SpawnOutcome(bool hasWork, Guid? agentRunId = null, bool? acceptancePassed = null)
    {
        var result = new SupervisorAgentResult
        {
            AgentRunId = agentRunId ?? Guid.NewGuid(),
            Status = "Succeeded",
            ChangedFiles = hasWork ? new[] { "a.txt" } : Array.Empty<string>(),
            AcceptancePassed = acceptancePassed,
        };
        return JsonSerializer.Serialize(new { agentRunIds = new[] { result.AgentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);
    }

    private static string MergeOutcome(string integrationStatus, string? integratedBranch, string? reason = null) =>
        JsonSerializer.Serialize(new { merged = Array.Empty<object>(), count = 0, integration = new { status = integrationStatus, integratedBranch, reason } }, AgentJson.Options);

    private static string ResolveOutcomeWithBranch(string summary, string producedBranch)
    {
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Summary = summary, ProducedBranch = producedBranch };
        return JsonSerializer.Serialize(new { agentRunIds = new[] { result.AgentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);
    }
}
