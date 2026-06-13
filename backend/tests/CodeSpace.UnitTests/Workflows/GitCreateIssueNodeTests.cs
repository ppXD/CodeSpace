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
/// <c>git.create_issue</c> — drives the real node against a stub <see cref="IIssueService"/> that records the
/// <see cref="CreateIssueInput"/> it was called with and returns a canned issue (or throws), so input
/// parsing (required repositoryId/title, body, labels, actAsUserId), the output shape (number/url/state),
/// and the typed-provider-failure → actionable-message mapping are all pinned.
/// </summary>
[Trait("Category", "Unit")]
public class GitCreateIssueNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";

    private sealed class StubIssueService : IIssueService
    {
        public Guid RepoId;
        public CreateIssueInput? Input;
        public Guid? ActorUserId;
        public int Calls;
        public Exception? ThrowOnCreate;

        public Task<RemoteIssue> CreateAsync(Guid repositoryId, CreateIssueInput input, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; Input = input; ActorUserId = actorUserId; Calls++;
            if (ThrowOnCreate != null) throw ThrowOnCreate;
            return Task.FromResult(new RemoteIssue
            {
                ExternalId = "issue-1", Number = 321, Title = input.Title, State = IssueState.Open,
                Body = input.Body, CreatedDate = DateTimeOffset.UnixEpoch, WebUrl = "https://example.test/issues/321",
            });
        }

        public Task<RemoteIssueComment> CommentAsync(Guid r, int n, string b, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Creates_the_issue_and_outputs_number_url_state()
    {
        var stub = new StubIssueService();

        var result = await new GitCreateIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.RepoId.ShouldBe(Guid.Parse(Repo));
        stub.Input!.Title.ShouldBe("Bug: thing broke");
        stub.ActorUserId.ShouldBeNull();

        result.Outputs["number"].GetInt32().ShouldBe(321);
        result.Outputs["url"].GetString().ShouldBe("https://example.test/issues/321");
        result.Outputs["state"].GetString().ShouldBe("Open");
    }

    [Fact]
    public async Task Passes_body_labels_and_actAsUserId_through()
    {
        var stub = new StubIssueService();
        var actor = Guid.NewGuid();

        await new GitCreateIssueNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["title"] = JsonSerializer.SerializeToElement("Track flaky test"),
            ["body"] = JsonSerializer.SerializeToElement("details here"),
            ["labels"] = JsonSerializer.SerializeToElement(new[] { "bug", "  ", "flaky" }),
            ["actAsUserId"] = JsonSerializer.SerializeToElement(actor.ToString()),
        }), CancellationToken.None);

        stub.Input!.Body.ShouldBe("details here");
        stub.Input.Labels.ShouldBe(new[] { "bug", "flaky" }, "blank label entries are dropped, real ones trimmed-and-kept");
        stub.ActorUserId.ShouldBe(actor, "a wired actAsUserId must reach the service so the issue is authored by that user");
    }

    [Fact]
    public async Task Fails_when_repository_id_is_missing()
    {
        var stub = new StubIssueService();
        var result = await new GitCreateIssueNode(stub).RunAsync(ContextFrom(new()
        {
            ["title"] = JsonSerializer.SerializeToElement("x"),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
        stub.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Fails_when_title_is_missing()
    {
        var stub = new StubIssueService();
        var result = await new GitCreateIssueNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("title");
        stub.Calls.ShouldBe(0, "a missing required field must short-circuit before the provider call");
    }

    [Fact]
    public async Task Surfaces_the_services_validation_message()
    {
        var stub = new StubIssueService { ThrowOnCreate = new InvalidOperationException("An issue requires a title.") };

        var result = await new GitCreateIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("title");
    }

    [Fact]
    public async Task Insufficient_scope_fails_with_an_actionable_scope_message()
    {
        var stub = new StubIssueService { ThrowOnCreate = new ProviderInsufficientScopeException(ProviderKind.GitLab, "IIssueWriteCapability", new[] { "api" }, Array.Empty<string>(), "hint") };

        var result = await new GitCreateIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("api");
        result.Error.ShouldContain("scope");
        result.Error!.ShouldNotContain("HTTP", customMessage: "a scope gap must read as a scope message, not a raw SDK string");
    }

    [Theory]
    [InlineData(403, "permission")]
    [InlineData(404, "find")]
    [InlineData(410, "disabled")]
    [InlineData(422, "label")]
    public async Task Provider_http_failure_maps_to_an_actionable_message(int status, string expectedFragment)
    {
        var stub = new StubIssueService { ThrowOnCreate = new ProviderApiException(ProviderKind.GitHub, status, "CreateIssueAsync", "boom", new Exception()) };

        var result = await new GitCreateIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("issue");
        result.Error.ShouldContain(expectedFragment);
    }

    [Fact]
    public async Task Unknown_provider_status_surfaces_the_raw_http_code()
    {
        var stub = new StubIssueService { ThrowOnCreate = new ProviderApiException(ProviderKind.GitHub, 500, "CreateIssueAsync", "boom", new Exception()) };

        var result = await new GitCreateIssueNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("HTTP 500");
    }

    private static NodeRunContext Context() => ContextFrom(new()
    {
        ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        ["title"] = JsonSerializer.SerializeToElement("Bug: thing broke"),
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
