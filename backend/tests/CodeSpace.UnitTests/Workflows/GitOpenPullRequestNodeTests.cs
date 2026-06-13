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
/// <c>git.open_pr</c> — drives the real node against a stub <see cref="IPullRequestService"/> that records the
/// <see cref="OpenPullRequestInput"/> it was called with and returns a canned PR (or throws), so input
/// parsing (required title/branches, draft, actAsUserId), the output shape (number/url/state), and the
/// typed-provider-failure → actionable-message mapping are all pinned.
/// </summary>
[Trait("Category", "Unit")]
public class GitOpenPullRequestNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";
    private const string Team = "22222222-2222-2222-2222-222222222222";

    private sealed class StubPrService : IPullRequestService
    {
        public Guid RepoId;
        public Guid TeamId;
        public OpenPullRequestInput? Input;
        public Guid? ActorUserId;
        public int Calls;
        public Exception? ThrowOnOpen;

        public Task<RemotePullRequest> OpenPullRequestAsync(Guid repositoryId, Guid teamId, OpenPullRequestInput input, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; TeamId = teamId; Input = input; ActorUserId = actorUserId; Calls++;
            if (ThrowOnOpen != null) throw ThrowOnOpen;
            return Task.FromResult(new RemotePullRequest
            {
                ExternalId = "pr-1", Number = 123, Title = input.Title, State = input.Draft ? PullRequestState.Draft : PullRequestState.Open,
                SourceBranch = input.SourceBranch, TargetBranch = input.TargetBranch, CommentsCount = 0,
                CreatedDate = DateTimeOffset.UnixEpoch, UpdatedDate = DateTimeOffset.UnixEpoch, WebUrl = "https://example.test/pr/123",
            });
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid r, Guid t, PullRequestState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, Guid t, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid r, Guid t, int n, PullRequestReviewVerdict v, string? b, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid r, Guid t, int n, MergePullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Opens_the_pr_and_outputs_number_url_state()
    {
        var stub = new StubPrService();

        var result = await new GitOpenPullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.RepoId.ShouldBe(Guid.Parse(Repo));
        stub.Input!.Title.ShouldBe("Add feature");
        stub.Input.SourceBranch.ShouldBe("feature");
        stub.Input.TargetBranch.ShouldBe("main");
        stub.Input.Draft.ShouldBeFalse();
        stub.ActorUserId.ShouldBeNull();

        result.Outputs["number"].GetInt32().ShouldBe(123);
        result.Outputs["url"].GetString().ShouldBe("https://example.test/pr/123");
        result.Outputs["state"].GetString().ShouldBe("Open");
    }

    [Fact]
    public async Task Passes_draft_and_body_and_outputs_draft_state()
    {
        var stub = new StubPrService();

        var result = await new GitOpenPullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["title"] = JsonSerializer.SerializeToElement("WIP"),
            ["sourceBranch"] = JsonSerializer.SerializeToElement("feature"),
            ["targetBranch"] = JsonSerializer.SerializeToElement("main"),
            ["body"] = JsonSerializer.SerializeToElement("details"),
            ["draft"] = JsonSerializer.SerializeToElement(true),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Input!.Draft.ShouldBeTrue();
        stub.Input.Body.ShouldBe("details");
        result.Outputs["state"].GetString().ShouldBe("Draft");
    }

    [Fact]
    public async Task Passes_actAsUserId_through_when_wired()
    {
        var stub = new StubPrService();
        var actor = Guid.NewGuid();

        await new GitOpenPullRequestNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["title"] = JsonSerializer.SerializeToElement("t"),
            ["sourceBranch"] = JsonSerializer.SerializeToElement("a"),
            ["targetBranch"] = JsonSerializer.SerializeToElement("b"),
            ["actAsUserId"] = JsonSerializer.SerializeToElement(actor.ToString()),
        }), CancellationToken.None);

        stub.ActorUserId.ShouldBe(actor, "a wired actAsUserId must reach the service so the PR is authored by that user");
    }

    [Fact]
    public async Task Threads_the_run_team_from_sys_scope_into_the_service_call()
    {
        var stub = new StubPrService();

        await new GitOpenPullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        stub.TeamId.ShouldBe(Guid.Parse(Team), "the run's team flows from {{sys.team_id}} so the service fail-closes the repo load to it");
    }

    [Fact]
    public async Task Fails_closed_when_sys_scope_has_no_team()
    {
        var stub = new StubPrService();

        var result = await new GitOpenPullRequestNode(stub).RunAsync(ContextWithSys(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["title"] = JsonSerializer.SerializeToElement("Add feature"),
            ["sourceBranch"] = JsonSerializer.SerializeToElement("feature"),
            ["targetBranch"] = JsonSerializer.SerializeToElement("main"),
        }, new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("team context");
        stub.Calls.ShouldBe(0, "without a team the node must short-circuit before touching the service");
    }

    [Theory]
    [InlineData("title")]
    [InlineData("sourceBranch")]
    [InlineData("targetBranch")]
    public async Task Fails_when_a_required_field_is_missing(string omit)
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["title"] = JsonSerializer.SerializeToElement("t"),
            ["sourceBranch"] = JsonSerializer.SerializeToElement("a"),
            ["targetBranch"] = JsonSerializer.SerializeToElement("b"),
        };
        inputs.Remove(omit);

        var stub = new StubPrService();
        var result = await new GitOpenPullRequestNode(stub).RunAsync(ContextFrom(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain(omit);
        stub.Calls.ShouldBe(0, "a missing required field must short-circuit before the provider call");
    }

    [Fact]
    public async Task Fails_when_repository_id_is_missing()
    {
        var result = await new GitOpenPullRequestNode(new StubPrService()).RunAsync(ContextFrom(new()
        {
            ["title"] = JsonSerializer.SerializeToElement("t"),
            ["sourceBranch"] = JsonSerializer.SerializeToElement("a"),
            ["targetBranch"] = JsonSerializer.SerializeToElement("b"),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
    }

    [Fact]
    public async Task Surfaces_the_services_validation_message()
    {
        var stub = new StubPrService { ThrowOnOpen = new InvalidOperationException("The source and target branch must differ.") };

        var result = await new GitOpenPullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("differ");
    }

    [Fact]
    public async Task Insufficient_scope_fails_with_an_actionable_scope_message()
    {
        var stub = new StubPrService { ThrowOnOpen = new ProviderInsufficientScopeException(ProviderKind.GitLab, "IPullRequestWriteCapability", new[] { "api" }, Array.Empty<string>(), "hint") };

        var result = await new GitOpenPullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("api");
        result.Error.ShouldContain("scope");
        result.Error!.ShouldNotContain("HTTP", customMessage: "a scope gap must read as a scope message, not a raw SDK string");
    }

    [Fact]
    public async Task Provider_403_fails_with_a_permission_message()
    {
        var stub = new StubPrService { ThrowOnOpen = new ProviderApiException(ProviderKind.GitHub, 403, "OpenPullRequestAsync", "Forbidden", new Exception()) };

        var result = await new GitOpenPullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("permission");
    }

    [Fact]
    public async Task Provider_422_fails_with_a_validation_message()
    {
        var stub = new StubPrService { ThrowOnOpen = new ProviderApiException(ProviderKind.GitHub, 422, "OpenPullRequestAsync", "Unprocessable", new Exception()) };

        var result = await new GitOpenPullRequestNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("identical");
    }

    private static NodeRunContext Context() => ContextFrom(new()
    {
        ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        ["title"] = JsonSerializer.SerializeToElement("Add feature"),
        ["sourceBranch"] = JsonSerializer.SerializeToElement("feature"),
        ["targetBranch"] = JsonSerializer.SerializeToElement("main"),
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
