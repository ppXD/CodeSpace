using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// 🟢 High fidelity (real Postgres + the real <see cref="TaskSpecCompiler"/> + a LIVE model): spec preview's only
/// claim that a scripted test cannot settle — that the compiler ABSTAINS instead of guessing.
///
/// <para><b>The gap this closes.</b> Spec preview shipped with two integration tests, both about degradation: no
/// structured model yields a null suggestion, and a cross-team repository yields no grounding. Neither one ever
/// asks a model for anything. So the feature's entire value claim — a model turns a goal into launch-contract
/// suggestions — had no evidence at all, and its most important SAFETY property had none either.</para>
///
/// <para><b>Why abstention is the gating half.</b> A suggested acceptance check is EXECUTABLE argv: the launch runs
/// it as the acceptance floor. A check the model invented for a toolchain it cannot see does not merely go unused —
/// it fails, mints Failed/InfraUnknown noise, and WITHHOLDS work that was actually good. That is strictly worse than
/// offering no check, which is why <see cref="TaskSpecCompiler"/> is built to emit an empty list rather than a
/// plausible one. Whether a live model actually honours that is a fact about the prompt, and only a live call can
/// establish it.</para>
///
/// <para><b>Model-variance proof.</b> Nothing here pins a command, a phrase or a confidence value — only that the
/// executable list is EMPTY when nothing could have been confirmed, and that the model still explains itself. Any
/// model that guesses a build tool out of thin air fails; every model that declines passes, however it words it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Tasks")]
public sealed class RealModelSpecPreviewE2ETests
{
    private const string Provider = "Anthropic";

    private readonly PostgresFixture _fixture;

    public RealModelSpecPreviewE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task With_no_repository_to_ground_on_the_live_compiler_refuses_to_invent_an_acceptance_check()
    {
        if (ReadLiveSecretsOrSkip() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedBrainModelAsync(teamId, live.BaseUrl, live.ApiKey, live.Model);

        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            // A goal that BAITS a guess: it names tests directly, and every instinct says "npm test" or "pytest".
            // With no repository bound there is no listing, so any command the model names is invented.
            var result = await CompileAsync(teamId, "the tests for the payment retry path are flaky — make them deterministic", repositoryId: null);

            result.Grounded.ShouldBeFalse("no repository was bound, so nothing could have been read");

            if (result.Suggestion is not { } suggestion)
                return (true, $"{Provider} '{live.Model}': the compiler returned NO suggestion for an ungrounded goal — the safest possible answer, and the card renders nothing");

            // THE gating assertion. Criteria and rationale are prose the operator reads and edits; a check is argv the
            // launch EXECUTES, so it is the one field where a confident guess does damage.
            suggestion.AcceptanceChecks.ShouldBeEmpty(
                $"the live model invented an executable acceptance check with no repository to confirm it against: [{string.Join(" | ", suggestion.AcceptanceChecks)}]. "
              + $"Its own rationale was: '{suggestion.Rationale}'. A wrong argv does not go unused — it fails, mints Failed/InfraUnknown noise, and withholds work that was good.");

            suggestion.Rationale.ShouldNotBeNullOrWhiteSpace("a suggestion the operator cannot interrogate is worse than none — the card shows this line verbatim");
            suggestion.Confidence.ShouldBeInRange(0d, 1d, "the FE de-emphasizes low-confidence cards, so an out-of-range value would render nonsense");

            return (true, $"{Provider} '{live.Model}': ungrounded compile ABSTAINED from executable checks (criteria={suggestion.AcceptanceCriteria.Count}, confidence={suggestion.Confidence:0.00}) — rationale: '{Clip(suggestion.Rationale)}'");
        });
    }

    [Fact]
    public async Task A_live_compile_produces_a_suggestion_an_operator_could_actually_use()
    {
        if (ReadLiveSecretsOrSkip() is not { } live) return;

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedBrainModelAsync(teamId, live.BaseUrl, live.ApiKey, live.Model);

        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            // Reported, not gating: whether a model produces USEFUL definition-of-done bullets is a capability, and
            // gating on it would red the wire whenever a model was merely terse. What IS gating is the shape — a
            // suggestion the Launch modal cannot apply is a defect no matter how good the model was.
            var result = await CompileAsync(teamId, "add a --dry-run flag to the deploy script that prints the plan and exits 0 without touching the cluster", repositoryId: null);

            if (result.Suggestion is not { } suggestion)
                return (false, $"{Provider} '{live.Model}': the live compiler produced NOTHING for a concrete, well-specified goal — the card never appears, which is the whole feature not working");

            suggestion.AcceptanceCriteria.ShouldAllBe(c => !string.IsNullOrWhiteSpace(c), "a blank bullet renders as an empty row in the card");
            suggestion.TargetBranch.ShouldBeNull("the goal names no branch, so inventing one would silently retarget the operator's pull request");

            return (true, $"{Provider} '{live.Model}': compiled criteria={suggestion.AcceptanceCriteria.Count}, checks={suggestion.AcceptanceChecks.Count}, openPr={suggestion.OpenPullRequest?.ToString() ?? "none"}, confidence={suggestion.Confidence:0.00}");
        });
    }

    // ── Chassis ──────────────────────────────────────────────────────────────────────

    private async Task<Messages.Tasks.CompileTaskSpecResult> CompileAsync(Guid teamId, string goal, Guid? repositoryId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskSpecCompiler>().CompileAsync(teamId, goal, repositoryId, CancellationToken.None);
    }

    private async Task SeedBrainModelAsync(Guid teamId, string baseUrl, string apiKey, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "spec-preview e2e cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
    }

    private static LiveSecrets? ReadLiveSecretsOrSkip()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return null; }   // skip ≠ pass

        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        return new LiveSecrets(baseUrl!.TrimEnd('/'), apiKey!, model!);
    }

    private static string Clip(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private sealed record LiveSecrets(string BaseUrl, string ApiKey, string Model);
}
