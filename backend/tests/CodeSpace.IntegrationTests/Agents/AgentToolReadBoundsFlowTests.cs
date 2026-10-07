using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// What one pull-request list costs the team's GitLab connection, and what one tool result can carry, over the production
/// path end to end: the real <c>McpRequestHandler</c>, the real DI tool registry (<c>NodeAgentTool</c> →
/// <c>GitListPullRequestsNode</c> → <c>PullRequestService</c> → the real GitLab provider and NGitLab) and the real mediator
/// pipeline behind the Pulls tab, on Postgres, against a loopback GitLab that holds as many merge requests as anyone asks for.
///
/// <para>Covers: a page and page size an agent names are held to the list's ceilings and read as one request for that one
/// page (the deep-page walk spent a request per page before it); the Pulls tab's and the Issues tab's queries are held the
/// same way; one agent run spends only its share of the connection's requests a minute, so the Pulls tab is still served
/// while it lists; a list too large for one tool result reaches the model cut, as an error that says so; and a command
/// whose output is too large is answered as done, cut, and never invited to run again.</para>
///
/// <para>Fidelity: high for everything CodeSpace runs; GitLab is the loopback.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentToolReadBoundsFlowTests(PostgresFixture fixture)
{
    private const string MergeRequestsPath = "/api/v4/projects/4242/merge_requests?";
    private const string LabelsPath = "/api/v4/projects/4242/labels";
    private const string IssuesPath = "/api/v4/projects/4242/issues";

    [Fact]
    public async Task An_agents_list_asks_GitLab_for_one_bounded_page_whatever_it_names()
    {
        using var gitlab = LoopbackGitLab(titleLength: 8);
        var world = await SeedWorldAsync(gitlab.BaseUrl);

        using var scope = fixture.BeginScope();
        var result = await CallToolAsync(Handler(scope, world), "git.list_prs", new { repositoryId = world.RepositoryId.ToString(), page = 1_000_000, perPage = int.MaxValue });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse(Text(result));
        var page = ForgeQueryOf(gitlab.Requests.Where(r => r.PathAndQuery.Contains(MergeRequestsPath)).ShouldHaveSingleItem("one request for the one page, never a walk through the pages before it"));
        page["page"].ShouldBe(ListPullRequestsQuery.MaxPage.ToString());
        page["per_page"].ShouldBe(ListPullRequestsQuery.MaxPerPage.ToString());
        gitlab.Requests.Count(r => r.PathAndQuery.Contains(LabelsPath)).ShouldBe(1);
        gitlab.Requests.Count.ShouldBe(2, string.Join("\n", gitlab.Requests.Select(r => r.PathAndQuery)));
    }

    [Fact]
    public async Task The_pulls_tab_asks_GitLab_for_one_bounded_page_too()
    {
        using var gitlab = LoopbackGitLab(titleLength: 8);
        var world = await SeedWorldAsync(gitlab.BaseUrl);

        using var scope = fixture.BeginScopeAs(world.OwnerId, world.TeamId, Roles.Admin);
        var listed = await scope.Resolve<IMediator>().Send(new ListPullRequestsQuery { RepositoryId = world.RepositoryId, Page = 1_000_000, PerPage = int.MaxValue });

        listed.Count.ShouldBe(ListPullRequestsQuery.MaxPerPage);
        var page = ForgeQueryOf(gitlab.Requests.Where(r => r.PathAndQuery.Contains(MergeRequestsPath)).ShouldHaveSingleItem());
        page["page"].ShouldBe(ListPullRequestsQuery.MaxPage.ToString());
        page["per_page"].ShouldBe(ListPullRequestsQuery.MaxPerPage.ToString());
    }

    [Fact]
    public async Task A_list_too_large_for_one_tool_result_reaches_the_model_cut_and_says_so()
    {
        using var gitlab = LoopbackGitLab(titleLength: 2_000);
        var world = await SeedWorldAsync(gitlab.BaseUrl);

        using var scope = fixture.BeginScope();
        var result = await CallToolAsync(Handler(scope, world), "git.list_prs", new { repositoryId = world.RepositoryId.ToString(), perPage = 100 });
        var text = Text(result);

        result.GetProperty("isError").GetBoolean().ShouldBeTrue("a hundred 2,000-character titles are more than one tool result carries");
        result.TryGetProperty("structuredContent", out _).ShouldBeFalse();
        text.Length.ShouldBeLessThan(McpRequestHandler.MaxToolResultCharacters + 1_000);
        text.ShouldContain($"more than the {McpRequestHandler.MaxToolResultCharacters}");
        text.ShouldContain("\"number\":1,", customMessage: "the start of the list is kept");
    }

    [Fact]
    public async Task The_issues_tab_asks_GitLab_for_one_bounded_page_too()
    {
        using var gitlab = LoopbackGitLab(titleLength: 8);
        var world = await SeedWorldAsync(gitlab.BaseUrl);

        using var scope = fixture.BeginScopeAs(world.OwnerId, world.TeamId, Roles.Admin);
        var listed = await scope.Resolve<IMediator>().Send(new ListIssuesQuery { RepositoryId = world.RepositoryId, Page = 1_000_000, PerPage = int.MaxValue });

        listed.Count.ShouldBe(ListIssuesQuery.MaxPerPage);
        var page = ForgeQueryOf(gitlab.Requests.Where(r => r.PathAndQuery.Contains(IssuesPath)).ShouldHaveSingleItem("one request for the one page, never a walk through the pages before it"));
        page["page"].ShouldBe(ListIssuesQuery.MaxPage.ToString());
        page["per_page"].ShouldBe(ListIssuesQuery.MaxPerPage.ToString());
    }

    [Fact]
    public async Task One_agent_run_spends_only_its_share_of_the_connection_and_the_pulls_tab_is_still_served()
    {
        using var gitlab = LoopbackGitLab(titleLength: 8);
        var world = await SeedWorldAsync(gitlab.BaseUrl);
        const int requestsPerClosedPage = 5;   // two pages of each finished state, and the labels
        var refusals = new List<string>();

        using (var scope = fixture.BeginScope())
        {
            var handler = Handler(scope, world);

            for (var i = 0; i < 30; i++)
            {
                var result = await CallToolAsync(handler, "git.list_prs", new { repositoryId = world.RepositoryId.ToString(), state = "Closed", page = 2, perPage = 100 });
                if (result.GetProperty("isError").GetBoolean()) refusals.Add(Text(result));
            }
        }

        gitlab.Requests.Count.ShouldBe(ExternalCallResilience.TokensPerMinutePerAgentRun, "the run's share of the connection, and not one request more");
        refusals.Count.ShouldBe(30 - ExternalCallResilience.TokensPerMinutePerAgentRun / requestsPerClosedPage);
        refusals.ShouldAllBe(text => text.Contains("has used its share of"));

        using var victim = fixture.BeginScopeAs(world.OwnerId, world.TeamId, Roles.Admin);
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listed = await victim.Resolve<IMediator>().Send(new ListPullRequestsQuery { RepositoryId = world.RepositoryId, Page = 1, PerPage = 10 }, patience.Token);

        listed.Count.ShouldBe(10, "the Pulls tab is served from the rest of the connection's minute — if this timed out, the run drained the whole bucket");
    }

    [Fact]
    public async Task A_command_whose_output_is_too_large_to_carry_is_answered_as_done_and_is_not_run_again()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"codespace-side-effect-{Guid.NewGuid():N}.txt");
        var arguments = new { command = "sh", args = new[] { "-c", $"echo ran >> '{marker}'; head -c 150000 /dev/zero | tr '\\0' a" } };

        try
        {
            using var gitlab = LoopbackGitLab(titleLength: 8);
            var world = await SeedWorldAsync(gitlab.BaseUrl);
            using var scope = fixture.BeginScope();
            var handler = new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Unleashed, world.TeamId, null, world.RunId, scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true);

            var first = await CallToolAsync(handler, "agent.run_command", arguments);
            var text = Text(first);

            first.GetProperty("isError").GetBoolean().ShouldBeFalse(text[..Math.Min(400, text.Length)]);
            text.ShouldStartWith("This call ran and its side effects are done");
            text.ShouldNotContain("Ask for less");
            text.Length.ShouldBeLessThan(McpRequestHandler.MaxToolResultCharacters + 1_000);
            first.GetProperty("structuredContent").GetProperty("stdout").GetString()!.ShouldStartWith("aaaa", customMessage: "the declared structure is kept, its long text cut");

            var row = (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(world.RunId, world.TeamId, CancellationToken.None)).ShouldHaveSingleItem();
            row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the command ran; only its answer was cut");
            Text(JsonDocument.Parse(row.ResultJson!).RootElement).ShouldBe(text, "the row records the answer the model got (jsonb keeps its meaning, not its spelling)");

            Text(await CallToolAsync(handler, "agent.run_command", arguments)).ShouldBe(text, "an identical re-call replays the answer");
            File.ReadAllLines(marker).Length.ShouldBe(1, "the command ran once");
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    // ── Handler and calls ────────────────────────────────────────────────────

    private static McpRequestHandler Handler(ILifetimeScope scope, World world) =>
        new(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Standard, world.TeamId, null, world.RunId, scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true,
            repositories: [new WorkspaceRepositorySpec { Alias = "api", RepositoryId = world.RepositoryId, Access = WorkspaceAccess.Read, Ref = "main" }]);

    private static async Task<JsonElement> CallToolAsync(McpRequestHandler handler, string name, object arguments)
    {
        var request = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });

        return (await handler.HandleAsync(request, CancellationToken.None))!.Value.GetProperty("result");
    }

    private static string Text(JsonElement toolResult) => toolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    private static System.Collections.Specialized.NameValueCollection ForgeQueryOf(RecordedRequest request) => System.Web.HttpUtility.ParseQueryString(new Uri(new Uri("http://stub"), request.PathAndQuery).Query);

    // ── The loopback GitLab ──────────────────────────────────────────────────

    /// <summary>
    /// acme/api with as many merge requests and issues as the deepest page anyone may ask for, paged GitLab's way:
    /// <c>per_page</c> rows (20 unless asked, never more than 100), each merge request titled <paramref name="titleLength"/>
    /// characters long, and a <c>Link</c> to the next page while there is one. Finite, so a list that walks pages fails on the
    /// count rather than walking forever.
    /// </summary>
    private static StubProviderHost LoopbackGitLab(int titleLength)
    {
        const int total = ListPullRequestsQuery.MaxPage * ListPullRequestsQuery.MaxPerPage;
        var host = new StubProviderHost();

        host.Answer("GET", IssuesPath, request =>
        {
            var query = ForgeQueryOf(request);
            var perPage = Math.Min(long.Parse(query["per_page"] ?? "20"), 100);
            var page = long.Parse(query["page"] ?? "1");
            var first = (page - 1) * perPage + 1;
            var rows = Enumerable.Range((int)Math.Min(first, total + 1), (int)Math.Clamp(total - first + 1, 0, perPage)).Select(number => new
            {
                id = number, iid = number, project_id = 4242, title = $"Issue {number}", state = "opened", author = new { id = 1, username = "dev", name = "Dev" }, labels = Array.Empty<string>(),
                created_at = "2026-09-24T08:00:00.000Z", updated_at = "2026-09-24T08:00:00.000Z", web_url = $"https://gitlab.test/acme/api/-/issues/{number}",
            });

            return new StubReply(200, JsonSerializer.Serialize(rows)) { Headers = new Dictionary<string, string> { ["X-Next-Page"] = page * perPage < total ? (page + 1).ToString() : "" } };
        });

        host.Answer("GET", LabelsPath, 200, "[]").Answer("GET", MergeRequestsPath, request =>
        {
            var query = ForgeQueryOf(request);
            var perPage = Math.Min(long.Parse(query["per_page"] ?? "20"), 100);
            var page = long.Parse(query["page"] ?? "1");
            var first = (page - 1) * perPage + 1;
            var count = (int)Math.Clamp(total - first + 1, 0, perPage);
            var rows = Enumerable.Range((int)Math.Min(first, total + 1), count).Select(number => new
            {
                id = number, iid = number, project_id = 4242, title = new string('t', titleLength), state = "opened", source_branch = $"f{number}", target_branch = "main",
                author = new { id = 1, username = "dev", name = "Dev" }, labels = Array.Empty<string>(), created_at = "2026-09-24T08:00:00.000Z", updated_at = "2026-09-24T08:00:00.000Z",
                web_url = $"https://gitlab.test/acme/api/-/merge_requests/{number}",
            });
            var hasNext = page * perPage < total;
            var headers = new Dictionary<string, string> { ["X-Next-Page"] = hasNext ? (page + 1).ToString() : "" };

            if (hasNext) headers["Link"] = $"<{host.BaseUrl}/api/v4/projects/4242/merge_requests?per_page={perPage}&page={page + 1}>; rel=\"next\"";

            return new StubReply(200, JsonSerializer.Serialize(rows)) { Headers = headers };
        });

        return host;
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid OwnerId, Guid RepositoryId, Guid RunId);

    /// <summary>A team with an owner, and acme/api on a loopback GitLab reached with a team connection credential.</summary>
    private async Task<World> SeedWorldAsync(string gitlabBaseUrl)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownerId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();

        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "loopback", BaseUrl = gitlabBaseUrl, ApiUrl = gitlabBaseUrl };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "connection",
            EncryptedPayload = encryptor.Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "glpat-fake-loopback" })), Status = CredentialStatus.Active,
        };

        db.User.Add(new User { Id = ownerId, Email = $"bounds-{suffix}@test.local", Name = $"bounds-{suffix}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"bounds-{suffix}", Name = "Bounds Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = ownerId, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(new Repository
        {
            Id = repositoryId, TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://gitlab.test/acme/api", Status = RepositoryStatus.Active,
        });

        await db.SaveChangesAsync();

        return new World(teamId, ownerId, repositoryId, Guid.NewGuid());
    }
}
