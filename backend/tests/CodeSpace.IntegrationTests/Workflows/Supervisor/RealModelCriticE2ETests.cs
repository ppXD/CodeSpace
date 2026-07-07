using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Real-model E2E for the generic critic primitive: a LIVE reviewer model reviews a deliberately-thin plan through the
/// REAL <c>CustomClient</c> transport over real Postgres. The assertion is STRUCTURAL (a verdict was produced — not
/// Failed — with the right mode); the QUALITY (what it flagged / how it critiqued) is reported, not gated, since it
/// depends on the model. Gated on <c>CODESPACE_LLM_*</c> (green-skip without it). A failed verdict (gateway infra) is
/// non-gating, mirroring the other real-model suites.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
public sealed class RealModelCriticE2ETests
{
    private const string Custom = "Custom";

    private const string ThinPlan =
        "Goal: add user authentication to the API.\n" +
        "Recommended execution shape: coding\n" +
        "Subtasks:\n  - Do auth: implement login\n" +
        "Success criteria:\n  - it works\n";   // deliberately thin: no tests, no security, vague — a reviewer should flag this

    private readonly PostgresFixture _fixture;

    public RealModelCriticE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(ReviewMode.Gate)]
    [InlineData(ReviewMode.Improve)]
    public async Task A_live_reviewer_produces_a_verdict_for_a_thin_plan(ReviewMode mode)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip

        var teamId = await SeedTeamAsync();
        var reviewerRowId = await SeedCredentialedModelAsync(teamId, model, baseUrl, apiKey);

        await RealModelGate.AssessLiveAsync(Custom, async () =>
        {
            using var scope = _fixture.BeginScope();
            var critic = new LlmStructuredCritic(RealModelLiveWire.Registry(), scope.Resolve<CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector>());

            var verdict = await critic.ReviewAsync(
                new CriticRequest { Mode = mode, ArtifactKind = "workflow plan", Artifact = ThinPlan, Goal = "Add secure user authentication to the API." },
                teamId, reviewerRowId, CancellationToken.None);

            // A failed verdict is most likely a gateway-infra blip → non-gating. A produced verdict must have the right
            // mode + carry its mode's payload (GATE → a rationale; IMPROVE → a non-empty critique). The QUALITY is reported.
            if (verdict.Failed)
                return (true, $"{mode}: the reviewer produced no verdict (gateway infra) — not gating");

            verdict.Mode.ShouldBe(mode);

            var ok = mode == ReviewMode.Improve
                ? !string.IsNullOrWhiteSpace(verdict.Critique)
                : !string.IsNullOrWhiteSpace(verdict.Rationale);

            return (ok, $"{mode}: verdict produced (approved={verdict.Approved}, score={verdict.Score}, issues={verdict.Issues.Count}) — {verdict.Rationale}");
        });
    }

    /// <summary>The S6 EFFECTIVENESS gate: the same live reviewer must DISCRIMINATE — reject an agent change whose diff plants an egregious, material flaw against the goal, and approve a small, sound one. This is the claim the revise loop rests on (a critic that approves garbage feeds the loop nothing); unlike the thin-plan probe above, the verdicts here ARE gated. Infra failures (no verdict) stay non-gating.</summary>
    [Fact]
    public async Task A_live_reviewer_discriminates_a_planted_flaw_from_a_clean_change()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip

        const string goal = "Reject empty or missing passwords in the login endpoint.";

        const string flawedChange =
            "Agent summary: added the password validation\n\n" +
            "Changed files (1): src/Api/LoginController.cs\n\n" +
            "Diff:\n" +
            "diff --git a/src/Api/LoginController.cs b/src/Api/LoginController.cs\n" +
            "+            // TODO placeholder — accepts every password, validation not actually implemented\n" +
            "+            if (password == null || password.Length >= 0) return Ok(IssueToken(user));\n";

        const string cleanChange =
            "Agent summary: reject empty or missing passwords with 400\n\n" +
            "Changed files (2): src/Api/LoginController.cs, tests/LoginControllerTests.cs\n\n" +
            "Diff:\n" +
            "diff --git a/src/Api/LoginController.cs b/src/Api/LoginController.cs\n" +
            "+            if (string.IsNullOrEmpty(password)) return BadRequest(\"password is required\");\n" +
            "diff --git a/tests/LoginControllerTests.cs b/tests/LoginControllerTests.cs\n" +
            "+        [Theory] [InlineData(null)] [InlineData(\"\")] public async Task Rejects_missing_password(string? password) => (await Post(password)).StatusCode.ShouldBe(400);\n";

        var teamId = await SeedTeamAsync();
        var reviewerRowId = await SeedCredentialedModelAsync(teamId, model, baseUrl, apiKey);

        await RealModelGate.AssessLiveAsync(Custom, async () =>
        {
            using var scope = _fixture.BeginScope();
            var critic = new LlmStructuredCritic(RealModelLiveWire.Registry(), scope.Resolve<CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector>());

            var flawed = await critic.ReviewAsync(
                new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = flawedChange, Goal = goal },
                teamId, reviewerRowId, CancellationToken.None);
            var clean = await critic.ReviewAsync(
                new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = cleanChange, Goal = goal },
                teamId, reviewerRowId, CancellationToken.None);

            if (flawed.Failed || clean.Failed)
                return (true, "the reviewer produced no verdict on one side (gateway infra) — not gating");

            var discriminated = !flawed.Approved && clean.Approved;

            return (discriminated,
                $"flawed: approved={flawed.Approved} (want false; issues={flawed.Issues.Count}) · clean: approved={clean.Approved} (want true) — {flawed.Rationale}");
        });
    }

    /// <summary>
    /// The P1 SEVERITY-CALIBRATION gate: the same live reviewer must grade PROPORTIONATELY — mark a functionally FATAL
    /// flaw a BLOCKER (so the gate still halts it), and a purely COSMETIC issue on otherwise-correct code NOT a blocker
    /// (so the artifact is NOT halted). This is the claim the calibration fix rests on — that adversarial review stops
    /// blocking on nitpicks WITHOUT going blind to real defects. The verdicts ARE gated; infra failures stay non-gating.
    /// </summary>
    [Fact]
    public async Task A_live_reviewer_blocks_a_fatal_flaw_but_not_a_cosmetic_one()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip

        const string goal = "Reject empty or missing passwords in the login endpoint.";

        // FATAL: functionally accepts every password — it does NOT achieve the goal (a Blocker).
        const string fatalChange =
            "Agent summary: added password validation\n\n" +
            "Changed files (1): src/Api/LoginController.cs\n\n" +
            "Diff:\n" +
            "diff --git a/src/Api/LoginController.cs b/src/Api/LoginController.cs\n" +
            "+            // accepts every password — validation is not actually implemented\n" +
            "+            if (password == null || password.Length >= 0) return Ok(IssueToken(user));\n";

        // COSMETIC: correctly rejects empty/missing passwords (ACHIEVES the goal) with a covering test — the only
        // quibble is a terse local variable name. A well-calibrated reviewer marks this minor and does NOT block it.
        const string cosmeticChange =
            "Agent summary: reject empty or missing passwords with 400, with a covering test\n\n" +
            "Changed files (2): src/Api/LoginController.cs, tests/LoginControllerTests.cs\n\n" +
            "Diff:\n" +
            "diff --git a/src/Api/LoginController.cs b/src/Api/LoginController.cs\n" +
            "+            if (string.IsNullOrEmpty(pwd)) return BadRequest(\"password is required\");\n" +
            "diff --git a/tests/LoginControllerTests.cs b/tests/LoginControllerTests.cs\n" +
            "+        [Theory] [InlineData(null)] [InlineData(\"\")] public async Task Rejects_missing_password(string? pwd) => (await Post(pwd)).StatusCode.ShouldBe(400);\n";

        var teamId = await SeedTeamAsync();
        var reviewerRowId = await SeedCredentialedModelAsync(teamId, model, baseUrl, apiKey);

        await RealModelGate.AssessLiveAsync(Custom, async () =>
        {
            using var scope = _fixture.BeginScope();
            var critic = new LlmStructuredCritic(RealModelLiveWire.Registry(), scope.Resolve<CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector>());

            var fatal = await critic.ReviewAsync(
                new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = fatalChange, Goal = goal },
                teamId, reviewerRowId, CancellationToken.None);
            var cosmetic = await critic.ReviewAsync(
                new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = cosmeticChange, Goal = goal },
                teamId, reviewerRowId, CancellationToken.None);

            if (fatal.Failed || cosmetic.Failed)
                return (true, "the reviewer produced no verdict on one side (gateway infra) — not gating");

            // Severity-authoritative: the fatal flaw must be BLOCKED (a Blocker issue → Approved=false), and the
            // cosmetic-only change must NOT be blocked (Approved=true — the calibration fix, a real model no longer
            // halting sound code over a naming nit).
            var calibrated = !fatal.Approved
                && fatal.Issues.Any(i => i.Severity == CriticSeverity.Blocker)
                && cosmetic.Approved;

            return (calibrated,
                $"fatal: approved={fatal.Approved} (want false), severities=[{string.Join(",", fatal.Issues.Select(i => i.Severity))}] · " +
                $"cosmetic: approved={cosmetic.Approved} (want true), severities=[{string.Join(",", cosmetic.Issues.Select(i => i.Severity))}] — {fatal.Rationale}");
        });
    }

    /// <summary>
    /// The P1b-2 CONVERGENCE prerequisite under a LIVE model: an oscillation-stop rests on the critic RE-FLAGGING an
    /// unremoved flaw (if it didn't, there'd be nothing to converge on). A real reviewer must flag a flaw, then flag a
    /// SUPERFICIALLY-revised change (the flaw untouched, only a comment reworded) AGAIN — the gate. The convergence
    /// module's assessment over the two live verdicts (did the fingerprint catch the persistence?) is REPORTED, not
    /// gated: text-fingerprinting catches re-wording, not arbitrary semantic paraphrase, so a live match is a bonus
    /// signal, never a flaky gate. Infra failures (no verdict) stay non-gating.
    /// </summary>
    [Fact]
    public async Task A_live_reviewer_consistently_re_flags_an_unremoved_flaw()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip

        const string goal = "Reject empty or missing passwords in the login endpoint.";

        // The flaw: validation that accepts every password. Both versions carry it UNCHANGED — only the comment differs.
        const string firstChange =
            "Agent summary: added password validation\n\nDiff:\n" +
            "+            // validate the password\n" +
            "+            if (password == null || password.Length >= 0) return Ok(IssueToken(user));\n";

        const string superficiallyRevised =
            "Agent summary: revised the password validation\n\nDiff:\n" +
            "+            // now with a clearer comment about checking the password value\n" +
            "+            if (password == null || password.Length >= 0) return Ok(IssueToken(user));\n";

        var teamId = await SeedTeamAsync();
        var reviewerRowId = await SeedCredentialedModelAsync(teamId, model, baseUrl, apiKey);

        await RealModelGate.AssessLiveAsync(Custom, async () =>
        {
            using var scope = _fixture.BeginScope();
            var critic = new LlmStructuredCritic(RealModelLiveWire.Registry(), scope.Resolve<CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector>());

            var first = await critic.ReviewAsync(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = firstChange, Goal = goal }, teamId, reviewerRowId, CancellationToken.None);
            var second = await critic.ReviewAsync(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = superficiallyRevised, Goal = goal }, teamId, reviewerRowId, CancellationToken.None);

            if (first.Failed || second.Failed)
                return (true, "the reviewer produced no verdict on one side (gateway infra) — not gating");

            // GATE: the unremoved flaw is re-flagged both times — the consistency convergence relies on.
            var consistentlyFlagged = !first.Approved && !second.Approved;

            // REPORT (non-gating): whether the convergence fingerprint recognised the persisting flaw across the two
            // independent live reviews — a bonus over the deterministic integration proof, never a flaky gate.
            var report = CriticConvergence.Assess(first.Issues, second.Issues);

            return (consistentlyFlagged,
                $"first: approved={first.Approved} (issues={first.Issues.Count}) · second: approved={second.Approved} (issues={second.Issues.Count}) · " +
                $"convergence persisting={report.Persisting.Count} resolved={report.Resolved.Count} introduced={report.Introduced.Count}");
        });
    }

    /// <summary>The S7 RUBRIC-JUDGE effectiveness gate: the live judge must answer per-criterion BINARY verdicts that
    /// DISCRIMINATE — an artifact that plainly satisfies both criteria gets both met, one that satisfies neither gets
    /// neither. This is what the non-coding oracle rests on; the verdicts ARE gated (infra failures stay non-gating).</summary>
    [Fact]
    public async Task A_live_rubric_judge_discriminates_per_criterion()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip

        var rubric = new CodeSpace.Messages.Agents.AcceptanceRubric
        {
            Criteria = new[]
            {
                new CodeSpace.Messages.Agents.AcceptanceRubricCriterion { Id = "competitors", Requirement = "names at least three specific competitors" },
                new CodeSpace.Messages.Agents.AcceptanceRubricCriterion { Id = "sources", Requirement = "cites at least one source with a URL" },
            },
        };

        const string strong =
            "=== report.md ===\nThe main competitors are Acme Corp, Globex Ltd, and Initech GmbH. Each holds >10% share.\n" +
            "Source: the 2025 market survey (https://example.com/market-2025).\n";

        const string weak =
            "=== report.md ===\nThe market is competitive and several firms operate in it. More research is needed.\n";

        var teamId = await SeedTeamAsync();
        var judgeRowId = await SeedCredentialedModelAsync(teamId, model, baseUrl, apiKey);

        await RealModelGate.AssessLiveAsync(Custom, async () =>
        {
            using var scope = _fixture.BeginScope();
            var judge = new CodeSpace.Core.Services.Review.LlmRubricJudge(RealModelLiveWire.Registry(), scope.Resolve<CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector>());

            var strongVerdict = await judge.JudgeAsync(rubric with { JudgeModelId = judgeRowId }, strong, "research the competitive landscape", teamId, CancellationToken.None);
            var weakVerdict = await judge.JudgeAsync(rubric with { JudgeModelId = judgeRowId }, weak, "research the competitive landscape", teamId, CancellationToken.None);

            if (strongVerdict.Failed || weakVerdict.Failed)
                return (true, "the judge produced no verdict on one side (gateway infra) — not gating");

            var discriminated = strongVerdict.Criteria.All(c => c.Met) && weakVerdict.Criteria.All(c => !c.Met);

            return (discriminated,
                $"strong: [{string.Join(", ", strongVerdict.Criteria.Select(c => $"{c.Id}={c.Met}"))}] (want all true) · " +
                $"weak: [{string.Join(", ", weakVerdict.Criteria.Select(c => $"{c.Id}={c.Met}"))}] (want all false)");
        });
    }

    // ─── Helpers ───

    private async Task<Guid> SeedCredentialedModelAsync(Guid teamId, string modelId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Custom, DisplayName = "live reviewer",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
        });
        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
        return rowId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"rmcritic-{userId:N}@test.local", Name = $"rmcritic-{userId:N}" });
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"rmcritic-{teamId:N}", Name = "RM Critic Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }
}
