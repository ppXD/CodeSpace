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
    public async Task Fails_on_a_malformed_repositories_array(string shape)
    {
        var stub = new StubChangeSetService();
        var inputs = new Dictionary<string, JsonElement> { ["title"] = JsonSerializer.SerializeToElement("t") };

        inputs["repositories"] = shape switch
        {
            "empty" => JsonSerializer.SerializeToElement(Array.Empty<object>()),
            "no-repo-id" => JsonSerializer.SerializeToElement(new[] { new { sourceBranch = "b", targetBranch = "main" } }),
            _ => JsonSerializer.SerializeToElement("not-an-array"),
        };
        if (shape == "missing") inputs.Remove("repositories");

        var result = await new GitOpenChangeSetNode(stub).RunAsync(ContextFrom(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositories");
        stub.Calls.ShouldBe(0, "a malformed array must short-circuit before any open");
    }

    [Fact]
    public async Task Binds_agent_code_repositoryResults_verbatim_via_producedBranch_and_baseBranch()
    {
        var stub = new StubChangeSetService();

        // The exact shape agent.code emits as repositoryResults — fed straight in, no re-authoring of source/target.
        await new GitOpenChangeSetNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositories"] = JsonSerializer.SerializeToElement(new[]
            {
                new { alias = "web", repositoryId = Web, producedBranch = "codespace/run-x", baseSha = "abc", baseBranch = "main" },
                new { alias = "api", repositoryId = Api, producedBranch = "codespace/run-x", baseSha = "def", baseBranch = "develop" },
            }),
            ["title"] = JsonSerializer.SerializeToElement("Coordinated change"),
        }), CancellationToken.None);

        stub.Calls.ShouldBe(1);
        var web = stub.Spec!.Repositories.Single(r => r.RepositoryId == Web);
        web.SourceBranch.ShouldBe("codespace/run-x", "producedBranch is read as the head with no sourceBranch present");
        web.TargetBranch.ShouldBe("main", "baseBranch is read as the PR target with no targetBranch present");
        stub.Spec.Repositories.Single(r => r.RepositoryId == Api).TargetBranch.ShouldBe("develop");
    }

    [Fact]
    public async Task Binds_supervisor_repositoryBranches_verbatim_via_sourceBranch_and_targetBranch()
    {
        var stub = new StubChangeSetService();

        // The exact shape agent.supervisor emits as repositoryBranches (the SupervisorRepositoryBranch noun serialized) —
        // fed straight into git.open_change_set, proving the resolver loop's last mile is a workflow edge, not a new node.
        var repositoryBranches = new[]
        {
            new SupervisorRepositoryBranch { RepositoryId = Web, Alias = "web", SourceBranch = "codespace/integration/run/turn4", TargetBranch = "main" },
            new SupervisorRepositoryBranch { RepositoryId = Api, Alias = "api", SourceBranch = "codespace/resolve/api-reconciled", TargetBranch = "develop" },
        };

        await new GitOpenChangeSetNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositories"] = JsonSerializer.SerializeToElement(repositoryBranches, CodeSpace.Core.Services.Agents.AgentJson.Options),
            ["title"] = JsonSerializer.SerializeToElement("Coordinated multi-repo change"),
        }), CancellationToken.None);

        stub.Calls.ShouldBe(1);
        var web = stub.Spec!.Repositories.Single(r => r.RepositoryId == Web);
        web.SourceBranch.ShouldBe("codespace/integration/run/turn4", "the supervisor's reconciled head binds as the PR source via sourceBranch");
        web.TargetBranch.ShouldBe("main", "the supervisor's per-repo base binds as the PR target via targetBranch");
        var api = stub.Spec.Repositories.Single(r => r.RepositoryId == Api);
        api.SourceBranch.ShouldBe("codespace/resolve/api-reconciled", "an accepted-resolution branch opens its PR too — the loop's last mile");
        api.TargetBranch.ShouldBe("develop");
    }

    [Fact]
    public async Task Prefers_producedBranch_over_a_hand_authored_sourceBranch_alias()
    {
        var stub = new StubChangeSetService();

        await new GitOpenChangeSetNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositories"] = JsonSerializer.SerializeToElement(new[]
            {
                // Both keys present (unusual) — the canonical repositoryResults field wins.
                new { repositoryId = Web, producedBranch = "from-results", sourceBranch = "hand-authored", baseBranch = "main", targetBranch = "ignored" },
            }),
            ["title"] = JsonSerializer.SerializeToElement("t"),
        }), CancellationToken.None);

        var web = stub.Spec!.Repositories.Single();
        web.SourceBranch.ShouldBe("from-results");
        web.TargetBranch.ShouldBe("main");
    }

    [Fact]
    public async Task Passes_an_entry_with_no_head_or_base_through_to_the_service_to_classify()
    {
        var stub = new StubChangeSetService();

        // A degraded repositoryResults entry (no branch at all) is NOT a node-level malformation — the service decides
        // Skipped (no head) vs Failed (head, no base), so binding the whole array verbatim never crashes the node.
        var result = await new GitOpenChangeSetNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositories"] = JsonSerializer.SerializeToElement(new[] { new { repositoryId = Web } }),
            ["title"] = JsonSerializer.SerializeToElement("t"),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        var only = stub.Spec!.Repositories.Single();
        only.SourceBranch.ShouldBe("");
        only.TargetBranch.ShouldBe("");
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
