using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="AgentRunExecutor.BuildManifestUpsert"/> — the pure mapping from a run's produced-artifact facts
/// to the publish-manifest row. This IS the I1/I3 invariant's decision logic: whether a repo's work resolves to
/// <see cref="PublishState.Pushed"/> or <see cref="PublishState.PatchOnly"/>, and whether that PatchOnly was an
/// intentional skip or a failed attempt (<see cref="PublishManifestUpsert.PublishError"/>).
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunExecutorManifestTests
{
    private static AgentRun Run() => new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), WorkflowRunId = Guid.NewGuid() };

    [Fact]
    public void A_produced_branch_resolves_to_Pushed_with_the_branch_name_recorded()
    {
        var upsert = AgentRunExecutor.BuildManifestUpsert(Run(), "primary", Guid.NewGuid(), "abc123", null, new[] { "a.cs" }, "codespace/agent/deadbeef", publishError: null, acceptancePassed: null);

        upsert.PublishStateValue.ShouldBe(PublishState.Pushed);
        upsert.Branch.ShouldBe("codespace/agent/deadbeef");
        upsert.PublishError.ShouldBeNull();
    }

    [Fact]
    public void No_branch_and_no_error_resolves_to_PatchOnly_by_choice()
    {
        var upsert = AgentRunExecutor.BuildManifestUpsert(Run(), "primary", null, "abc123", Guid.NewGuid(), new[] { "a.cs" }, producedBranch: null, publishError: null, acceptancePassed: null);

        upsert.PublishStateValue.ShouldBe(PublishState.PatchOnly);
        upsert.Branch.ShouldBeNull();
        upsert.PublishError.ShouldBeNull("no branch and no error means a policy skip, not a failed attempt");
    }

    [Fact]
    public void No_branch_with_an_error_resolves_to_PatchOnly_with_the_failure_recorded()
    {
        var upsert = AgentRunExecutor.BuildManifestUpsert(Run(), "primary", null, "abc123", Guid.NewGuid(), new[] { "a.cs" }, producedBranch: null, publishError: "push failed: connection refused", acceptancePassed: null);

        upsert.PublishStateValue.ShouldBe(PublishState.PatchOnly);
        upsert.PublishError.ShouldBe("push failed: connection refused", "a non-null error is what distinguishes an ATTEMPTED-and-failed push from a policy skip");
    }

    [Theory]
    [InlineData(true, PublishAcceptanceState.Passed)]
    [InlineData(false, PublishAcceptanceState.Failed)]
    [InlineData(null, PublishAcceptanceState.NotApplicable)]
    public void Acceptance_tristate_mirrors_the_graders_verdict_verbatim(bool? passed, PublishAcceptanceState expected) =>
        AgentRunExecutor.BuildManifestUpsert(Run(), "primary", null, null, null, Array.Empty<string>(), null, null, passed)
            .AcceptanceState.ShouldBe(expected);

    [Fact]
    public void Empty_changed_files_leaves_the_json_column_null_not_an_empty_array()
    {
        var upsert = AgentRunExecutor.BuildManifestUpsert(Run(), "primary", null, null, null, Array.Empty<string>(), null, null, null);

        upsert.ChangedFileCount.ShouldBe(0);
        upsert.ChangedFilesJson.ShouldBeNull();
    }

    [Fact]
    public void Changed_files_round_trip_as_a_json_string_array()
    {
        var files = new[] { "src/A.cs", "src/B.cs" };

        var upsert = AgentRunExecutor.BuildManifestUpsert(Run(), "primary", null, null, null, files, null, null, null);

        upsert.ChangedFileCount.ShouldBe(2);
        JsonSerializer.Deserialize<string[]>(upsert.ChangedFilesJson!).ShouldBe(files);
    }

    [Fact]
    public void Carries_the_runs_team_and_workflow_run_id_and_the_repos_own_facts()
    {
        var run = Run();
        var repositoryId = Guid.NewGuid();
        var patchArtifactId = Guid.NewGuid();

        var upsert = AgentRunExecutor.BuildManifestUpsert(run, "web", repositoryId, "base-sha", patchArtifactId, new[] { "x" }, "branch-x", null, true);

        upsert.TeamId.ShouldBe(run.TeamId);
        upsert.WorkflowRunId.ShouldBe(run.WorkflowRunId);
        upsert.RepositoryAlias.ShouldBe("web");
        upsert.RepositoryId.ShouldBe(repositoryId);
        upsert.BaseSha.ShouldBe("base-sha");
        upsert.PatchArtifactId.ShouldBe(patchArtifactId);
    }
}
