using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// How a provider user — named by their provider id — stands with a repository. For a webhook that names a pull
/// request's author but not their standing: GitLab's merge request hook carries the author id and no access level, so a
/// trigger that admits only members has to ask.
///
/// <para>Rule 7 (ISP): a sibling of <see cref="IRepositoryAccessCapability"/>, which reports the CREDENTIAL's own role. A
/// provider whose payload already says (GitHub's <c>author_association</c>) does not implement this.</para>
/// </summary>
public interface IRepositoryMemberStandingCapability : IProviderCapability
{
    /// <summary>Member or None from the provider's answer, Unknown when it answered with an error. A provider that cannot be reached throws; either way a caller treats the author as not a member.</summary>
    Task<PullRequestAuthorAssociation> GetMemberStandingAsync(ProviderContext context, RemoteRepository repository, string userExternalId, CancellationToken cancellationToken);
}
