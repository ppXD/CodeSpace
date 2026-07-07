using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the agent.code resume payload projection (<see cref="WorkflowResumeAgentRunCompletionNotifier"/>.BuildResumePayload)
/// + the shared <see cref="RepositoryRunResult.WithoutDiff"/> projection. The crown jewel (resolver loop #379 S7-C0):
/// the per-repo DIFF the executor now captures must NOT leak into the node's observable <c>outputs_jsonb</c> — the node
/// output exposes each repo's branch + base for a downstream git.open_change_set, exactly as it already excludes the
/// top-level patch, so a large per-repo diff never bloats the resolved output bag.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowResumeAgentRunPayloadTests
{
    [Fact]
    public void WithoutDiff_clears_the_per_repo_patch_keeping_the_bounded_facts()
    {
        var repoId = Guid.NewGuid();
        var stripped = new RepositoryRunResult
        {
            Alias = "web", RepositoryId = repoId, ChangedFiles = new[] { "web.txt" }, ProducedBranch = "codespace/agent/web",
            BaseSha = "base-web", BaseBranch = "main", Patch = "diff --git a/web.txt\n+unbounded", PatchArtifactId = Guid.NewGuid(), Access = WorkspaceAccess.Write,
        }.WithoutDiff();

        stripped.Patch.ShouldBe("", "the unbounded diff is cleared");
        stripped.PatchArtifactId.ShouldBeNull("the artifact ref is cleared too");

        stripped.Alias.ShouldBe("web", "the bounded facts are preserved");
        stripped.RepositoryId.ShouldBe(repoId);
        stripped.ChangedFiles.ShouldBe(new[] { "web.txt" });
        stripped.ProducedBranch.ShouldBe("codespace/agent/web");
        stripped.BaseSha.ShouldBe("base-web");
        stripped.BaseBranch.ShouldBe("main");
        stripped.Access.ShouldBe(WorkspaceAccess.Write);
    }

    [Fact]
    public void BuildResumePayload_carries_the_exit_reason_for_the_retry_verdict()
    {
        // The node's retry verdict keys on exitReason (a fail-closed "acceptance-failed" must not respawn) —
        // dropping it from the payload would silently turn every verdict failure back into a billed respawn loop.
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = AgentAcceptanceContract.FailClosedExitReason, Error = "The acceptance check did not pass: tests-failed-exit-1" };
        var run = new AgentRun { Status = AgentRunStatus.Failed, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options) };

        var payload = JsonDocument.Parse(WorkflowResumeAgentRunCompletionNotifier.BuildResumePayload(run)).RootElement;

        payload.GetProperty("exitReason").GetString().ShouldBe("acceptance-failed");
        payload.GetProperty("status").GetString().ShouldBe("Failed");
    }

    [Fact]
    public void BuildResumePayload_surfaces_per_repo_branches_WITHOUT_leaking_the_diff()
    {
        // S7-C0 — a multi-repo run's resume payload (the agent.code node output source) carries each repo's branch +
        // base for git.open_change_set, but the per-repo diff is stripped so a large change set never bloats outputs_jsonb.
        var result = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = "coordinated change",
            ProducedBranch = "codespace/agent/web",
            Patch = "top-level diff that is already excluded from the node output",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), ProducedBranch = "codespace/agent/web", BaseBranch = "main", Patch = "diff --git a/web.txt\n+SENTINEL-WEB-DIFF", Access = WorkspaceAccess.Write },
                new RepositoryRunResult { Alias = "api", RepositoryId = Guid.NewGuid(), ProducedBranch = "codespace/agent/api", BaseBranch = "develop", Patch = "diff --git a/api.cs\n+SENTINEL-API-DIFF", Access = WorkspaceAccess.Write },
            },
        };

        var run = new AgentRun { Status = AgentRunStatus.Succeeded, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options) };

        var payload = WorkflowResumeAgentRunCompletionNotifier.BuildResumePayload(run);

        payload.ShouldNotContain("SENTINEL-WEB-DIFF", Case.Insensitive, "the per-repo diff is stripped — it must never reach the node's outputs_jsonb");
        payload.ShouldNotContain("SENTINEL-API-DIFF", Case.Insensitive);

        var repos = JsonDocument.Parse(payload).RootElement.GetProperty("repositoryResults");
        repos.GetArrayLength().ShouldBe(2);

        var web = repos.EnumerateArray().Single(r => r.GetProperty("alias").GetString() == "web");
        web.GetProperty("producedBranch").GetString().ShouldBe("codespace/agent/web", "the per-repo branch IS surfaced — what git.open_change_set binds");
        web.GetProperty("baseBranch").GetString().ShouldBe("main");
        web.GetProperty("patch").GetString().ShouldBe("", "the per-repo patch is present-but-empty (stripped), never the raw diff");
    }
}
