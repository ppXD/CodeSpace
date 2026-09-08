using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The PR delivery detector. The hard case: a git.open_pr node and a git.create_issue node emit the SAME
/// <c>{ number, url, state }</c> output shape, so shape alone is ambiguous — a single PR is recognized only by its
/// PR-only branch inputs, while an issue (no branches) is rejected. Plus the multi-repo <c>pullRequests[]</c> shape,
/// graceful handling of malformed JSON, and a &gt; int32 PR number.
/// </summary>
[Trait("Category", "Unit")]
public class RoomDeliveryParserTests
{
    [Fact]
    public void A_single_pr_node_with_branch_inputs_is_a_delivery()
    {
        var d = RoomDeliveryParser.Parse(
            """{"number":128,"url":"https://x/pr/128","state":"open"}""",
            """{"title":"Rename run agent","sourceBranch":"feat/run-agent","targetBranch":"main"}""");

        d.ShouldNotBeNull();
        d!.Reference.ShouldBe("#128");
        d.Title.ShouldBe("Rename run agent");
        d.BranchHead.ShouldBe("feat/run-agent");
        d.BranchBase.ShouldBe("main");
        d.Url.ShouldBe("https://x/pr/128");
    }

    [Fact]
    public void An_issue_node_with_the_same_shape_but_no_branches_is_not_a_delivery()
    {
        RoomDeliveryParser.Parse(
            """{"number":42,"url":"https://x/issues/42","state":"open"}""",
            """{"title":"A bug report"}""")
            .ShouldBeNull("an issue shares the {number,url} shape but has no branch inputs — it is not a PR");
    }

    [Fact]
    public void A_multi_repo_change_set_is_a_delivery_from_the_pull_requests_array()
    {
        var d = RoomDeliveryParser.Parse(
            """{"pullRequests":[{"number":7,"url":"https://x/pr/7","state":"open"}]}""",
            "{}");

        d.ShouldNotBeNull("the pullRequests[] key is PR-specific — no branch-input check needed");
        d!.Reference.ShouldBe("#7");
    }

    [Fact]
    public void A_multi_repo_result_retains_every_disposition_and_repository_branch()
    {
        var apiId = Guid.NewGuid();
        var webId = Guid.NewGuid();
        var deliveries = RoomDeliveryParser.ParseMany(
            $$"""{"pullRequests":[{"repositoryId":"{{apiId}}","alias":"api","disposition":"Opened","number":7,"url":"https://x/api/pull/7"},{"repositoryId":"{{webId}}","alias":"web","disposition":"Failed","error":"credential cannot create pull requests"}]}""",
            $$"""{"title":"Ship the repository set","repositories":[{"repositoryId":"{{apiId}}","alias":"api","producedBranch":"codespace/api","baseBranch":"main"},{"repositoryId":"{{webId}}","alias":"web","producedBranch":"codespace/web","baseBranch":"release"}]}""");

        deliveries.Count.ShouldBe(2);
        deliveries[0].RepositoryId.ShouldBe(apiId);
        deliveries[0].RepositoryAlias.ShouldBe("api");
        deliveries[0].Disposition.ShouldBe(RoomPullRequestDisposition.Opened);
        deliveries[0].Reference.ShouldBe("#7");
        deliveries[0].BranchHead.ShouldBe("codespace/api");
        deliveries[0].BranchBase.ShouldBe("main");
        deliveries[1].RepositoryId.ShouldBe(webId);
        deliveries[1].RepositoryAlias.ShouldBe("web");
        deliveries[1].Disposition.ShouldBe(RoomPullRequestDisposition.Failed);
        deliveries[1].Error.ShouldBe("credential cannot create pull requests");
        deliveries[1].BranchHead.ShouldBe("codespace/web");
        deliveries[1].BranchBase.ShouldBe("release");
    }

    [Fact]
    public void A_large_pr_number_does_not_throw()
    {
        var d = RoomDeliveryParser.Parse(
            """{"number":9999999999,"url":"https://x/pr/big"}""",
            """{"sourceBranch":"f","targetBranch":"main"}""");

        d.ShouldNotBeNull();
        d!.Reference.ShouldBe("#9999999999");
    }

    [Theory]
    [InlineData("not json", null)]
    [InlineData("[1,2,3]", null)]
    [InlineData(null, null)]
    [InlineData("""{"foo":"bar"}""", """{"sourceBranch":"x"}""")]
    [InlineData("""{"number":1,"url":"https://x/pr/1"}""", null)]   // single shape but no branch inputs → ambiguous, rejected
    public void Malformed_or_ambiguous_input_yields_null_without_throwing(string? outputs, string? inputs)
    {
        RoomDeliveryParser.Parse(outputs, inputs).ShouldBeNull();
    }
}
