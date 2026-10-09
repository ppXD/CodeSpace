using System.Text.Json;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events.PullRequest;

namespace CodeSpace.Core.Services.Workflows.RunSources.Admission;

/// <summary>
/// Who may start a run with a pull request: the activation's <c>authors</c> value, <see cref="Any"/> or
/// <see cref="Members"/>, read from the same config the matchers read (schema: <c>PrTriggerSchemas.OutsiderReachableConfigSchemaJson</c>).
///
/// <para>An activation that names neither — every one saved before this filter existed — takes its repository's default,
/// decided by what the provider said on THIS delivery: members only on a public or internal repository, anyone on a
/// private one. When the delivery does not say, members only for a PR from a fork. Internal counts with public because
/// anyone who can read the repository can fork it and open a PR: every signed-in user of a GitLab instance, every member
/// of a GitHub enterprise. So an existing activation keeps admitting everyone only on a private repository.</para>
///
/// <para>Members means the people who made the PR's code what it is: its author, and for new commits whoever pushed them.
/// A member can open a PR from an outsider's fork branch, and from then on the outsider pushes the code it runs.</para>
/// </summary>
internal static class PullRequestTriggerAuthors
{
    public const string ConfigKey = "authors";

    public const string Any = "any";

    public const string Members = "members";

    /// <summary>What this activation requires of this PR's author. A value the filter cannot mean admits only members: a typo must never widen it.</summary>
    public static string Required(JsonElement activationConfig, PullRequestOrigin origin)
    {
        if (activationConfig.ValueKind != JsonValueKind.Object) return Default(origin);
        if (!activationConfig.TryGetProperty(ConfigKey, out var value) || value.ValueKind == JsonValueKind.Null) return Default(origin);

        return value.ValueKind == JsonValueKind.String && value.GetString() == Any ? Any : Members;
    }

    /// <summary>Members admits a <see cref="PullRequestAuthorAssociation.Member"/> and nothing else — an author whose standing is unknown is not one.</summary>
    public static bool Admits(string required, PullRequestAuthorAssociation association) =>
        required == Any || association == PullRequestAuthorAssociation.Member;

    /// <summary>
    /// Members admits new commits when their pusher is the author (already admitted), or pushed to a head in the target
    /// repository (which takes a role there), or is a member. Anyone else pushing to a fork head is not vouched for —
    /// including a pusher whose standing the provider cannot say.
    /// </summary>
    public static bool AdmitsPusher(string required, PullRequestOrigin origin) =>
        required == Any || PushNeedsNoStanding(origin) || origin.PusherAssociation == PullRequestAuthorAssociation.Member;

    /// <summary>No push in this event (an open or reopen), the author's own push, or a push to a head that lives in the target repository.</summary>
    private static bool PushNeedsNoStanding(PullRequestOrigin origin) =>
        origin.PusherExternalId == null || origin.PusherExternalId == origin.AuthorExternalId || !origin.IsFork;

    private static string Default(PullRequestOrigin origin) => origin.RepositoryVisibility switch
    {
        RepositoryVisibility.Public or RepositoryVisibility.Internal => Members,
        RepositoryVisibility.Private => Any,
        _ => origin.IsFork ? Members : Any
    };
}
