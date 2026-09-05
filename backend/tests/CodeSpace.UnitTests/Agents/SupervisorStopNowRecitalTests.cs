using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins P5-6 — the "if you stopped now" contract recital. The reducer's own mid-run verdict reaches the
/// decider prompt so the perception gap behind stop-without-shipping closes BEFORE the stop is chosen: unresolved
/// dimensions render with the settle-or-honest-stop steer, an all-clear renders the stop-now steer (anti-overwork),
/// settled-positive dimensions are omitted (the #1256 session-recital convention), and a contract-less run renders
/// nothing (byte-identical prompt). The DB-reading compose lives at rehydrate; this renderer is pure.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorStopNowRecitalTests
{
    private static CompletionAssessment Assessment(OutcomeDisposition outcome = OutcomeDisposition.Solved, VerificationDisposition verification = VerificationDisposition.Passed, ArtifactDisposition artifact = ArtifactDisposition.Captured, DeliveryDisposition delivery = DeliveryDisposition.Delivered) => new()
    {
        Basis = CompletionBasis.ContractDerived, Execution = ExecutionDisposition.Completed,
        Outcome = outcome, Verification = verification, Artifact = artifact, Delivery = delivery,
    };

    [Fact]
    public void No_assessment_renders_nothing()
    {
        SupervisorStopNowRecital.Render(null).ShouldBeNull("contract-less / pre-F0 runs pay no prompt tax");
    }

    [Fact]
    public void Unresolved_dimensions_render_with_the_settle_or_honest_stop_steer()
    {
        var block = SupervisorStopNowRecital.Render(Assessment(outcome: OutcomeDisposition.Unsolved, verification: VerificationDisposition.Failed, delivery: DeliveryDisposition.Unknown));

        block.ShouldNotBeNull();
        block!.ShouldContain("IF YOU STOPPED NOW", Case.Sensitive);
        block.ShouldContain("outcome=Unsolved", Case.Sensitive);
        block.ShouldContain("verification=Failed", Case.Sensitive);
        block.ShouldContain("delivery=Unknown", Case.Sensitive);
        block.ShouldNotContain("artifact=", Case.Sensitive, "a settled-positive dimension is omitted — the unclean ones name exactly what is owed");
        block.ShouldContain("a stop right now cannot read Solved", Case.Sensitive);
        block.ShouldContain("never stop as if done", Case.Sensitive, "the C3 stop-without-shipping steer");
    }

    [Fact]
    public void An_all_clear_renders_the_stop_now_steer()
    {
        var block = SupervisorStopNowRecital.Render(Assessment());

        block.ShouldNotBeNull("the clean direction is as decision-relevant as the dirty one");
        block!.ShouldContain("every contract dimension reads SETTLED", Case.Sensitive);
        block.ShouldContain("a clean stop now reads Solved", Case.Sensitive);
        block.ShouldContain("stop rather than spending further turns", Case.Sensitive, "the anti-overwork direction");
    }

    [Theory]
    [InlineData(VerificationDisposition.NotApplicable)]   // authorized-NA reads settled — no nag
    [InlineData(VerificationDisposition.Passed)]
    public void A_settled_verification_never_renders_as_a_concern(VerificationDisposition verification)
    {
        SupervisorStopNowRecital.Render(Assessment(verification: verification))!
            .ShouldNotContain("verification=", Case.Sensitive);
    }

    [Fact]
    public void The_header_is_pinned()
    {
        SupervisorStopNowRecital.Header.ShouldBe("IF YOU STOPPED NOW (the completion reducer's verdict on the facts so far):");
    }

    // ── The prompt wiring: prerendered at rehydrate, rendered verbatim by the pure prompt build ──

    [Fact]
    public void The_user_prompt_carries_the_prerendered_recital()
    {
        var recital = SupervisorStopNowRecital.Render(Assessment(verification: VerificationDisposition.Failed, outcome: OutcomeDisposition.Unsolved))!;

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(new SupervisorTurnContext
        {
            Goal = "ship it", TurnNumber = 3, PriorDecisions = Array.Empty<SupervisorPriorDecision>(), CompletionRecital = recital,
        });

        prompt.ShouldContain("IF YOU STOPPED NOW", Case.Sensitive);
        prompt.ShouldContain("verification=Failed", Case.Sensitive);
    }

    [Fact]
    public void A_null_recital_leaves_the_prompt_byte_identical()
    {
        LlmSupervisorDecider.BuildUserPromptForTest(new SupervisorTurnContext { Goal = "ship it", TurnNumber = 3, PriorDecisions = Array.Empty<SupervisorPriorDecision>() })
            .ShouldNotContain("IF YOU STOPPED NOW", Case.Sensitive);
    }

    // ── The FIFTH gate: the upstream stages the terminal authority parks a Success claim over ─────────

    /// <summary>The authority's OWN table, not a fixture — a profile invented here could declare a stage story production never enforces.</summary>
    private static readonly ModeProfile Supervisor = new ModeProfileRegistry().Resolve(RunModeKeys.Supervisor)!;

    /// <summary>Everything but Integrate — the exact shape of the run this line exists for: units executed under an authorized plan, their branches never reconciled.</summary>
    private static readonly IReadOnlySet<CompletionStage> AllButIntegrate = new HashSet<CompletionStage> { CompletionStage.Contract, CompletionStage.Plan, CompletionStage.Execute };

    private static readonly IReadOnlySet<CompletionStage> EveryUpstreamStage = UpstreamStageTrace.Stages;

    [Fact]
    public void A_required_stage_with_no_evidence_names_the_stage_and_the_count()
    {
        // The live gap (real-model runs 33930904059 / 33943475246): a conflicted merge and an unverified resolve
        // left Integrate unevidenced, the decider stopped 'completed', and the authority parked the run. The block
        // it read said every dimension was SETTLED and nothing else — the refusal was invisible until afterwards.
        var block = SupervisorStopNowRecital.Render(Assessment(), AllButIntegrate, Supervisor, CompletionEnforcementMode.Enforced);

        block.ShouldNotBeNull();
        block!.ShouldContain(SupervisorStopNowRecital.RefusalLead, Case.Sensitive);
        block.ShouldContain("requires 1 stage(s) with no evidence — Integrate.", Case.Sensitive, "the line must name the stage the authority parks on, and how many are missing");
        block.ShouldContain("do not claim completed", Case.Sensitive);
        block.ShouldContain("every contract dimension reads SETTLED", Case.Sensitive, "the settled arm is exactly where the gap bit — the stage line must render BESIDE it, not instead of it");
    }

    [Theory]
    [InlineData(CompletionEnforcementMode.Enforced)]
    [InlineData(CompletionEnforcementMode.Shadow)]
    [InlineData(CompletionEnforcementMode.Legacy)]
    public void A_never_derived_stage_trace_names_every_required_upstream_stage(CompletionEnforcementMode mode)
    {
        // The authority reads a null trace fail-CLOSED (a legacy compose evidences nothing). The recital mirrors
        // that reading rather than going quiet, or it would promise a stop the gate refuses. The FACTS are the same
        // in every mode — only the lead-in above them moves.
        SupervisorStopNowRecital.Render(Assessment(), exercisedUpstreamStages: null, Supervisor, mode)!
            .ShouldContain("requires 4 stage(s) with no evidence — Contract, Plan, Execute, Integrate.", Case.Sensitive);
    }

    // ── The MODE gate: the authority refuses only an Enforced run, so only an Enforced prompt may say REFUSED ──

    /// <summary>
    /// The false threat this pins out. <c>CompletionTerminalAuthority.ArbitrateAsync</c> passes a run through
    /// verbatim unless <c>CompletionPolicy.ModeFor(...) == Enforced</c> (its first line), and
    /// <c>CompletionPolicy.CurrentMode</c> is <c>Shadow</c> — so EVERY default supervisor run was being told its
    /// stop "will be REFUSED" by a gate that was never going to run against it, steering the whole default cohort
    /// off a stop the engine would have honoured and toward <c>ask_human</c>.
    /// </summary>
    [Theory]
    [InlineData(CompletionEnforcementMode.Enforced, true)]
    [InlineData(CompletionEnforcementMode.Shadow, false)]    // CompletionPolicy.CurrentMode — the default cohort
    [InlineData(CompletionEnforcementMode.Legacy, false)]    // ModeFor's fail-closed read of an unstamped column
    public void Only_an_enforced_run_is_told_its_stop_will_be_refused(CompletionEnforcementMode mode, bool refuses)
    {
        var block = SupervisorStopNowRecital.Render(Assessment(), AllButIntegrate, Supervisor, mode)!;

        block.Contains(SupervisorStopNowRecital.RefusalLead, StringComparison.Ordinal).ShouldBe(refuses,
            $"the authority refuses nothing under {mode}; promising a refusal that cannot happen is a false threat, and withholding one that will is the defect this block exists to fix");
        block.Contains(SupervisorStopNowRecital.AdvisoryLead, StringComparison.Ordinal).ShouldBe(!refuses,
            "the non-Enforced arm still has to state the gap — going silent would put the stage back out of the decider's sight");
    }

    [Fact]
    public void The_default_enforcement_mode_never_threatens_a_refusal()
    {
        SupervisorStopNowRecital.Render(Assessment(), AllButIntegrate, Supervisor)
            .ShouldNotContain(SupervisorStopNowRecital.RefusalLead, Case.Sensitive,
                "a call site that has not said which cohort the run is in must not speak for the strictest one");
    }

    /// <summary>
    /// Both lines, spelled out — the wording IS the behaviour here, and the honest exit in the steer is the point.
    /// Past <c>SupervisorLane.DefaultMaxResolveAttempts</c> (one) a further <c>resolve</c> force-stops the run, so
    /// "land that work or ask_human" was a dead end for exactly the tape this line renders over. A <c>stop</c>
    /// carrying <c>gave_up</c> reduces to Unsolved → <c>TerminalDecider</c> HonestFailure, which never reaches the
    /// stage gate (only a CleanSuccess does) — so naming it costs nothing and unblocks the run.
    /// </summary>
    [Theory]
    [InlineData(CompletionEnforcementMode.Enforced, "A stop now will be REFUSED by the completion authority:")]
    [InlineData(CompletionEnforcementMode.Shadow, "A 'completed' stop now would be recorded against missing evidence:")]
    public void The_stage_line_is_pinned_verbatim_in_both_modes(CompletionEnforcementMode mode, string lead)
    {
        SupervisorStopNowRecital.Render(Assessment(), AllButIntegrate, Supervisor, mode)
            .ShouldBe("IF YOU STOPPED NOW (the completion reducer's verdict on the facts so far):"
                + "\n- every contract dimension reads SETTLED — a clean stop now reads Solved. If the goal is met, stop rather than spending further turns on a contract that is already satisfied."
                + $"\n- {lead} mode 'supervisor' requires 1 stage(s) with no evidence — Integrate. Land that work, stop with outcome 'gave_up', or ask_human; do not claim completed.");
    }

    [Theory]
    [InlineData(true)]    // every required upstream stage evidenced → the authority passes → no line
    [InlineData(false)]   // an unregistered mode (no profile) → the authority parks on its OWN gate → nothing to add
    public void A_run_with_nothing_to_refuse_renders_the_block_byte_identically(bool registeredMode)
    {
        // The golden string, spelled out: a run that would NOT be refused pays no prompt tax at all, so every
        // existing scenario — and the pinned golden-prompt digest over them — is untouched by this slice.
        SupervisorStopNowRecital.Render(Assessment(), registeredMode ? EveryUpstreamStage : AllButIntegrate, registeredMode ? Supervisor : null)
            .ShouldBe("IF YOU STOPPED NOW (the completion reducer's verdict on the facts so far):\n- every contract dimension reads SETTLED — a clean stop now reads Solved. If the goal is met, stop rather than spending further turns on a contract that is already satisfied.");
    }

    [Fact]
    public void The_default_arguments_leave_every_pre_existing_call_site_byte_identical()
    {
        SupervisorStopNowRecital.Render(Assessment(outcome: OutcomeDisposition.Unsolved))
            .ShouldBe("IF YOU STOPPED NOW (the completion reducer's verdict on the facts so far):\n- UNRESOLVED: outcome=Unsolved — a stop right now cannot read Solved. Settle what is owed (make the failing checks pass, land the owed delivery/output), or stop honestly / ask a human — never stop as if done.");
    }

    /// <summary>
    /// The BOND: the line renders exactly when <c>CompletionTerminalAuthority</c> would object to the stop, because
    /// it asks the same reader the same question — <c>UpstreamStageTrace.MissingRequired(profile, trace)</c>, the
    /// gate at <c>CompletionTerminalAuthority.cs:121</c>. A second derivation here could drift into promising a stop
    /// the gate refuses (the defect this slice fixes) or nagging about a stop it would allow. Swept over EVERY
    /// subset of the trace's jurisdiction, so no cell of the mapping is left unmeasured — and over both cohorts,
    /// because the REFUSAL verb belongs only to the one the gate at <c>CompletionTerminalAuthority.cs:59</c> lets
    /// through: under Shadow the authority does not refuse at all, so the advisory renders in its place.
    /// </summary>
    [Theory]
    [InlineData(CompletionEnforcementMode.Enforced)]
    [InlineData(CompletionEnforcementMode.Shadow)]
    public void The_line_renders_exactly_when_the_authoritys_own_stage_gate_would_refuse(CompletionEnforcementMode mode)
    {
        var refuses = mode == CompletionEnforcementMode.Enforced;

        foreach (var exercised in Subsets(UpstreamStageTrace.Stages.ToArray()))
        {
            var missing = UpstreamStageTrace.MissingRequired(Supervisor, exercised);
            var block = SupervisorStopNowRecital.Render(Assessment(), exercised, Supervisor, mode)!;

            block.Contains(refuses ? SupervisorStopNowRecital.RefusalLead : SupervisorStopNowRecital.AdvisoryLead, StringComparison.Ordinal)
                .ShouldBe(missing.Count > 0, $"the recital and the authority disagree over the trace [{string.Join(",", exercised)}] under {mode}");

            block.ShouldNotContain(refuses ? SupervisorStopNowRecital.AdvisoryLead : SupervisorStopNowRecital.RefusalLead, Case.Sensitive,
                $"{mode} must never borrow the other cohort's lead-in");

            if (missing.Count > 0)
                block.ShouldContain($"{missing.Count} stage(s) with no evidence — {string.Join(", ", missing)}.", Case.Sensitive,
                    "the line must name the authority's OWN missing list, in its order — a re-derivation is what drifts");
        }
    }

    private static IEnumerable<IReadOnlySet<CompletionStage>> Subsets(CompletionStage[] stages)
    {
        for (var mask = 0; mask < 1 << stages.Length; mask++)
            yield return new HashSet<CompletionStage>(stages.Where((_, i) => (mask & (1 << i)) != 0));
    }
}
