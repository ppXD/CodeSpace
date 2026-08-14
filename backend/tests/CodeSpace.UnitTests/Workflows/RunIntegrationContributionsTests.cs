using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The run-sourced contribution mapping behind <c>git.integrate_run</c>: which of a run's produced work
/// integrates, in what order, carrying which patch bytes. Pins: only THIS repository's Agent-kind pushed/
/// patch-only manifests qualify (None-state and foreign-repo rows never leak in); order is agent-run creation
/// (deterministic apply order); an offloaded patch rides its artifact id while a SMALL patch falls back to the
/// result row's inline bytes (single-repo top-level, multi-repo per-alias) — without that fallback every
/// sub-threshold diff would read patch-less and block a clean integration; the label is the completion
/// protocol's own unit id, so the integration outcome names the same units the contract ledger stakes.
/// </summary>
[Trait("Category", "Unit")]
public class RunIntegrationContributionsTests
{
    private static readonly Guid Repo = Guid.NewGuid();

    [Fact]
    public void Only_this_repositories_produced_manifests_qualify()
    {
        var mine = Guid.NewGuid();
        var foreignRepo = Guid.NewGuid();
        var noneState = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[]
            {
                Manifest(mine, Repo, PublishState.Pushed, branch: "codespace/agent/a"),
                Manifest(Guid.NewGuid(), foreignRepo, PublishState.Pushed, branch: "codespace/agent/b"),
                Manifest(noneState, Repo, PublishState.None),
            },
            new[] { Work(mine, "agent", "map#0", 1), Work(noneState, "agent", "map#1", 2) });

        contributions.ShouldHaveSingleItem().ProducedBranch.ShouldBe("codespace/agent/a");
    }

    [Fact]
    public void Contributions_apply_in_agent_run_creation_order()
    {
        var late = Guid.NewGuid();
        var early = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(late, Repo, PublishState.Pushed), Manifest(early, Repo, PublishState.Pushed) },
            new[] { Work(late, "agent", "map#1", minute: 9), Work(early, "agent", "map#0", minute: 3) });

        contributions.Select(c => c.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1" });
    }

    [Fact]
    public void An_offloaded_patch_rides_its_artifact_id()
    {
        var runId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.PatchOnly, patchArtifactId: artifactId) },
            new[] { Work(runId, "agent", "map#0", 1) }).ShouldHaveSingleItem();

        contribution.PatchArtifactId.ShouldBe(artifactId);
        contribution.Patch.ShouldBe("", customMessage: "the integrator resolves the artifact itself — this layer never double-loads");
    }

    [Fact]
    public void A_small_patch_falls_back_to_the_results_inline_bytes()
    {
        var runId = Guid.NewGuid();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = "diff --git a/x b/x" }, AgentJson.Options);

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed) },
            new[] { Work(runId, "agent", "map#0", 1, resultJson) }).ShouldHaveSingleItem();

        contribution.Patch.ShouldBe("diff --git a/x b/x");
    }

    [Fact]
    public void A_multi_repo_result_matches_the_inline_patch_by_alias()
    {
        var runId = Guid.NewGuid();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "api", Patch = "diff-api" },
                new RepositoryRunResult { Alias = "web", Patch = "diff-web" },
            },
        }, AgentJson.Options);

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, alias: "web") },
            new[] { Work(runId, "agent", "map#0", 1, resultJson) }).ShouldHaveSingleItem();

        contribution.Patch.ShouldBe("diff-web");
    }

    [Fact]
    public void An_unparseable_result_contributes_empty_bytes_never_a_guess()
    {
        var runId = Guid.NewGuid();

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/x") },
            new[] { Work(runId, "agent", "map#0", 1, "{not-json") }).ShouldHaveSingleItem();

        contribution.Patch.ShouldBe("", customMessage: "the integrator names the contribution unintegrable — this layer never fabricates bytes");
        contribution.ProducedBranch.ShouldBe("codespace/agent/x");
    }

    [Fact]
    public void A_manifest_with_no_agent_row_is_dropped()
    {
        RunIntegrationContributions.Build(Repo,
            new[] { Manifest(Guid.NewGuid(), Repo, PublishState.Pushed) },
            Array.Empty<RunAgentWork>()).ShouldBeEmpty();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static PublishManifest Manifest(Guid agentRunId, Guid repositoryId, PublishState state, string? branch = null, Guid? patchArtifactId = null, string alias = "primary") => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = PublishManifestKind.Agent, AgentRunId = agentRunId,
        RepositoryId = repositoryId, RepositoryAlias = alias, BaseSha = "base1", Branch = branch,
        PatchArtifactId = patchArtifactId, PublishStateValue = state,
    };

    private static RunAgentWork Work(Guid agentRunId, string nodeId, string iterationKey, int minute, string? resultJson = null) =>
        new(agentRunId, nodeId, iterationKey, new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero), resultJson);
}
