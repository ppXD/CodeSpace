using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Publish.Guards;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The repository's own say over an agent's use of it. A pull-request write (open, merge, review, comment) meets the SAME
/// guard chain an agent's branch push meets, so a repository whose policy refuses agent-pushed branches
/// (<see cref="RepositoryPublishMode.PatchOnly"/>) does not take an agent-opened, -merged, -reviewed or -commented pull
/// request either; and a ref a command names on read-only context is judged against the repository's default branch.
/// Pinned over the production guards.
/// </summary>
[Trait("Category", "Unit")]
public class AgentRepositoryPolicyTests
{
    private static readonly IPublishGuard[] ProductionGuards = [new RepositoryPolicyPublishGuard(), new ProfileOptOutPublishGuard(), new NoCredentialPublishGuard()];

    [Theory]
    [InlineData(RepositoryPublishMode.PatchOnly, true)]
    [InlineData(RepositoryPublishMode.Branch, false)]
    public void A_patch_only_repository_refuses_an_agents_pull_request_write(RepositoryPublishMode mode, bool refused)
    {
        var repository = Repo(mode, credentialId: Guid.NewGuid());

        var refusal = AgentRepositoryPolicy.Refusal(repository, Write(repository), ProductionGuards);

        (refusal is not null).ShouldBe(refused, refusal);
        if (refused) refusal.ShouldBe($"Repository {repository.Id} does not take pull-request writes from an agent: the repository requires patch-only publishing.");
    }

    [Fact]
    public void A_read_of_a_patch_only_repository_does_not_meet_its_publish_guards()
    {
        var repository = Repo(RepositoryPublishMode.PatchOnly, credentialId: null);

        AgentRepositoryPolicy.Refusal(repository, new AgentRepositoryUse { TeamId = repository.TeamId, Bound = Bound(repository, WorkspaceAccess.Read, null), Ref = "main" }, ProductionGuards).ShouldBeNull();
    }

    [Fact]
    public void The_chain_is_walked_in_its_declared_order_not_the_order_it_was_handed()
    {
        // A credential-less patch-only repository trips two guards; the lower Order (no-credential, 10) speaks first —
        // the same first-wins walk the push path does, never DI registration order.
        var repository = Repo(RepositoryPublishMode.PatchOnly, credentialId: null);

        var refusal = AgentRepositoryPolicy.Refusal(repository, Write(repository), ProductionGuards);

        refusal.ShouldNotBeNull().ShouldEndWith("the repository has no bound push credential.");
    }

    [Theory]
    // access                bound ref      requested       refused
    [InlineData(WorkspaceAccess.Read, null, "secret-branch", true)]
    [InlineData(WorkspaceAccess.Read, null, "main", false)]                // the default branch, which only the repository row knows
    [InlineData(WorkspaceAccess.Read, "release/1", "release/1", false)]
    [InlineData(WorkspaceAccess.Read, "release/1", "main", false)]
    [InlineData(WorkspaceAccess.Write, null, "secret-branch", false)]
    public void A_ref_named_on_read_only_context_is_judged_against_the_repositorys_default_branch(WorkspaceAccess access, string? boundRef, string requestedRef, bool refused)
    {
        var repository = Repo(RepositoryPublishMode.Branch, credentialId: Guid.NewGuid());
        var bound = Bound(repository, access, boundRef);

        var refusal = AgentRepositoryPolicy.Refusal(repository, new AgentRepositoryUse { TeamId = repository.TeamId, Bound = bound, Ref = requestedRef }, ProductionGuards);

        if (refused) refusal.ShouldBe(AgentRepositoryBinding.RefOutsideBinding(repository.Id, bound, requestedRef, "main"));
        else refusal.ShouldBeNull();
    }

    [Fact]
    public void A_repository_that_did_not_resolve_is_left_to_the_tool_which_reports_it_not_found()
    {
        var repository = Repo(RepositoryPublishMode.PatchOnly, credentialId: null);

        AgentRepositoryPolicy.Refusal(null, Write(repository), ProductionGuards).ShouldBeNull();
    }

    private static AgentRepositoryUse Write(Repository repository) => new() { TeamId = repository.TeamId, Bound = Bound(repository, WorkspaceAccess.Write, null), Writes = true };

    private static WorkspaceRepositorySpec Bound(Repository repository, WorkspaceAccess access, string? @ref) => new() { Alias = "repo", RepositoryId = repository.Id, Access = access, Ref = @ref };

    private static Repository Repo(RepositoryPublishMode mode, Guid? credentialId) => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), ProviderInstanceId = Guid.NewGuid(), CredentialId = credentialId, PublishMode = mode, DefaultBranch = "main",
        ExternalId = "x", NamespacePath = "acme", Name = "compliance", FullPath = "acme/compliance", WebUrl = "https://git.example.invalid/acme/compliance",
    };
}
