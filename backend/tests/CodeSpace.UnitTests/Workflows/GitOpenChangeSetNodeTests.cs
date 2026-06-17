using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <c>git.open_change_set</c> — drives the real node against a stub <see cref="IChangeSetService"/>: pins the
/// <c>repositories</c> array parsing, the team fail-close, the title/body/draft threading, and that per-repo failures
/// are a ROUTABLE outcome (the node SUCCEEDS, the workflow branches on <c>failedCount</c>), not a node crash.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitOpenChangeSetNodeTests
{
    private static readonly Guid Web = Guid.NewGuid();
    private static readonly Guid Api = Guid.NewGuid();
    private const string Team = "22222222-2222-2222-2222-222222222222";

    private sealed class StubChangeSetService : IChangeSetService
    {
        public Guid TeamId;
        public ChangeSetPullRequestSpec? Spec;
        public Guid? ActorUserId;
        public int Calls;
        public ChangeSetResult Result = new() { PullRequests = Array.Empty<ChangeSetPullRequestOutcome>() };

        public Task<ChangeSetResult> OpenPullRequestsAsync(Guid teamId, ChangeSetPullRequestSpec spec, Guid? actorUserId, CancellationToken cancellationToken)
        {
            TeamId = teamId; Spec = spec; ActorUserId = actorUserId; Calls++;
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Parses_the_repositories_array_and_threads_title_body_draft_into_the_spec()
    {
        var stub = new StubChangeSetService();

        await new GitOpenChangeSetNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositories"] = JsonSerializer.SerializeToElement(new[]
            {
                new { repositoryId = Web, sourceBranch = "codespace/run-x", targetBranch = "main" },
                new { repositoryId = Api, sourceBranch = "codespace/run-x", targetBranch = "develop" },
            }),
            ["title"] = JsonSerializer.SerializeToElement("Coordinated change"),
            ["body"] = JsonSerializer.SerializeToElement("details"),
            ["draft"] = JsonSerializer.SerializeToElement(true),
        }), CancellationToken.None);

        stub.Calls.ShouldBe(1);
        stub.TeamId.ShouldBe(Guid.Parse(Team), "the run's team flows so each repo opens team-scoped");
        stub.ActorUserId.ShouldBeNull("v1 opens as the repo connection credential, never a user");
        stub.Spec!.Title.ShouldBe("Coordinated change");
        stub.Spec.Body.ShouldBe("details");
        stub.Spec.Draft.ShouldBeTrue();
        stub.Spec.Repositories.Count.ShouldBe(2);
        stub.Spec.Repositories.Single(r => r.RepositoryId == Web).TargetBranch.ShouldBe("main");
        stub.Spec.Repositories.Single(r => r.RepositoryId == Api).TargetBranch.ShouldBe("develop");
    }

    [Fact]
    public async Task Maps_the_change_set_result_into_outputs()
    {
        var stub = new StubChangeSetService
        {
            Result = new ChangeSetResult
            {
                OpenedCount = 1, SkippedCount = 0, FailedCount = 1,
                PullRequests = new[]
                {
                    new ChangeSetPullRequestOutcome { RepositoryId = Web, Disposition = ChangeSetPullRequestDisposition.Opened, Number = 7, Url = "https://x/pr/7", State = "Open" },
                    new ChangeSetPullRequestOutcome { RepositoryId = Api, Disposition = ChangeSetPullRequestDisposition.Failed, Error = "GitHub rejected it." },
                },
            },
        };

        var result = await new GitOpenChangeSetNode(stub).RunAsync(DefaultContext(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "a per-repo failure is a routable outcome, not a node crash — the workflow branches on failedCount");
        result.Outputs["openedCount"].GetInt32().ShouldBe(1);
        result.Outputs["failedCount"].GetInt32().ShouldBe(1);
        result.Outputs["pullRequests"].GetArrayLength().ShouldBe(2);
        result.Outputs["pullRequests"][0].GetProperty("disposition").GetString().ShouldBe("Opened");
        result.Outputs["pullRequests"][0].GetProperty("number").GetInt32().ShouldBe(7);
        result.Outputs["pullRequests"][1].GetProperty("disposition").GetString().ShouldBe("Failed");
    }

    [Fact]
    public async Task Fails_closed_without_a_team()
    {
        var stub = new StubChangeSetService();

        var result = await new GitOpenChangeSetNode(stub).RunAsync(ContextWithSys(DefaultInputs(), new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("team context");
        stub.Calls.ShouldBe(0, "without a team the node short-circuits before any open");
    }

    [Fact]
    public async Task Fails_when_title_is_missing()
    {
        var result = await new GitOpenChangeSetNode(new StubChangeSetService()).RunAsync(ContextFrom(new()
        {
            ["repositories"] = JsonSerializer.SerializeToElement(new[] { new { repositoryId = Web, sourceBranch = "b", targetBranch = "main" } }),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("title");
    }

    [Theory]
    [InlineData("missing")]   // no repositories key
    [InlineData("empty")]     // empty array
    [InlineData("no-repo-id")]
    [InlineData("no-target")]
    public async Task Fails_on_a_malformed_repositories_array(string shape)
    {
        var stub = new StubChangeSetService();
        var inputs = new Dictionary<string, JsonElement> { ["title"] = JsonSerializer.SerializeToElement("t") };

        inputs["repositories"] = shape switch
        {
            "empty" => JsonSerializer.SerializeToElement(Array.Empty<object>()),
            "no-repo-id" => JsonSerializer.SerializeToElement(new[] { new { sourceBranch = "b", targetBranch = "main" } }),
            "no-target" => JsonSerializer.SerializeToElement(new[] { new { repositoryId = Web, sourceBranch = "b" } }),
            _ => JsonSerializer.SerializeToElement("not-an-array"),
        };
        if (shape == "missing") inputs.Remove("repositories");

        var result = await new GitOpenChangeSetNode(stub).RunAsync(ContextFrom(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositories");
        stub.Calls.ShouldBe(0, "a malformed array must short-circuit before any open");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, JsonElement> DefaultInputs() => new()
    {
        ["repositories"] = JsonSerializer.SerializeToElement(new[] { new { repositoryId = Web, sourceBranch = "b", targetBranch = "main" } }),
        ["title"] = JsonSerializer.SerializeToElement("Coordinated change"),
    };

    private static NodeRunContext DefaultContext() => ContextFrom(DefaultInputs());

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
