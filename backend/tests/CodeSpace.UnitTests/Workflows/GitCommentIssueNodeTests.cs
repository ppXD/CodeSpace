using System.Text.Json;
using CodeSpace.Core.Services.Issues;
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
/// <c>git.comment_issue</c> — drives the real node against a stub <see cref="IIssueService"/> that records
/// the call and returns a canned comment (or throws), pinning input parsing (required repositoryId/number/
/// body, actAsUserId), the output shape (commentId/webUrl), and the typed-provider-failure → actionable
/// message mapping.
/// </summary>
[Trait("Category", "Unit")]
public class GitCommentIssueNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";
    private const string Team = "22222222-2222-2222-2222-222222222222";

    private sealed class StubIssueService : IIssueService
    {
        public Guid RepoId;
        public Guid TeamId;
        public int Number;
        public string? Body;
        public Guid? ActorUserId;
        public int Calls;
        public Exception? ThrowOnComment;

        public Task<RemoteIssueComment> CommentAsync(Guid repositoryId, Guid teamId, int number, string body, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; TeamId = teamId; Number = number; Body = body; ActorUserId = actorUserId; Calls++;
            if (ThrowOnComment != null) throw ThrowOnComment;
            return Task.FromResult(new RemoteIssueComment
            {
                ExternalId = "note-7", Body = body, AuthorName = "alice", CreatedAt = DateTimeOffset.UnixEpoch,
                WebUrl = "https://example.test/issues/42#note-7",
            });
        }

        public Task<RemoteIssue> CreateAsync(Guid r, Guid t, CreateIssueInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemoteIssue> CloseAsync(Guid r, Guid t, int n, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Comments_and_outputs_commentId_and_url()
    {
        var stub = new StubIssueService();

        var result = await new GitCommentIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.RepoId.ShouldBe(Guid.Parse(Repo));
        stub.Number.ShouldBe(42);
        stub.Body.ShouldBe("looks good");
        stub.ActorUserId.ShouldBeNull();

        result.Outputs["commentId"].GetString().ShouldBe("note-7");
        result.Outputs["webUrl"].GetString().ShouldBe("https://example.test/issues/42#note-7");
    }

    [Fact]
    public async Task Passes_actAsUserId_through_when_wired()
    {
        var stub = new StubIssueService();
        var actor = Guid.NewGuid();

        await new GitCommentIssueNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["body"] = JsonSerializer.SerializeToElement("hi"),
            ["actAsUserId"] = JsonSerializer.SerializeToElement(actor.ToString()),
        }), CancellationToken.None);

        stub.ActorUserId.ShouldBe(actor, "a wired actAsUserId must reach the service so the comment is attributed to that user");
    }

    [Fact]
    public async Task Threads_the_run_team_from_sys_scope_into_the_service_call()
    {
        var stub = new StubIssueService();

        await new GitCommentIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        stub.TeamId.ShouldBe(Guid.Parse(Team), "the run's team flows from {{sys.team_id}} so the service fail-closes the repo load to it");
    }

    [Fact]
    public async Task Fails_closed_when_sys_scope_has_no_team()
    {
        var stub = new StubIssueService();

        var result = await new GitCommentIssueNode(stub).RunAsync(ContextWithSys(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["body"] = JsonSerializer.SerializeToElement("looks good"),
        }, new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("team context");
        stub.Calls.ShouldBe(0, "without a team the node must short-circuit before touching the service");
    }

    [Theory]
    [InlineData("repositoryId")]
    [InlineData("number")]
    [InlineData("body")]
    public async Task Fails_when_a_required_field_is_missing(string omit)
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["body"] = JsonSerializer.SerializeToElement("hi"),
        };
        inputs.Remove(omit);

        var stub = new StubIssueService();
        var result = await new GitCommentIssueNode(stub).RunAsync(ContextFrom(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        stub.Calls.ShouldBe(0, "a missing required field must short-circuit before the provider call");
    }

    [Fact]
    public async Task Insufficient_scope_fails_with_an_actionable_scope_message()
    {
        var stub = new StubIssueService { ThrowOnComment = new ProviderInsufficientScopeException(ProviderKind.GitLab, "IIssueWriteCapability", new[] { "api" }, Array.Empty<string>(), "hint") };

        var result = await new GitCommentIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("api");
        result.Error.ShouldContain("scope");
        result.Error!.ShouldNotContain("HTTP", customMessage: "a scope gap must read as a scope message, not a raw SDK string");
    }

    [Theory]
    [InlineData(403, "permission")]
    [InlineData(404, "find")]
    [InlineData(410, "disabled")]
    public async Task Provider_http_failure_maps_to_an_actionable_message(int status, string expectedFragment)
    {
        var stub = new StubIssueService { ThrowOnComment = new ProviderApiException(ProviderKind.GitHub, status, "CommentIssueAsync", "boom", new Exception()) };

        var result = await new GitCommentIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("#42");
        result.Error.ShouldContain(expectedFragment);
    }

    [Fact]
    public async Task Unknown_provider_status_surfaces_the_raw_http_code()
    {
        var stub = new StubIssueService { ThrowOnComment = new ProviderApiException(ProviderKind.GitHub, 500, "CommentIssueAsync", "boom", new Exception()) };

        var result = await new GitCommentIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("HTTP 500");
    }

    private static NodeRunContext Context() => ContextFrom(new()
    {
        ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        ["number"] = JsonSerializer.SerializeToElement(42),
        ["body"] = JsonSerializer.SerializeToElement("looks good"),
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
