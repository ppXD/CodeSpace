using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Publish.Guards;

/// <summary>
/// A repository with no bound credential clones anonymously — <c>LocalGitWorkspaceProvider</c>'s push path already
/// short-circuits to null for a tokenless clone (there is no remote-write credential to authenticate with), but
/// that decision was previously invisible (a silent null, no recorded reason). This guard makes it a NAMED,
/// pre-flight policy decision that lands on the manifest, checked from <see cref="Repository.CredentialId"/> alone
/// (no decryption needed — a bound credential's mere presence is enough to know the clone will carry a token).
/// </summary>
public sealed class NoCredentialPublishGuard : IPublishGuard, IScopedDependency
{
    public string Name => "no-credential";

    public int Order => 10;

    public PublishGuardVerdict? Evaluate(AgentTask task, Repository? repository) =>
        repository is { CredentialId: null } ? new PublishGuardVerdict(Name, "the repository has no bound push credential") : null;
}
