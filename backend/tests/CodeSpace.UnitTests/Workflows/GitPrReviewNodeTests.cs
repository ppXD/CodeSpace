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

[Trait("Category", "Unit")]
public class GitPrReviewNodeTests
{
    /// <summary>Hand-rolled stub (no mocking lib) — records the review call, returns a canned result; reads throw.</summary>
    private sealed class StubPrService : IPullRequestService
    {
        public Guid RepoId;
        public int Number;
        public PullRequestReviewVerdict Verdict;
        public string? Body;
        public Guid? ActorUserId;
        public int Calls;
        public Exception? ThrowOnReview;

        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid repositoryId, int number, PullRequestReviewVerdict verdict, string? body, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; Number = number; Verdict = verdict; Body = body; ActorUserId = actorUserId; Calls++;
            if (ThrowOnReview != null) throw ThrowOnReview;
            return Task.FromResult(new RemotePullRequestReview { Verdict = verdict, ExternalId = "rev-1", WebUrl = "https://example.test/review/1" });
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid r, PullRequestState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> GetAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid r, OpenPullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid r, int n, MergePullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    private const string Repo = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Submits_the_parsed_verdict_and_body_and_outputs_the_verdict_and_url()
    {
        var stub = new StubPrService();

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 42, "approve", "ship it"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.RepoId.ShouldBe(Guid.Parse(Repo));
        stub.Number.ShouldBe(42);
        stub.Verdict.ShouldBe(PullRequestReviewVerdict.Approve);
        stub.Body.ShouldBe("ship it");
        stub.ActorUserId.ShouldBeNull("no actAsUserId wired → null → service uses the connection credential");

        result.Outputs["verdict"].GetString().ShouldBe("Approve");
        result.Outputs["url"].GetString().ShouldBe("https://example.test/review/1");
    }

    [Fact]
    public async Task Passes_actAsUserId_through_when_wired()
    {
        var stub = new StubPrService();
        var actor = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(3),
            ["verdict"] = JsonSerializer.SerializeToElement("approve"),
            ["actAsUserId"] = JsonSerializer.SerializeToElement(actor.ToString()),
        };

        var result = await new GitPrReviewNode(stub).RunAsync(ContextFromInputs(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.ActorUserId.ShouldBe(actor, "a wired actAsUserId must reach the service so it acts as that user's identity");
    }

    [Fact]
    public async Task Approve_without_a_body_delegates_a_null_body()
    {
        var stub = new StubPrService();

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 9, "approve", body: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Verdict.ShouldBe(PullRequestReviewVerdict.Approve);
        stub.Body.ShouldBeNull();
    }

    [Theory]
    [InlineData("request_changes", PullRequestReviewVerdict.RequestChanges)]
    [InlineData("RequestChanges", PullRequestReviewVerdict.RequestChanges)]
    [InlineData("comment", PullRequestReviewVerdict.Comment)]
    [InlineData("Approve", PullRequestReviewVerdict.Approve)]
    public async Task Parses_the_verdict_tolerantly_of_snake_case_and_casing(string raw, PullRequestReviewVerdict expected)
    {
        var stub = new StubPrService();

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 7, raw, "x"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Verdict.ShouldBe(expected);
    }

    [Fact]
    public async Task Fails_for_an_unknown_verdict()
    {
        var result = await new GitPrReviewNode(new StubPrService()).RunAsync(BuildContext(Repo, 1, "merge", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("verdict");
    }

    [Fact]
    public async Task Provider_insufficient_scope_fails_with_an_actionable_scope_message()
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderInsufficientScopeException(ProviderKind.GitLab, "IPullRequestReviewCapability", new[] { "api" }, Array.Empty<string>(), "hint") };

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 42, "approve", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("api");
        result.Error.ShouldContain("scope");
        result.Error!.ShouldNotContain("HTTP", customMessage: "a scope gap must read as a scope message, not a raw SDK string");
    }

    [Fact]
    public async Task Provider_403_fails_with_a_permission_message_not_a_scope_message()
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderApiException(ProviderKind.GitLab, 403, "SubmitReviewAsync", "403 Forbidden", new Exception()) };

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 42, "approve", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("permission");
        result.Error!.ShouldNotContain("scope", customMessage: "a 403 is a permission problem — must not be mislabelled a scope gap");
    }

    [Fact]
    public async Task Provider_404_fails_with_a_not_found_message()
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderApiException(ProviderKind.GitHub, 404, "SubmitReviewAsync", "Not Found", new Exception()) };

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 7, "approve", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("couldn't find");
        result.Error.ShouldContain("#7");
    }

    [Fact]
    public async Task Provider_422_fails_with_a_self_approval_message()
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderApiException(ProviderKind.GitHub, 422, "SubmitReviewAsync", "Unprocessable Entity", new Exception()) };

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 99, "approve", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("own pull request");
        result.Error.ShouldContain("#99");
    }

    [Fact]
    public async Task Fails_when_repository_id_is_missing()
    {
        var result = await new GitPrReviewNode(new StubPrService()).RunAsync(BuildContext(repositoryId: null, number: 1, verdict: "approve", body: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
    }

    [Fact]
    public async Task Fails_when_number_is_missing()
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["verdict"] = JsonSerializer.SerializeToElement("approve"),
        };

        var result = await new GitPrReviewNode(new StubPrService()).RunAsync(ContextFromInputs(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("number");
    }

    private static NodeRunContext BuildContext(string? repositoryId, int number, string verdict, string? body)
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["number"] = JsonSerializer.SerializeToElement(number),
            ["verdict"] = JsonSerializer.SerializeToElement(verdict),
        };
        if (repositoryId != null) inputs["repositoryId"] = JsonSerializer.SerializeToElement(repositoryId);
        if (body != null) inputs["body"] = JsonSerializer.SerializeToElement(body);

        return ContextFromInputs(inputs);
    }

    private static NodeRunContext ContextFromInputs(Dictionary<string, JsonElement> inputs) => new()
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
