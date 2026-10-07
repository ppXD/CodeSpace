using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The calling run's hold on a repository a tool call names: only the repositories the run is bound to resolve, every
/// other id gets one "not found" whatever it is, and a repository bound as read-only context checks out only its bound
/// branch or its default branch. A writable repository is the run's own to work in, at any ref.
/// </summary>
[Trait("Category", "Unit")]
public class AgentRepositoryBindingTests
{
    private const string DefaultBranch = "main";

    private static readonly Guid Writable = Guid.NewGuid();
    private static readonly Guid Context = Guid.NewGuid();

    private static readonly AgentRunPosture Run = new()
    {
        Autonomy = AgentAutonomyLevel.Unleashed,
        Permissions = new AgentPermissions(),
        Repositories = [Spec(Writable, WorkspaceAccess.Write, null), Spec(Context, WorkspaceAccess.Read, "release/1")],
    };

    [Theory]
    [InlineData("writable", true)]
    [InlineData("read-context", true)]
    [InlineData("unbound", false)]
    public void Find_resolves_only_a_repository_the_run_is_bound_to(string target, bool bound)
    {
        var repositoryId = target switch { "writable" => Writable, "read-context" => Context, _ => Guid.NewGuid() };

        var found = AgentRepositoryBinding.Find(Run, repositoryId);

        (found is not null).ShouldBe(bound, $"a {target} repository must {(bound ? "" : "not ")}resolve against the run's binding");
        found?.RepositoryId.ShouldBe(repositoryId);
    }

    [Fact]
    public void A_run_bound_to_no_repository_resolves_none()
    {
        var run = new AgentRunPosture { Autonomy = AgentAutonomyLevel.Unleashed, Permissions = new AgentPermissions() };

        run.Repositories.ShouldBeEmpty("a posture stamped without a binding binds nothing — fail-closed, never every repository");
        AgentRepositoryBinding.Find(run, Writable).ShouldBeNull();
    }

    [Fact]
    public void The_refusal_is_the_not_found_a_missing_repository_gets()
    {
        // One answer whatever the id is (this team's, another team's, nobody's) — and byte-identical to RunCommandService's
        // own tenant-filter miss, pinned end to end in AgentToolRepositoryBindingFlowTests — so a refusal can never tell
        // the model that the repository it guessed exists in the team.
        var repositoryId = Guid.NewGuid();

        AgentRepositoryBinding.NotFound(repositoryId).ShouldBe($"Repository {repositoryId} not found.");
    }

    [Theory]
    // access              bound ref    requested ref     allowed
    [InlineData(WorkspaceAccess.Write, null, "secret-branch", true)]        // the run's own repository: any ref
    [InlineData(WorkspaceAccess.Write, "feature", "release/2026", true)]
    [InlineData(WorkspaceAccess.Read, null, null, true)]                     // no ref → the default branch
    [InlineData(WorkspaceAccess.Read, null, "  ", true)]                     // blank reads as no ref, as the clone does
    [InlineData(WorkspaceAccess.Read, null, "main", true)]                   // the default branch by name
    [InlineData(WorkspaceAccess.Read, null, "secret-branch", false)]         // an unmerged branch of read-only context
    [InlineData(WorkspaceAccess.Read, "release/1", "release/1", true)]       // the bound branch
    [InlineData(WorkspaceAccess.Read, "release/1", "main", true)]            // the default branch beside it
    [InlineData(WorkspaceAccess.Read, "release/1", "release/2", false)]
    [InlineData(WorkspaceAccess.Read, null, "MAIN", false)]                  // refs are case-sensitive
    [InlineData(WorkspaceAccess.Read, null, "refs/heads/secret", false)]
    [InlineData((WorkspaceAccess)99, null, "secret-branch", false)]          // an access this code does not know is pinned like read
    public void A_ref_is_open_on_a_writable_repository_and_pinned_on_read_only_context(WorkspaceAccess access, string? boundRef, string? requestedRef, bool allowed)
    {
        var bound = Spec(Guid.NewGuid(), access, boundRef);

        AgentRepositoryBinding.AllowsRef(bound, requestedRef, DefaultBranch).ShouldBe(allowed, $"{access} bound at '{boundRef ?? "(default)"}' asked for '{requestedRef ?? "(none)"}'");
    }

    [Theory]
    [InlineData(WorkspaceAccess.Write, true)]
    [InlineData(WorkspaceAccess.Read, false)]            // read-only context is the run's to read, not to open, merge, review or comment on
    [InlineData((WorkspaceAccess)99, false)]             // an access this code does not know is held like read-only context
    public void Only_a_repository_bound_writable_takes_an_agents_write(WorkspaceAccess access, bool allowed)
    {
        AgentRepositoryBinding.AllowsWrite(Spec(Guid.NewGuid(), access, null)).ShouldBe(allowed);
    }

    private static WorkspaceRepositorySpec Spec(Guid repositoryId, WorkspaceAccess access, string? @ref) =>
        new() { Alias = repositoryId.ToString("N"), RepositoryId = repositoryId, Access = access, Ref = @ref };
}
