using System.Text.Json;
using CodeSpace.Core.Services.Workflows.RunSources.Admission;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events.PullRequest;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Who may start a run with a pull request. An activation names <c>authors: any | members</c>; one that names neither —
/// every activation saved before this existed — takes the default for the repository the PR targets: members only when
/// the provider says it is public or internal (anyone who can read either can fork it and open a PR), anyone when it
/// says it is private, and, when the delivery does not say, members only for a PR from a fork. So an existing activation
/// keeps admitting everyone only on a private repository. Under members, new commits must also have been pushed by the
/// author or by a member: a member's PR whose head lives in an outsider's fork is the outsider's code from then on.
/// </summary>
[Trait("Category", "Unit")]
public class PullRequestTriggerAuthorsTests
{
    [Theory]
    [InlineData(RepositoryVisibility.Public, false, PullRequestTriggerAuthors.Members)]
    [InlineData(RepositoryVisibility.Public, true, PullRequestTriggerAuthors.Members)]
    [InlineData(RepositoryVisibility.Private, false, PullRequestTriggerAuthors.Any)]
    [InlineData(RepositoryVisibility.Private, true, PullRequestTriggerAuthors.Any)]
    [InlineData(RepositoryVisibility.Internal, false, PullRequestTriggerAuthors.Members)]
    [InlineData(RepositoryVisibility.Internal, true, PullRequestTriggerAuthors.Members)]
    [InlineData(null, true, PullRequestTriggerAuthors.Members)]
    [InlineData(null, false, PullRequestTriggerAuthors.Any)]
    public void An_activation_naming_no_authors_takes_the_repositorys_default(RepositoryVisibility? visibility, bool isFork, string expected)
    {
        var origin = new PullRequestOrigin { RepositoryVisibility = visibility, IsFork = isFork };

        PullRequestTriggerAuthors.Required(Config("{}"), origin).ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{"authors":"any"}""", PullRequestTriggerAuthors.Any)]
    [InlineData("""{"authors":"members"}""", PullRequestTriggerAuthors.Members)]
    [InlineData("""{"authors":"everyone"}""", PullRequestTriggerAuthors.Members)]
    [InlineData("""{"authors":7}""", PullRequestTriggerAuthors.Members)]
    [InlineData("""{"authors":null}""", PullRequestTriggerAuthors.Any)]
    public void An_activation_naming_authors_gets_what_it_named_and_a_value_it_cannot_mean_admits_only_members(string configJson, string expected)
    {
        // Named on a PRIVATE repository so the default would be "any": an explicit "members" and an unreadable value
        // both have to win over it. A null is the editor's cleared field, which is "no choice made".
        var origin = new PullRequestOrigin { RepositoryVisibility = RepositoryVisibility.Private };

        PullRequestTriggerAuthors.Required(Config(configJson), origin).ShouldBe(expected);
    }

    [Fact]
    public void An_explicit_any_admits_an_outsider_on_a_public_repository()
    {
        var origin = new PullRequestOrigin { RepositoryVisibility = RepositoryVisibility.Public, IsFork = true, AuthorAssociation = PullRequestAuthorAssociation.None };

        PullRequestTriggerAuthors.Admits(PullRequestTriggerAuthors.Required(Config("""{"authors":"any"}"""), origin), origin.AuthorAssociation).ShouldBeTrue();
    }

    [Theory]
    [InlineData(PullRequestAuthorAssociation.Member, true)]
    [InlineData(PullRequestAuthorAssociation.Contributor, false)]
    [InlineData(PullRequestAuthorAssociation.None, false)]
    [InlineData(PullRequestAuthorAssociation.Unknown, false)]
    public void Members_admits_only_a_member_and_never_an_unknown(PullRequestAuthorAssociation association, bool admitted)
    {
        PullRequestTriggerAuthors.Admits(PullRequestTriggerAuthors.Members, association).ShouldBe(admitted);
    }

    [Theory]
    [InlineData(PullRequestAuthorAssociation.Member)]
    [InlineData(PullRequestAuthorAssociation.None)]
    [InlineData(PullRequestAuthorAssociation.Unknown)]
    public void Any_admits_everyone(PullRequestAuthorAssociation association)
    {
        PullRequestTriggerAuthors.Admits(PullRequestTriggerAuthors.Any, association).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, true, PullRequestAuthorAssociation.Unknown, true)]       // an open or reopen: nobody pushed anything
    [InlineData("9", true, PullRequestAuthorAssociation.Unknown, true)]        // the author pushed; their standing was already decided
    [InlineData("66", false, PullRequestAuthorAssociation.Unknown, true)]      // the head lives in the target repository: pushing there takes a role
    [InlineData("66", true, PullRequestAuthorAssociation.Member, true)]        // someone else pushed to a fork head, and is a member
    [InlineData("66", true, PullRequestAuthorAssociation.Contributor, false)]
    [InlineData("66", true, PullRequestAuthorAssociation.None, false)]
    [InlineData("66", true, PullRequestAuthorAssociation.Unknown, false)]      // GitHub names the pusher but not their standing
    public void Members_admits_new_commits_only_when_the_author_or_a_member_pushed_them(string? pusher, bool isFork, PullRequestAuthorAssociation pusherAssociation, bool admitted)
    {
        var origin = new PullRequestOrigin { AuthorExternalId = "9", AuthorAssociation = PullRequestAuthorAssociation.Member, IsFork = isFork, PusherExternalId = pusher, PusherAssociation = pusherAssociation };

        PullRequestTriggerAuthors.AdmitsPusher(PullRequestTriggerAuthors.Members, origin).ShouldBe(admitted);
    }

    [Fact]
    public void Any_admits_whoever_pushed()
    {
        var origin = new PullRequestOrigin { AuthorExternalId = "9", IsFork = true, PusherExternalId = "66", PusherAssociation = PullRequestAuthorAssociation.None };

        PullRequestTriggerAuthors.AdmitsPusher(PullRequestTriggerAuthors.Any, origin).ShouldBeTrue();
    }

    [Fact]
    public void The_config_key_and_values_are_pinned()
    {
        // Stored in workflow_activation.config_json and in every saved definition's trigger node. A rename silently
        // turns every explicit choice into the default.
        PullRequestTriggerAuthors.ConfigKey.ShouldBe("authors");
        PullRequestTriggerAuthors.Any.ShouldBe("any");
        PullRequestTriggerAuthors.Members.ShouldBe("members");
    }

    private static JsonElement Config(string json) => JsonDocument.Parse(json).RootElement;
}
