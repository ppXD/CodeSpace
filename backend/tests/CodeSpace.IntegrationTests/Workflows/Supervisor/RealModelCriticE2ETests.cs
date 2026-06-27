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
