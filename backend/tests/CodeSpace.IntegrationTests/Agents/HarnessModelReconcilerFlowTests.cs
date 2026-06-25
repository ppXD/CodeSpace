using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The always-runnable fallback (兜底) against the REAL harness registry (codex-cli + claude-code) and real Postgres:
/// when an agent's authored harness can't drive its pinned model credential's provider — the impossible pairing that
/// otherwise fails every agent at execution — <see cref="IHarnessModelReconciler"/> repairs it to a harness that can,
/// so the agent still runs. A compatible / unpinned / foreign credential is left untouched.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class HarnessModelReconcilerFlowTests
{
    private readonly PostgresFixture _fixture;

    public HarnessModelReconcilerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_codex_default_with_an_Anthropic_pinned_credential_reconciles_to_claude()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credentialId = await SeedCredentialAsync(teamId, "Anthropic");
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", ModelCredentialId = credentialId };

        HarnessReconciliation result;
        using (var scope = _fixture.BeginScope())
            result = await scope.Resolve<IHarnessModelReconciler>().ReconcileAsync(task, teamId, CancellationToken.None);

        result.Repaired.ShouldBeTrue();
        result.HarnessKind.ShouldBe("claude-code", "codex-cli cannot drive an Anthropic credential — reconcile so the agent still runs");
        result.Note.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_compatible_pinned_credential_keeps_the_authored_harness()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credentialId = await SeedCredentialAsync(teamId, "OpenAI");
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", ModelCredentialId = credentialId };

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IHarnessModelReconciler>().ReconcileAsync(task, teamId, CancellationToken.None);

        result.Repaired.ShouldBeFalse();
        result.HarnessKind.ShouldBe("codex-cli");
    }

    [Fact]
    public async Task An_unpinned_task_keeps_the_authored_harness()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var task = new AgentTask { Goal = "g", Harness = "codex-cli" };

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IHarnessModelReconciler>().ReconcileAsync(task, teamId, CancellationToken.None);

        result.Repaired.ShouldBeFalse();
        result.HarnessKind.ShouldBe("codex-cli", "no pin → the team-default path resolves a compatible credential by construction");
    }

    [Fact]
    public async Task An_unpinned_loose_model_name_reconciles_the_harness_to_the_models_provider()
    {
        // The planner authors a loose model NAME with no credential pin. The model lives in the pool under an Anthropic
        // credential, but the authored harness is codex-cli (can't drive Anthropic). The 兜底 aligns the harness with
        // the named model's provider so the (harness, model) pair runs instead of failing.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedPoolModelAsync(teamId, "claude-sonnet", "Anthropic");
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", Model = "claude-sonnet" };

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IHarnessModelReconciler>().ReconcileAsync(task, teamId, CancellationToken.None);

        result.Repaired.ShouldBeTrue();
        result.HarnessKind.ShouldBe("claude-code", "codex-cli can't drive the Anthropic model the planner named — reconcile so the agent still runs");
    }

    [Fact]
    public async Task An_unpinned_model_name_not_in_the_pool_keeps_the_authored_harness()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", Model = "ghost-model" };

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IHarnessModelReconciler>().ReconcileAsync(task, teamId, CancellationToken.None);

        result.Repaired.ShouldBeFalse();
        result.HarnessKind.ShouldBe("codex-cli", "a model name not in the pool leaves the authored harness for the resolver's default");
    }

    [Fact]
    public async Task A_foreign_or_missing_credential_is_left_for_the_resolver_to_reject()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", ModelCredentialId = Guid.NewGuid() };

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IHarnessModelReconciler>().ReconcileAsync(task, teamId, CancellationToken.None);

        result.Repaired.ShouldBeFalse();
        result.HarnessKind.ShouldBe("codex-cli", "a missing credential is not a harness mismatch — leave it for the resolver's precise error");
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider)
    {
        var id = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = provider, DisplayName = provider + " cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("k"), Status = CredentialStatus.Active,
        });

        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Seed an enabled pool model (a <c>ModelCredentialModel</c> row) under a fresh active credential of <paramref name="provider"/> — the loose-name reconciliation resolves the model's provider from this row.</summary>
    private async Task SeedPoolModelAsync(Guid teamId, string modelId, string provider)
    {
        var credentialId = await SeedCredentialAsync(teamId, provider);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ModelCredentialModel.Add(new ModelCredentialModel
        {
            Id = Guid.NewGuid(), ModelCredentialId = credentialId, ModelId = modelId, Enabled = true,
        });

        await db.SaveChangesAsync();
    }
}
