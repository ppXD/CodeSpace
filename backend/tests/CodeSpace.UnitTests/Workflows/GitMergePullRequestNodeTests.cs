using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <c>git.merge_pr</c> — drives the real node against a stub <see cref="IPullRequestService"/> that records the
/// <see cref="MergePullRequestInput"/> it was called with and returns a canned result (or throws), so input
/// parsing (required repositoryId/number, method default + parse, commit title/message, deleteSourceBranch,
/// actAsUserId), the output shape (merged/sha/message/sourceBranchDeletion/sourceBranchDetail), and the typed-provider-failure → actionable-message
/// mapping (scope, 403, 404, 405, 409, 422) are all pinned.
/// </summary>
[Trait("Category", "Unit")]
public class GitMergePullRequestNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";
    private const string Team = "22222222-2222-2222-2222-222222222222";

    private sealed class StubPrService : IPullRequestService
    {
        public Guid RepoId;
        public Guid TeamId;
        public int Number;
        public MergePullRequestInput? Input;
        public Guid? ActorUserId;
        public int Calls;
        public Exception? ThrowOnMerge;
        public RemotePullRequestMergeResult Result = new() { Merged = true, Sha = "abc123", Message = "Merged" };

        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid repositoryId, Guid teamId, int number, MergePullRequestInput input, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; TeamId = teamId; Number = number; Input = input; ActorUserId = actorUserId; Calls++;
            if (ThrowOnMerge != null) throw ThrowOnMerge;
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid r, Guid t, PullRequestState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, Guid t, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid r, Guid t, int n, SubmitPullRequestReviewInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid r, Guid t, OpenPullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Merges_with_method_default_and_outputs_merged_sha_message()
    {
        var stub = new StubPrService { Result = new() { Merged = true, Sha = "deadbeef", Message = "Pull Request successfully merged" } };

        var result = await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.RepoId.ShouldBe(Guid.Parse(Repo));
        stub.Number.ShouldBe(42);
        stub.Input!.Method.ShouldBe(PullRequestMergeMethod.Merge, "no method wired → merge-commit default");
        stub.Input.CommitTitle.ShouldBeNull();
        stub.Input.CommitMessage.ShouldBeNull();
        stub.Input.DeleteSourceBranch.ShouldBeFalse();
        stub.ActorUserId.ShouldBeNull();

        result.Outputs["merged"].GetBoolean().ShouldBeTrue();
        result.Outputs["sha"].GetString().ShouldBe("deadbeef");
        result.Outputs["message"].GetString().ShouldBe("Pull Request successfully merged");
    }

    [Theory]
    [InlineData("squash", PullRequestMergeMethod.Squash)]
    [InlineData("rebase", PullRequestMergeMethod.Rebase)]
    [InlineData("merge", PullRequestMergeMethod.Merge)]
    [InlineData("SQUASH", PullRequestMergeMethod.Squash)]
    public async Task Parses_the_merge_method_case_insensitively(string raw, PullRequestMergeMethod expected)
    {
        var stub = new StubPrService();

        var result = await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["method"] = JsonSerializer.SerializeToElement(raw),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Input!.Method.ShouldBe(expected);
    }

    [Fact]
    public async Task Passes_commit_title_message_and_delete_source_branch_through()
    {
        var stub = new StubPrService();

        await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["method"] = JsonSerializer.SerializeToElement("squash"),
            ["commitTitle"] = JsonSerializer.SerializeToElement("Final title"),
            ["commitMessage"] = JsonSerializer.SerializeToElement("Body of the squash commit"),
            ["deleteSourceBranch"] = JsonSerializer.SerializeToElement(true),
        }), CancellationToken.None);

        stub.Input!.CommitTitle.ShouldBe("Final title");
        stub.Input.CommitMessage.ShouldBe("Body of the squash commit");
        stub.Input.DeleteSourceBranch.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0a1b2c3d", "0a1b2c3d")]
    [InlineData("  0a1b2c3d  ", "0a1b2c3d")]
    [InlineData("", null)]   // empty: merge whatever the head is, as before
    public async Task Passes_the_expected_head_through_so_the_provider_merges_only_that_commit(string given, string? expected)
    {
        var stub = new StubPrService();

        await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["expectedHeadSha"] = JsonSerializer.SerializeToElement(given),
        }), CancellationToken.None);

        stub.Input!.ExpectedHeadSha.ShouldBe(expected);
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("  release/2.0  ", "release/2.0")]
    [InlineData("", null)]   // empty: merge into whatever the base is, as before
    public async Task Passes_the_expected_base_through_so_the_merge_lands_only_on_that_branch(string given, string? expected)
    {
        var stub = new StubPrService();

        await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["expectedBaseBranch"] = JsonSerializer.SerializeToElement(given),
        }), CancellationToken.None);

        stub.Input!.ExpectedBaseBranch.ShouldBe(expected);
    }

    [Fact]
    public async Task A_merge_with_no_expected_head_or_base_pins_nothing()
    {
        var stub = new StubPrService();

        await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        stub.Input!.ExpectedHeadSha.ShouldBeNull();
        stub.Input.ExpectedBaseBranch.ShouldBeNull();
    }

    [Theory]
    [InlineData("base", "docs-sandbox", "main", "Couldn't merge PR #42: its base is now main, not docs-sandbox, the one it was pinned to, so nothing was merged. Read what changed before asking again.")]
    [InlineData("head", "0a1b2c3d", "ffff0000", "Couldn't merge PR #42: its head is now ffff0000, not 0a1b2c3d, the one it was pinned to, so nothing was merged. Read what changed before asking again.")]
    public async Task A_pull_request_that_moved_from_its_pins_before_the_merge_was_sent_says_so_and_that_nothing_merged(string pinned, string expected, string actual, string error)
    {
        var stub = new StubPrService { ThrowOnMerge = new PullRequestMovedException(42, pinned, expected, actual) };

        var result = await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldBe(error);
    }

    [Theory]
    [InlineData(ProviderKind.GitHub)]
    [InlineData(ProviderKind.GitLab)]
    public async Task A_pinned_merge_refused_because_the_head_moved_says_so_and_that_nothing_merged(ProviderKind provider)
    {
        var stub = new StubPrService { ThrowOnMerge = new ProviderApiException(provider, 409, "MergePullRequestAsync", "Head branch was modified. Review and try the merge again.", new Exception()) };

        var result = await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["expectedHeadSha"] = JsonSerializer.SerializeToElement("0a1b2c3d"),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldBe($"Couldn't merge PR #42: {provider} reports its head is no longer 0a1b2c3d, the commit this merge was pinned to, so nothing was merged. Read the new commits before asking again.");
    }

    [Fact]
    public void The_expected_head_and_base_are_declared_inputs_the_manifest_pins_and_the_target_is_the_pull_request_at_them()
    {
        var manifest = new GitMergePullRequestNode(new StubPrService()).Manifest;

        manifest.InputSchema.GetProperty("properties").TryGetProperty("expectedHeadSha", out _).ShouldBeTrue("a workflow can bind the reviewed head too");
        manifest.InputSchema.GetProperty("properties").TryGetProperty("expectedBaseBranch", out _).ShouldBeTrue("and the base it was reviewed against");
        manifest.RepositoryInput.ShouldNotBeNull();
        (manifest.RepositoryInput.PullRequestInputKey, manifest.RepositoryInput.HeadShaInputKey, manifest.RepositoryInput.BaseBranchInputKey).ShouldBe(("number", "expectedHeadSha", "expectedBaseBranch"));
        manifest.ApprovalTargetInputs.ShouldBe(["repositoryId", "number", "expectedHeadSha", "expectedBaseBranch"], "new commits, or a new base, are a new request; a new method or commit text is not");
    }

    [Fact]
    public async Task Passes_actAsUserId_through_when_wired()
    {
        var stub = new StubPrService();
        var actor = Guid.NewGuid();

        await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["actAsUserId"] = JsonSerializer.SerializeToElement(actor.ToString()),
        }), CancellationToken.None);

        stub.ActorUserId.ShouldBe(actor, "a wired actAsUserId must reach the service so the merge is attributed to that user");
    }

    [Fact]
    public async Task Threads_the_run_team_from_sys_scope_into_the_service_call()
    {
        var stub = new StubPrService();

        await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        stub.TeamId.ShouldBe(Guid.Parse(Team), "the run's team flows from {{sys.team_id}} so the service fail-closes the repo load to it");
    }

    [Fact]
    public async Task Fails_closed_when_sys_scope_has_no_team()
    {
        var stub = new StubPrService();

        var result = await new GitMergePullRequestNode(stub).RunAsync(ContextWithSys(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
        }, new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("team context");
        stub.Calls.ShouldBe(0, "without a team the node must short-circuit before touching the service");
    }

    [Fact]
    public async Task Outputs_merged_false_when_the_provider_reports_not_merged()
    {
        var stub = new StubPrService { Result = new() { Merged = false, Sha = null, Message = "not mergeable" } };

        var result = await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "a clean 'not merged' answer from the provider is a successful node run with merged=false");
        result.Outputs["merged"].GetBoolean().ShouldBeFalse();
        result.Outputs["sha"].ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(SourceBranchDeletion.NotRequested, null)]
    [InlineData(SourceBranchDeletion.Deleted, "Deleted 'feature/retry' from acme/api.")]
    [InlineData(SourceBranchDeletion.Requested, "Asked GitLab to delete 'feature/retry' from the merge request's own source project once the merge completes; GitLab does so when the merging identity may push there.")]
    [InlineData(SourceBranchDeletion.SkippedFork, "Kept 'release': the pull request's head is in outsider/api, not acme/api, and a source branch is deleted only from its own repository.")]
    [InlineData(SourceBranchDeletion.Failed, "The merge stands, but its source branch was not deleted: GitHub returned HTTP 422 for MergePullRequestAsync/delete-source-branch: Cannot delete this protected branch")]
    public async Task Outputs_what_became_of_the_source_branch(SourceBranchDeletion deletion, string? detail)
    {
        var stub = new StubPrService { Result = new() { Merged = true, Sha = "deadbeef", SourceBranchDeletion = deletion, SourceBranchDetail = detail } };

        var result = await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "the merge stands whatever became of its source branch");
        result.Outputs["merged"].GetBoolean().ShouldBeTrue();
        result.Outputs["sourceBranchDeletion"].GetString().ShouldBe(deletion.ToString());
        result.Outputs["sourceBranchDetail"].Deserialize<string?>().ShouldBe(detail);
    }

    [Fact]
    public void Output_schema_declares_every_source_branch_outcome()
    {
        var declared = new GitMergePullRequestNode(new StubPrService()).Manifest.OutputSchema.GetProperty("properties").GetProperty("sourceBranchDeletion").GetProperty("enum").EnumerateArray().Select(e => e.GetString());

        declared.ShouldBe(Enum.GetNames<SourceBranchDeletion>(), "a workflow or a model branching on the output reads the vocabulary from the schema");
    }

    [Fact]
    public async Task Fails_when_repository_id_is_missing()
    {
        var stub = new StubPrService();
        var result = await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["number"] = JsonSerializer.SerializeToElement(42),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
        stub.Calls.ShouldBe(0, "a missing required field must short-circuit before the provider call");
    }

    [Fact]
    public async Task Fails_when_number_is_missing()
    {
        var stub = new StubPrService();
        var result = await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("number");
        stub.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Fails_when_method_is_unrecognised()
    {
        var stub = new StubPrService();
        var result = await new GitMergePullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["method"] = JsonSerializer.SerializeToElement("fast-forward"),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("merge, squash, rebase");
        stub.Calls.ShouldBe(0, "an invalid method must short-circuit before the provider call");
    }

    [Fact]
    public async Task Insufficient_scope_fails_with_an_actionable_scope_message()
    {
        var stub = new StubPrService { ThrowOnMerge = new ProviderInsufficientScopeException(ProviderKind.GitLab, "IPullRequestWriteCapability", new[] { "api" }, Array.Empty<string>(), "hint") };

        var result = await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("api");
        result.Error.ShouldContain("scope");
        result.Error!.ShouldNotContain("HTTP", customMessage: "a scope gap must read as a scope message, not a raw SDK string");
    }

    [Theory]
    [InlineData(403, "permission")]
    [InlineData(404, "find")]
    [InlineData(405, "mergeable")]
    [InlineData(409, "conflict")]
    [InlineData(422, "mergeable")]
    public async Task Provider_http_failure_maps_to_an_actionable_message(int status, string expectedFragment)
    {
        var stub = new StubPrService { ThrowOnMerge = new ProviderApiException(ProviderKind.GitHub, status, "MergePullRequestAsync", "boom", new Exception()) };

        var result = await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("#42");
        result.Error.ShouldContain(expectedFragment);
    }

    [Fact]
    public async Task Unknown_provider_status_surfaces_the_raw_http_code()
    {
        var stub = new StubPrService { ThrowOnMerge = new ProviderApiException(ProviderKind.GitHub, 500, "MergePullRequestAsync", "boom", new Exception()) };

        var result = await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("HTTP 500");
    }

    private static NodeRunContext Context() => ContextFrom(new()
    {
        ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        ["number"] = JsonSerializer.SerializeToElement(42),
    });

    // Default context carries the run's team in sys scope (as the engine always does) so the node resolves it.
    private static NodeRunContext ContextFrom(Dictionary<string, JsonElement> inputs) =>
        ContextWithSys(inputs, new() { [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(Team) });

    private static NodeRunContext ContextWithSys(Dictionary<string, JsonElement> inputs, Dictionary<string, JsonElement> sys) => new()
    {
        Inputs = inputs,
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>(), Sys = sys },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };
}
