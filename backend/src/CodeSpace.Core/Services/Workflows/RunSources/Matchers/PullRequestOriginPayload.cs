using CodeSpace.Messages.Events.PullRequest;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// How a PR's origin reads in a trigger payload, shared by the two triggers an outsider can cause so
/// <c>{{trigger.authorAssociation}}</c> means the same thing on both: <c>member</c>, <c>contributor</c>, <c>none</c> or
/// <c>unknown</c>.
/// </summary>
internal static class PullRequestOriginPayload
{
    public static string Association(PullRequestOrigin origin) => origin.AuthorAssociation.ToString().ToLowerInvariant();
}
