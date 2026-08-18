using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
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
///
/// <para>And ONE attempt per unit: a retried subtask respawns a fresh agent run whose manifest row lands beside
/// the abandoned attempt's, so without the reduction the unit conflicted with ITSELF and parked the run. Pinned
/// as a reduction over superseded DUPLICATES, never an outcome filter — a lone failed-but-captured attempt still
/// contributes, and a fan-out's distinct units all survive.</para>
///
/// <para>Pinned with it, the LANE FENCE that keeps that reduction honest: a supervisor stamps one turn cell on all
/// K agents it spawns in a turn, so a supervisor-staked row is never reduced against a cell-sharing peer — K
/// concurrent deliverables would otherwise collapse to one and silently lose K-1. An unreadable task envelope
/// takes the same conservative branch. And the invariant's real scope: one attempt per (unit, REPOSITORY).</para>
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

    // ─── One attempt per unit: a retry must not integrate against its own abandoned attempt ──

    [Fact]
    public void A_retried_unit_contributes_only_its_latest_attempt()
    {
        var attempt1 = Guid.NewGuid();
        var attempt2 = Guid.NewGuid();

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(attempt1, Repo, PublishState.PatchOnly), Manifest(attempt2, Repo, PublishState.Pushed, branch: "codespace/agent/a2") },
            new[] { Work(attempt1, "agent", "map#0", minute: 1, Patch("half-done")), Work(attempt2, "agent", "map#0", minute: 7, Patch("finished")) })
            .ShouldHaveSingleItem(customMessage: "a respawned attempt SUPERSEDES the abandoned one — contributing both makes the unit conflict with itself and parks the run");

        contribution.Patch.ShouldBe("finished");
        contribution.ProducedBranch.ShouldBe("codespace/agent/a2");
    }

    [Fact]
    public void Distinct_units_each_keep_their_own_contribution()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(first, Repo, PublishState.Pushed), Manifest(second, Repo, PublishState.Pushed) },
            new[] { Work(first, "agent", "map#0", minute: 1), Work(second, "agent", "map#1", minute: 2) });

        contributions.Select(c => c.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1" },
            customMessage: "the reduction keys on the UNIT — a fan-out's siblings are different units and must all survive it");
    }

    [Fact]
    public void A_lone_failed_attempt_that_captured_a_diff_still_contributes()
    {
        var runId = Guid.NewGuid();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "timeout", Patch = "partial" }, AgentJson.Options);

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.PatchOnly) },
            new[] { Work(runId, "agent", "map#0", minute: 1, resultJson) })
            .ShouldHaveSingleItem(customMessage: "superseded duplicates are removed — a unit's only attempt is never filtered by how it ended");

        contribution.Patch.ShouldBe("partial");
    }

    [Fact]
    public void A_surviving_attempts_sibling_alias_rows_all_stay()
    {
        var attempt1 = Guid.NewGuid();
        var attempt2 = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[]
            {
                Manifest(attempt1, Repo, PublishState.Pushed, alias: "repo"),
                Manifest(attempt2, Repo, PublishState.Pushed, patchArtifactId: Guid.NewGuid(), alias: "repo"),
                Manifest(attempt2, Repo, PublishState.Pushed, patchArtifactId: Guid.NewGuid(), alias: "repo-2"),
            },
            new[] { Work(attempt1, "agent", "map#0", minute: 1), Work(attempt2, "agent", "map#0", minute: 7) });

        contributions.Count.ShouldBe(2, "the reduction drops superseded ATTEMPTS, never the surviving attempt's own per-alias rows");
    }

    [Fact]
    public void The_order_and_the_surviving_attempt_repeat_across_builds()
    {
        var early = Guid.NewGuid();
        var retriedFirst = Guid.NewGuid();
        var retriedLast = Guid.NewGuid();

        // The two map#1 attempts share a LABEL, so a label-only assertion cannot see which one survived a reversal —
        // they carry distinct branches and bytes precisely so an order-dependent pick would fail this test.
        var manifests = new[]
        {
            Manifest(retriedLast, Repo, PublishState.Pushed, branch: "codespace/agent/a2"),
            Manifest(early, Repo, PublishState.Pushed, branch: "codespace/agent/e"),
            Manifest(retriedFirst, Repo, PublishState.Pushed, branch: "codespace/agent/a1"),
        };
        var work = new[] { Work(retriedLast, "agent", "map#1", minute: 8, Patch("finished")), Work(early, "agent", "map#0", minute: 2), Work(retriedFirst, "agent", "map#1", minute: 4, Patch("half-done")) };

        var first = RunIntegrationContributions.Build(Repo, manifests, work);
        var second = RunIntegrationContributions.Build(Repo, manifests.Reverse().ToArray(), work.Reverse().ToArray());

        first.Select(c => c.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1" }, customMessage: "the surviving attempts still apply in agent-run creation order");
        second.Select(c => c.Label).ShouldBe(first.Select(c => c.Label), customMessage: "the apply order — and so the integration outcome — is a function of the rows, never of the query's row order");
        second.Select(c => c.ProducedBranch).ShouldBe(first.Select(c => c.ProducedBranch), customMessage: "WHICH attempt survives is a function of the rows too — reversing the query's row order must not swap the retried unit's branch");
        second.Select(c => c.Patch).ShouldBe(first.Select(c => c.Patch), customMessage: "and the bytes that reach the integrator with it");
        second.Last().ProducedBranch.ShouldBe("codespace/agent/a2", customMessage: "the newest attempt by agent-run creation is the one that survives, whichever order the rows arrived in");
    }

    // ─── The lane fence: a supervisor turn cell is not a unit ────────────────────────

    [Fact]
    public void A_supervisor_turns_parallel_agents_all_contribute()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // RealSupervisorActionExecutor stamps ONE turn cell (<nodeId>#turn{N}) on every agent of a turn, so these two
        // share a (node, iteration) cell while being concurrent deliverables — reducing them drops real work.
        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(first, Repo, PublishState.Pushed, branch: "codespace/agent/s1"), Manifest(second, Repo, PublishState.Pushed, branch: "codespace/agent/s2") },
            new[] { PlannedSupervisorWork(first, "sup#turn1", "subtask-a", minute: 1), PlannedSupervisorWork(second, "sup#turn1", "subtask-b", minute: 2) });

        contributions.Select(c => c.ProducedBranch).ShouldBe(new[] { "codespace/agent/s1", "codespace/agent/s2" },
            customMessage: "the supervisor's K parallel agents share a TURN cell, not a unit — collapsing them to one silently loses K-1 unsuperseded contributions");
        contributions.Select(c => c.Label).ShouldBe(new[] { "sup#sup#turn1", "sup#sup#turn1" },
            customMessage: "and they still SHARE the turn-cell label downstream — the fence changes which rows survive, never what the run calls them");
    }

    [Fact]
    public void A_plan_less_supervisor_spawn_is_fenced_off_too()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // The plan-lineage WorkUnit stamp is only written when a plan decision exists (Spawn.cs), so a plan-less
        // spawn's rows carry SubtaskId ALONE — a fence reading only WorkUnit would collapse this pair.
        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(first, Repo, PublishState.Pushed), Manifest(second, Repo, PublishState.Pushed) },
            new[] { SupervisorWork(first, "sup#turn1", "subtask-a", minute: 1), SupervisorWork(second, "sup#turn1", "subtask-b", minute: 2) });

        contributions.Count.ShouldBe(2, "SubtaskId alone marks the supervisor lane when no plan decision stamped a WorkUnit — the same K-1 loss otherwise returns through the plan-less spawn");
    }

    [Fact]
    public void An_unreadable_task_envelope_is_never_reduced()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(first, Repo, PublishState.Pushed), Manifest(second, Repo, PublishState.Pushed) },
            new[] { new RunAgentWork(first, "agent", "map#0", At(1), null, "{not-json"), new RunAgentWork(second, "agent", "map#0", At(2), null, null) });

        contributions.Count.ShouldBe(2, "an unknown lane must not be reduced — an extra integrator conflict is routable, losing produced work is not");
    }

    // ─── Scope: the invariant is one attempt per (unit, REPOSITORY) ─────────────────

    [Fact]
    public void A_repository_the_surviving_attempt_never_touched_keeps_the_abandoned_attempts_work()
    {
        var abandoned = Guid.NewGuid();
        var respawned = Guid.NewGuid();
        var otherRepo = Guid.NewGuid();

        var manifests = new[]
        {
            Manifest(abandoned, Repo, PublishState.Pushed, branch: "codespace/agent/a1"),
            Manifest(abandoned, otherRepo, PublishState.Pushed, branch: "codespace/agent/a1-other"),
            Manifest(respawned, Repo, PublishState.Pushed, branch: "codespace/agent/a2"),
        };
        var work = new[] { Work(abandoned, "agent", "map#0", minute: 1), Work(respawned, "agent", "map#0", minute: 7) };

        RunIntegrationContributions.Build(Repo, manifests, work).ShouldHaveSingleItem().ProducedBranch.ShouldBe("codespace/agent/a2");

        RunIntegrationContributions.Build(otherRepo, manifests, work).ShouldHaveSingleItem(customMessage: "the reduction runs AFTER the repository filter — deliberately, since the retry produced nothing here to supersede this with")
            .ProducedBranch.ShouldBe("codespace/agent/a1-other");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static string Patch(string patch) =>
        JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = patch }, AgentJson.Options);

    private static PublishManifest Manifest(Guid agentRunId, Guid repositoryId, PublishState state, string? branch = null, Guid? patchArtifactId = null, string alias = "primary") => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = PublishManifestKind.Agent, AgentRunId = agentRunId,
        RepositoryId = repositoryId, RepositoryAlias = alias, BaseSha = "base1", Branch = branch,
        PatchArtifactId = patchArtifactId, PublishStateValue = state,
    };

    /// <summary>A row from a lane whose (node, iteration) cell IS the unit — a map branch / loop iteration / top-level node, whose task envelope carries no supervisor stamp.</summary>
    private static RunAgentWork Work(Guid agentRunId, string nodeId, string iterationKey, int minute, string? resultJson = null) =>
        new(agentRunId, nodeId, iterationKey, At(minute), resultJson, TaskJson(subtaskId: null, workUnit: null));

    /// <summary>A row the SUPERVISOR staked on a plan-less spawn: its cell is the whole turn, and only <c>SubtaskId</c> names the agent's own unit.</summary>
    private static RunAgentWork SupervisorWork(Guid agentRunId, string turnCell, string subtaskId, int minute) =>
        new(agentRunId, "sup", turnCell, At(minute), null, TaskJson(subtaskId, workUnit: null));

    /// <summary>The plan-lineage <c>WorkUnit</c> stamp — the coordinate the completion composer fences on — carried ALONE. A real planned spawn writes it beside <c>SubtaskId</c>; isolating it here keeps that half of the fence independently falsifiable.</summary>
    private static RunAgentWork PlannedSupervisorWork(Guid agentRunId, string turnCell, string unitId, int minute) =>
        new(agentRunId, "sup", turnCell, At(minute), null, TaskJson(subtaskId: null, new WorkUnitRef { WorkPlanId = Guid.NewGuid(), PlanVersion = 1, UnitId = unitId }));

    private static string TaskJson(string? subtaskId, WorkUnitRef? workUnit) =>
        JsonSerializer.Serialize(new AgentTask { Goal = "do the work", Harness = "codex-cli", SubtaskId = subtaskId, WorkUnit = workUnit }, AgentJson.Options);

    private static DateTimeOffset At(int minute) => new(2026, 1, 1, 0, minute, 0, TimeSpan.Zero);
}
