using System.Text.Json;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit DRIFT-GUARD: git.list_prs / git.fetch_pr_checks / git.fetch_pr_diff now serialize their array outputs
/// with <see cref="WorkflowJson.Options"/> (camelCase + string enums, like every other node output) and declare a
/// TYPED item shape so the {{ref}} picker drills <c>pullRequests[0].number</c> / <c>checks[0].status</c> /
/// <c>files[0].fileName</c>. That is only safe if every schema-declared key is a REAL serialized key — a
/// declared-but-never-emitted key would be a silent null dead-ref. This pins schema↔runtime parity: serialize a
/// fully-populated provider DTO with the SAME options the node uses and assert each declared field appears.
/// Rename a DTO property (or flip its casing back) and this fails until the schema follows.
/// </summary>
[Trait("Category", "Unit")]
public class GitPrOutputSchemaTests
{
    private static JsonElement ItemProps(JsonElement outputSchema, string arrayKey) =>
        outputSchema.GetProperty("properties").GetProperty(arrayKey).GetProperty("items").GetProperty("properties");

    private static void AssertDeclaredKeysAreEmitted(JsonElement itemProps, JsonElement emittedItem, string label)
    {
        foreach (var prop in itemProps.EnumerateObject())
            emittedItem.TryGetProperty(prop.Name, out _).ShouldBeTrue($"{label} declares .{prop.Name} but the DTO never serializes that key → dead ref");
    }

    [Fact]
    public void ListPullRequests_item_schema_matches_the_serialized_RemotePullRequest()
    {
        var pr = new RemotePullRequest
        {
            ExternalId = "pr-1", Number = 1, Title = "t", State = PullRequestState.Open,
            SourceBranch = "feat", TargetBranch = "main", AuthorLogin = "me", WebUrl = "https://x/pr/1",
            CommentsCount = 2, CreatedDate = DateTimeOffset.UnixEpoch, UpdatedDate = DateTimeOffset.UnixEpoch,
            Body = "desc", Labels = new[] { new LabelRef { Name = "bug", Color = "red" } },
        };
        var emitted = JsonSerializer.SerializeToElement(pr, WorkflowJson.Options);

        var itemProps = ItemProps(new GitListPullRequestsNode(null!).Manifest.OutputSchema, "pullRequests");
        AssertDeclaredKeysAreEmitted(itemProps, emitted, "pullRequests[]");

        // string enum, not int / PascalCase.
        emitted.GetProperty("state").GetString().ShouldBe("Open");

        // The NESTED labels[] item shape is drill-guarded too — every declared labels[].<key> (name, color)
        // must be a real serialized key, so a LabelRef rename can't leave a schema-declared dead-ref.
        var labelProps = itemProps.GetProperty("labels").GetProperty("items").GetProperty("properties");
        AssertDeclaredKeysAreEmitted(labelProps, emitted.GetProperty("labels")[0], "pullRequests[].labels[]");
        emitted.GetProperty("labels")[0].GetProperty("name").GetString().ShouldBe("bug");
    }

    [Fact]
    public void FetchChecks_item_schema_matches_the_serialized_RemotePullRequestCheck()
    {
        var check = new RemotePullRequestCheck
        {
            Name = "build / test", Status = PullRequestCheckStatus.Success, Conclusion = "success",
            StartedAt = DateTimeOffset.UnixEpoch, CompletedAt = DateTimeOffset.UnixEpoch, DurationSeconds = 42,
            DetailsUrl = "https://x/checks",
        };
        var emitted = JsonSerializer.SerializeToElement(check, WorkflowJson.Options);

        AssertDeclaredKeysAreEmitted(ItemProps(new GitFetchPrChecksNode(null!).Manifest.OutputSchema, "checks"), emitted, "checks[]");
        emitted.GetProperty("status").GetString().ShouldBe("Success");   // string enum, not int 1
    }

    [Fact]
    public void FetchDiff_item_schema_matches_the_serialized_RemotePullRequestFile()
    {
        var file = new RemotePullRequestFile
        {
            FileName = "a.cs", PreviousFileName = "old.cs", Status = FileChangeStatus.Renamed,
            Additions = 3, Deletions = 1, Patch = "@@ -1 +1 @@",
        };
        var emitted = JsonSerializer.SerializeToElement(file, WorkflowJson.Options);

        AssertDeclaredKeysAreEmitted(ItemProps(new GitFetchPrDiffNode(null!).Manifest.OutputSchema, "files"), emitted, "files[]");
        emitted.GetProperty("status").GetString().ShouldBe("Renamed");   // string enum, not int 3
    }
}
