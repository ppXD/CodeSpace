using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// THE scientific referee for the owner's core doubt — "does the adversarial agent OUTPUT critic + bounded revise loop
/// actually raise task correctness, or is it just cost and burden?" — answered by running the SAME seed corpus TWICE
/// through the real coding agent, arms differing in EXACTLY ONE axis: the output-critic config on
/// <see cref="BenchmarkAgentSelection"/> (Arm A = <see cref="ReviewMode.Improve"/> + one revise round; Arm B = no
/// critic). An ESTIMATE-AND-REPORT instrument, never a hypothesis test: n=18 per arm with no RNG seed, so the 95%
/// Wilson half-width at p≈0.6 is ≈±22pp — the E2E therefore GATES only deterministic wiring + a per-arm capability
/// floor, and NEVER on Arm A ≥ Arm B (that would flake on sampling noise and train operators to ignore reds). The
/// per-arm delta, paired discordant cells, token cost, and the intervention 2×2 are REPORTED for the operator to read.
///
/// <para><b>Every confound the design hunt surfaced is neutralized here, in code:</b>
/// (1) <b>Survivorship</b> — the solve rate is <c>run.Scorecard.Overall.SuccessRate</c> = <c>Count(Grade.Passed)/Count(all)</c>,
/// the critic-blind objective oracle over the WHOLE population (a critic-flagged pair stays in the denominator; the
/// buggy <c>solved/executed</c> formula is never used). (2) <b>Unfair retry</b> — the benchmark tasks carry no
/// <c>Acceptance</c>, so every Arm A revise round is critic-driven; the A−B delta is still labelled "critic + its
/// triggered retry combined", and Arm A's ΣReviseRounds is emitted as the retry-share disclosure. (3) <b>Cost
/// undercount</b> — the token total is agent+revise only (the critic's own model tokens land nowhere on a standalone
/// benchmark run), reported with a LOUD label that Arm A's true cost is strictly higher. (4) <b>Intervention sign</b> —
/// scoped to <c>ExitReason=="output-flagged"</c> and crossed with the grade into trueCatches / falseFlags / leaks, so a
/// higher flag count WITH higher correctness reads as the critic WORKING, never as burden. (5) <b>Fail-open silent
/// null</b> — a critic-fired guard FAILS LOUD if Arm A shows zero flags AND zero revise rounds (the reviewer never ran),
/// so a null delta can never masquerade as "the critic doesn't help".</para>
///
/// <para>Double opt-in (skip ≠ pass, LOUD): needs the <c>CODESPACE_LLM_*</c> secrets AND the explicit
/// <see cref="OptInEnvVar"/> — two live corpus passes are ≈2–3× a single-arm job, so it stays OFF the default
/// real-model lane budget and runs only on deliberate dispatch. POSIX-only; <c>[Category=RealModel]</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelCriticAbCorpusE2ETests
{
    private const string Provider = "Anthropic";   // the blessed wire

    /// <summary>Explicit opt-in on top of the secret gate: two live corpus passes are far heavier than the single-arm gate, so this A/B runs ONLY when deliberately dispatched, never doubling every real-model lane run.</summary>
    public const string OptInEnvVar = "CODESPACE_BENCH_AB";

    /// <summary>The per-arm capability FLOOR (independent gate, model-blind to the OTHER arm) — a catastrophically broken arm still reds. Same conservative floor as the single-arm gate; the A−B delta is never gated (n=18 noise).</summary>
    private const double MinSolveRate = 0.5;

    private readonly PostgresFixture _fixture;

    public RealModelCriticAbCorpusE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Critic_on_vs_off_over_the_seed_corpus_reports_the_solve_cost_and_intervention_deltas()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip green proving nothing.");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OptInEnvVar)))
        {
            RealModelGate.ReportSkipped(Provider, $"critic A/B opt-in absent — set {OptInEnvVar}=1 to run the two-pass corpus A/B (≈2–3× a single-arm job; kept off the default lane budget)");
            return;   // skip ≠ pass
        }

        if (OperatingSystem.IsWindows()) return;                          // the seed fixtures + checks are /bin/sh scripts
        if (!await GitReadyAsync()) return;
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the benchmark needs a harness binary (skip ≠ pass)"); return; }

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credId = await SeedAgentCredentialAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!);
        var reviewerRowId = await SeedReviewerPoolRowAsync(teamId, credId, model!);   // an explicit pool row → the critic resolves the SAME live model (no auto-pick fail-open)

        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            // The two arms differ in EXACTLY the critic axis — same harness, model, credential, autonomy (a record `with`).
            var armB = new BenchmarkAgentSelection { Harness = "claude-code", Model = model, ModelCredentialId = credId, Autonomy = AgentAutonomyLevel.Trusted };
            var armA = armB with { OutputReviewMode = ReviewMode.Improve, MaxReviseRounds = 1, ReviewerModelId = reviewerRowId };

            CorpusBenchmarkRun bRun, aRun;
            using (var scope = _fixture.BeginScope())
                bRun = await scope.Resolve<ICorpusBenchmarkRunner>().RunAsync(SeedBenchmarkCorpus.Tasks, teamId, armB, CancellationToken.None);
            using (var scope = _fixture.BeginScope())
                aRun = await scope.Resolve<ICorpusBenchmarkRunner>().RunAsync(SeedBenchmarkCorpus.Tasks, teamId, armA, CancellationToken.None);

            // Per-arm infra guard: no pair reached a clean terminal ⇒ gateway/execution outage, LOUD infra — never a false capability verdict.
            RanCleanly(bRun).ShouldBeGreaterThan(0, "Arm B: no pair executed — gateway/execution infra, not a capability miss");
            RanCleanly(aRun).ShouldBeGreaterThan(0, "Arm A: no pair executed — gateway/execution infra, not a capability miss");
            if (RanCleanly(bRun) == 0 || RanCleanly(aRun) == 0)
                throw new AgentExecutionInfraException("a whole arm failed to execute (gateway/execution infra)");

            // Critic-fired guard (fatal-confound #5): the live critic fails OPEN — if the reviewer never resolved, Arm A ≡ Arm B
            // and a null delta would lie as "the critic doesn't help". Demand PROOF the critic actually ran, else FAIL LOUD as infra.
            var aReviseTotal = aRun.Results.Sum(r => r.ReviseRounds);
            var aFlagged = aRun.Results.Count(r => r.ExitReason == "output-flagged");
            if (aReviseTotal == 0 && aFlagged == 0)
                throw new AgentExecutionInfraException($"Arm A critic NEVER FIRED (0 flags, 0 revise rounds) — reviewer model/pool row {reviewerRowId} likely unresolved or no structured client registered; a null delta here would falsely read as 'critic has no effect'");

            // ── Solve rate: arm-STABLE, critic-blind, survivorship-proof (Grade.Passed over ALL results) ──
            var (aSolved, aN, aRate) = RateOf(aRun);
            var (bSolved, bN, bRate) = RateOf(bRun);

            // ── Paired discordant cells (intersection-only on (TaskId,Mode)) — shows whether an effect rests on 1 task (noise) or 5 (signal) ──
            var (aOnly, bOnly, paired) = PairedDiscordant(aRun, bRun);

            // ── Intervention decomposition (Arm A): the critic INTERVENED when it flagged at least once — a revise round
            // ran under Improve (ReviseRounds > 0) OR the run ended still-flagged (output-flagged). This is deliberately
            // BROADER than the final ExitReason: the critic's HEADLINE value — flag → agent revises → final code PASSES —
            // ends with ExitReason "completed", so scoping only to "output-flagged" (as a naive 2×2 does) makes the
            // catch-AND-fix invisible and undercounts the critic. Crossed with the OBJECTIVE final grade:
            bool Flagged(BenchmarkResult r) => r.ReviseRounds > 0 || r.ExitReason == "output-flagged";
            var catchAndResolve = aRun.Results.Count(r => Flagged(r) && r.Grade.Passed && r.ExitReason != "output-flagged");   // flagged → revised → correct + shipped: the critic's productive VALUE (incl. false alarms the revise cleared — the oracle can't split the two)
            var trueHold = aRun.Results.Count(r => r.ExitReason == "output-flagged" && !r.Grade.Passed);                       // caught a broken change, held for a human: VALUE
            var falseHold = aRun.Results.Count(r => r.ExitReason == "output-flagged" && r.Grade.Passed);                      // held a CORRECT change for a human: the burden
            var aMissed = aRun.Results.Count(r => !Flagged(r) && !r.Grade.Passed);                                            // critic stayed silent on a broken change: a MISS
            var bLeaks = bRun.Results.Count(r => !r.Grade.Passed);                                                            // Arm B has no critic: every broken change ships unblocked

            // ── Cost: tokensPerSolve (agent+revise ONLY — critic's own tokens land nowhere on a standalone run) + usage coverage ──
            var (aTok, aCov) = TokensPerSolve(aRun, aSolved);
            var (bTok, bCov) = TokensPerSolve(bRun, bSolved);

            var aRoundsPairs = aRun.Results.Count(r => r.ReviseRounds > 0);

            // GATE: each arm independently clears the capability floor (a broken arm reds); the A−B delta is REPORTED, never gated.
            var outcome = aRate >= MinSolveRate && bRate >= MinSolveRate ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss;

            var note =
                $"CRITIC A/B over {SeedBenchmarkCorpus.Tasks.Count} tasks × modes — " +
                $"SOLVE (objective grade, arm-stable): Arm A {aSolved}/{aN} ({aRate:P0}) vs Arm B {bSolved}/{bN} ({bRate:P0}); " +
                $"paired discordant A-only:{aOnly} B-only:{bOnly} over {paired} shared pairs (delta rests on {Math.Abs(aOnly - bOnly)} net task(s) — REPORT-ONLY, not significance-tested at n≈18). " +
                $"INTERVENTIONS (Arm A): catchAndResolve {catchAndResolve} (flagged → revised → correct+shipped — the critic's productive VALUE, incl. false alarms the revise cleared), trueHold {trueHold} (caught broken, held for a human — VALUE), falseHold {falseHold} (held a CORRECT change — the burden), critic-missed {aMissed} broken; Arm B leaks {bLeaks} broken shipped unblocked. Critic value = catchAndResolve + trueHold; a higher flag count WITH a higher solve rate is the critic WORKING, not burden. " +
                $"RETRY DISCLOSURE: Arm A ΣReviseRounds {aReviseTotal} over {aRoundsPairs} pair(s) — the A−B delta is critic + its triggered retry COMBINED, not critic alone. " +
                $"COST: tokensPerSolve A {aTok} (coverage {aCov}) vs B {bTok} (coverage {bCov}) — Arm A total EXCLUDES critic/reviewer/co-sign model tokens (uncaptured on standalone runs); true Arm-A cost is strictly higher. " +
                $"per-mode A[{StrataOf(aRun)}] B[{StrataOf(bRun)}]. " +
                $"per-arm floor {MinSolveRate:P0} → {outcome}. Single-run estimate, n={aN} paired, NOT significance-tested; aggregate ≥~10 dispatches (n≈180) before any significance claim.";

            return (outcome, note);
        }, attempts: 1);
    }

    // ─── Metric helpers ───

    /// <summary>Pairs that reached a clean terminal (executed) — Succeeded OR the critic-flagged NeedsReview. A zero means the whole arm could not run (infra), never a capability verdict.</summary>
    private static int RanCleanly(CorpusBenchmarkRun run) =>
        run.Results.Count(r => r.RunStatus is AgentRunStatus.Succeeded or AgentRunStatus.NeedsReview);

    /// <summary>The arm-stable solve rate straight off the scorecard: numerator = Count(Grade.Passed), denominator = every result. NeedsReview never evicts (survivorship-proof); RunStatus is not the correctness oracle.</summary>
    private static (int Solved, int N, double Rate) RateOf(CorpusBenchmarkRun run) =>
        (run.Scorecard.Overall.Succeeded, run.Scorecard.Overall.Total, run.Scorecard.Overall.SuccessRate);

    /// <summary>Join the two arms on (TaskId,Mode), intersection-only, and count the discordant cells: A-solved/B-unsolved vs A-unsolved/B-solved. Concordant pairs cancel — the delta is exactly (aOnly − bOnly).</summary>
    private static (int AOnly, int BOnly, int Paired) PairedDiscordant(CorpusBenchmarkRun aRun, CorpusBenchmarkRun bRun)
    {
        var b = bRun.Results.ToDictionary(r => (r.TaskId, r.Mode), r => r.Grade.Passed);

        var aOnly = 0;
        var bOnly = 0;
        var paired = 0;

        foreach (var ar in aRun.Results)
        {
            if (!b.TryGetValue((ar.TaskId, ar.Mode), out var bPassed)) continue;   // intersection-only

            paired++;

            if (ar.Grade.Passed && !bPassed) aOnly++;
            else if (!ar.Grade.Passed && bPassed) bOnly++;
        }

        return (aOnly, bOnly, paired);
    }

    /// <summary>tokensPerSolve = Σ(input+output over pairs WITH usage) / solved, plus the usage-coverage fraction (a partial/asymmetric coverage makes the headline known-partial). "n/a" when nothing solved or no usage was reported.</summary>
    private static (string PerSolve, string Coverage) TokensPerSolve(CorpusBenchmarkRun run, int solved)
    {
        var withUsage = run.Results.Where(r => r.TokenUsage is not null).ToList();
        var total = withUsage.Sum(r => (long)r.TokenUsage!.InputTokens + r.TokenUsage!.OutputTokens);
        var perSolve = solved > 0 && withUsage.Count > 0 ? (total / solved).ToString() : "n/a";

        return (perSolve, $"{withUsage.Count}/{run.Results.Count}");
    }

    /// <summary>The per-mode strata (bench:cli / bench:cli-mcp) as "label k/n" — the critic delta is read within each mode AND pooled, never pooling across modes as if mode were noise.</summary>
    private static string StrataOf(CorpusBenchmarkRun run) =>
        string.Join(" ", run.Scorecard.Harnesses.OrderBy(h => h.Harness).Select(h => $"{h.Harness} {h.Succeeded}/{h.Total}"));

    // ─── Seeding ───

    /// <summary>The gateway <see cref="ModelCredential"/> the agent authenticates with (executor resolves it via <c>ModelCredentialId</c>; the ClaudeCodeHarness projects ANTHROPIC_BASE_URL/API_KEY).</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "critic-ab agent cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>An explicit pool ROW (<see cref="ModelCredentialModel"/>) over the SAME credential — passed as <c>selection.ReviewerModelId</c> so the output critic resolves the live model deterministically, never relying on an auto-pick that could fail open (leaving Arm A ≡ Arm B).</summary>
    private async Task<Guid> SeedReviewerPoolRowAsync(Guid teamId, Guid credId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
        return rowId;
    }

    private static async Task<bool> GitReadyAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }
}
