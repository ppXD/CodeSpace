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
/// <c>git.close_issue</c> — drives the real node against a stub <see cref="IIssueService"/>, pinning input
/// parsing (required repositoryId/number, actAsUserId), the output shape (number/state/url), and the
/// typed-provider-failure → actionable message mapping.
/// </summary>
[Trait("Category", "Unit")]
public class GitCloseIssueNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";
    private const string Team = "22222222-2222-2222-2222-222222222222";

    private sealed class StubIssueService : IIssueService
    {
        public Guid RepoId;
        public Guid TeamId;
        public int Number;
        public Guid? ActorUserId;
        public int Calls;
        public Exception? ThrowOnClose;

        public Task<RemoteIssue> CloseAsync(Guid repositoryId, Guid teamId, int number, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; TeamId = teamId; Number = number; ActorUserId = actorUserId; Calls++;
            if (ThrowOnClose != null) throw ThrowOnClose;
            return Task.FromResult(new RemoteIssue
            {
                ExternalId = "issue-1", Number = number, Title = "x", State = IssueState.Closed,
                CreatedDate = DateTimeOffset.UnixEpoch, WebUrl = "https://example.test/issues/42",
            });
        }

        public Task<RemoteIssue> CreateAsync(Guid r, Guid t, CreateIssueInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemoteIssueComment> CommentAsync(Guid r, Guid t, int n, string b, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemoteIssue>> ListAsync(Guid r, Guid t, IssueState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemoteIssueCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<RemoteIssue> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemoteIssueComment>> ListCommentsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemoteIssueEvent>> ListEventsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Closes_and_outputs_number_state_url()
    {
        var stub = new StubIssueService();

        var result = await new GitCloseIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.RepoId.ShouldBe(Guid.Parse(Repo));
        stub.Number.ShouldBe(42);
        stub.ActorUserId.ShouldBeNull();

        result.Outputs["number"].GetInt32().ShouldBe(42);
        result.Outputs["state"].GetString().ShouldBe("Closed");
        result.Outputs["url"].GetString().ShouldBe("https://example.test/issues/42");
    }

    [Fact]
    public async Task Passes_actAsUserId_through_when_wired()
    {
        var stub = new StubIssueService();
        var actor = Guid.NewGuid();

        await new GitCloseIssueNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["actAsUserId"] = JsonSerializer.SerializeToElement(actor.ToString()),
        }), CancellationToken.None);

        stub.ActorUserId.ShouldBe(actor, "a wired actAsUserId must reach the service so the close is attributed to that user");
    }

    [Fact]
    public async Task Threads_the_run_team_from_sys_scope_into_the_service_call()
    {
        var stub = new StubIssueService();

        await new GitCloseIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        stub.TeamId.ShouldBe(Guid.Parse(Team), "the run's team flows from {{sys.team_id}} so the service fail-closes the repo load to it");
    }

    [Fact]
    public async Task Fails_closed_when_sys_scope_has_no_team()
    {
        var stub = new StubIssueService();

        var result = await new GitCloseIssueNode(stub).RunAsync(ContextWithSys(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
        }, new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("team context");
        stub.Calls.ShouldBe(0, "without a team the node must short-circuit before touching the service");
    }

    [Fact]
    public async Task Fails_when_repository_id_is_missing()
    {
        var stub = new StubIssueService();
        var result = await new GitCloseIssueNode(stub).RunAsync(ContextFrom(new()
        {
            ["number"] = JsonSerializer.SerializeToElement(42),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
        stub.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Fails_when_number_is_missing()
    {
        var stub = new StubIssueService();
        var result = await new GitCloseIssueNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("number");
        stub.Calls.ShouldBe(0, "a missing required field must short-circuit before the provider call");
    }

    [Fact]
    public async Task Insufficient_scope_fails_with_an_actionable_scope_message()
    {
        var stub = new StubIssueService { ThrowOnClose = new ProviderInsufficientScopeException(ProviderKind.GitLab, "IIssueWriteCapability", new[] { "api" }, Array.Empty<string>(), "hint") };

        var result = await new GitCloseIssueNode(stub).RunAsync(Context(), CancellationToken.None);

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
        var stub = new StubIssueService { ThrowOnClose = new ProviderApiException(ProviderKind.GitHub, status, "CloseIssueAsync", "boom", new Exception()) };

        var result = await new GitCloseIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("#42");
        result.Error.ShouldContain(expectedFragment);
    }

    [Fact]
    public async Task Unknown_provider_status_surfaces_the_raw_http_code()
    {
        var stub = new StubIssueService { ThrowOnClose = new ProviderApiException(ProviderKind.GitHub, 500, "CloseIssueAsync", "boom", new Exception()) };

        var result = await new GitCloseIssueNode(stub).RunAsync(Context(), CancellationToken.None);

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
