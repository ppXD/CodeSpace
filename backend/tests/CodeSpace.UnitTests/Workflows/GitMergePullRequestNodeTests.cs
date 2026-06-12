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
/// actAsUserId), the output shape (merged/sha/message), and the typed-provider-failure → actionable-message
/// mapping (scope, 403, 404, 405, 409, 422) are all pinned.
/// </summary>
[Trait("Category", "Unit")]
public class GitMergePullRequestNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";

    private sealed class StubPrService : IPullRequestService
    {
        public Guid RepoId;
        public int Number;
        public MergePullRequestInput? Input;
        public Guid? ActorUserId;
        public int Calls;
        public Exception? ThrowOnMerge;
        public RemotePullRequestMergeResult Result = new() { Merged = true, Sha = "abc123", Message = "Merged" };

        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid repositoryId, int number, MergePullRequestInput input, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; Number = number; Input = input; ActorUserId = actorUserId; Calls++;
            if (ThrowOnMerge != null) throw ThrowOnMerge;
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid r, PullRequestState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> GetAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid r, int n, PullRequestReviewVerdict v, string? b, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid r, OpenPullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
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
    public async Task Outputs_merged_false_when_the_provider_reports_not_merged()
    {
        var stub = new StubPrService { Result = new() { Merged = false, Sha = null, Message = "not mergeable" } };

        var result = await new GitMergePullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "a clean 'not merged' answer from the provider is a successful node run with merged=false");
        result.Outputs["merged"].GetBoolean().ShouldBeFalse();
        result.Outputs["sha"].ValueKind.ShouldBe(JsonValueKind.Null);
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

    private static NodeRunContext ContextFrom(Dictionary<string, JsonElement> inputs) => new()
    {
        Inputs = inputs,
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };
}
