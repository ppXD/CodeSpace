using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// An agent's tool call reaches only the repositories its run is bound to, over the production path end to end:
/// <c>McpRequestHandler</c> (stamped with the run's bound repositories) → the real DI <c>IAgentToolRegistry</c> →
/// <c>NodeAgentTool</c> → the real builtin node → <c>RunCommandService</c> / <c>PullRequestService</c> on real Postgres →
/// a real git clone of a <c>file://</c> remote. The team holds a SECOND repository the run is not bound to, carrying a
/// secret on an unmerged branch: the shape the audit probe used to read it.
///
/// <para>Covers the boundary on every repository-taking tool (an unbound same-team repository reads exactly like a
/// foreign or missing one), the read-only ref pin, the read-only-context and patch-only refusals of every agent
/// pull-request write, that each refusal is answered before a Standard-tier call is parked for a human's approval, that
/// read-only context keeps its pull requests readable (the pin holds what a command checks out, nothing more), and that
/// a workflow node's call — no calling run — is unchanged. Skips on Windows / without git so a cross-host
/// <c>dotnet test</c> stays clean.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentToolRepositoryBindingFlowTests(PostgresFixture fixture)
{
    private const string BoundContent = "bound-repository-readme";
    private const string UnboundSecret = "UNBOUND-SECRET-ON-UNMERGED-BRANCH";
    private const string BoundSecret = "BOUND-REPO-UNMERGED-BRANCH-CONTENT";

    public static TheoryData<string> RepositoryReadTools => new() { "agent.run_command", "git.fetch_pr_diff", "git.fetch_pr_checks", "git.list_prs" };

    public static TheoryData<string> PullRequestWriteTools => new() { "git.open_pr", "git.merge_pr", "git.pr_review", "git.post_pr_comment" };

    [Theory]
    [MemberData(nameof(RepositoryReadTools))]
    public async Task An_unbound_repository_of_the_runs_own_team_reads_exactly_like_a_foreign_or_missing_one(string kind)
    {
        if (!await GitReadyAsync()) return;

        await using var world = await SeedWorldAsync();
        var handler = HandlerBoundTo(world, Bound(world.BoundRepositoryId, WorkspaceAccess.Write));
        var missing = Guid.NewGuid();

        var unbound = await CallToolAsync(handler, kind, ArgumentsFor(world.UnboundRepositoryId, branch: "secret-branch", command: "cat", "SECRET.txt"));
        var foreign = await CallToolAsync(handler, kind, ArgumentsFor(world.ForeignRepositoryId, branch: "secret-branch", command: "cat", "SECRET.txt"));
        var absent = await CallToolAsync(handler, kind, ArgumentsFor(missing, branch: "secret-branch", command: "cat", "SECRET.txt"));

        foreach (var result in new[] { unbound, foreign, absent }) result.GetProperty("isError").GetBoolean().ShouldBeTrue($"{kind}: {result.GetRawText()}");
        Text(unbound).ShouldBe(AgentRepositoryBinding.NotFound(world.UnboundRepositoryId), $"{kind} must refuse a repository its run is not bound to as not found, even though the team owns it");
        Text(unbound).Replace(world.UnboundRepositoryId.ToString(), "id").ShouldBe(Text(foreign).Replace(world.ForeignRepositoryId.ToString(), "id"), "same-team-unbound and foreign must be indistinguishable");
        Text(unbound).Replace(world.UnboundRepositoryId.ToString(), "id").ShouldBe(Text(absent).Replace(missing.ToString(), "id"), "same-team-unbound and missing must be indistinguishable — no existence oracle");
        unbound.GetRawText().ShouldNotContain(UnboundSecret, customMessage: "the unbound repository's content must never reach the model");
    }

    [Theory]
    [MemberData(nameof(RepositoryReadTools))]
    public async Task A_bound_repository_passes_the_binding_and_reaches_the_tool(string kind)
    {
        if (!await GitReadyAsync()) return;

        await using var world = await SeedWorldAsync();
        var handler = HandlerBoundTo(world, Bound(world.BoundRepositoryId, WorkspaceAccess.Write));

        var result = await CallToolAsync(handler, kind, ArgumentsFor(world.BoundRepositoryId, branch: null, command: "cat", "README.md"));

        // run_command reads the clone; the PR reads reach PullRequestService, which refuses this credential-less local
        // repository on its own terms — either way the call got past the binding to the repository itself.
        Text(result).ShouldNotBe(AgentRepositoryBinding.NotFound(world.BoundRepositoryId), $"{kind} must reach a repository its run is bound to");
        if (kind == "agent.run_command") Text(result).ShouldContain(BoundContent);
    }

    [Theory]
    // access                  branch            allowed
    [InlineData(WorkspaceAccess.Read, null, true)]
    [InlineData(WorkspaceAccess.Read, "main", true)]
    [InlineData(WorkspaceAccess.Read, "secret-branch", false)]
    [InlineData(WorkspaceAccess.Write, "secret-branch", true)]
    public async Task A_read_only_context_repository_checks_out_only_its_bound_or_default_branch(WorkspaceAccess access, string? branch, bool allowed)
    {
        if (!await GitReadyAsync()) return;

        await using var world = await SeedWorldAsync();
        var handler = HandlerBoundTo(world, Bound(world.BoundRepositoryId, access));
        var file = branch == "secret-branch" ? "SECRET.txt" : "README.md";

        var result = await CallToolAsync(handler, "agent.run_command", ArgumentsFor(world.BoundRepositoryId, branch, command: "cat", file));

        result.GetProperty("isError").GetBoolean().ShouldBe(!allowed, $"{access} at '{branch ?? "(default)"}': {result.GetRawText()}");
        if (allowed) Text(result).ShouldContain(branch == "secret-branch" ? BoundSecret : BoundContent);
        else
        {
            Text(result).ShouldContain("read-only context", customMessage: "the refusal tells the agent why, so it can retry on the bound branch");
            result.GetRawText().ShouldNotContain(BoundSecret, customMessage: "a branch outside the binding is never cloned");
        }
    }

    [Theory]
    [MemberData(nameof(PullRequestWriteTools))]
    public async Task A_patch_only_repository_refuses_every_agent_pull_request_write_before_the_provider(string kind)
    {
        await using var world = await SeedWorldAsync(withGit: false);
        var patchOnly = await SeedCredentialedRepositoryAsync(world.TeamId, world.OwnerUserId, RepositoryPublishMode.PatchOnly);
        var branchMode = await SeedCredentialedRepositoryAsync(world.TeamId, world.OwnerUserId, RepositoryPublishMode.Branch);
        using var scope = fixture.BeginScope();
        var tool = scope.Resolve<IAgentToolRegistry>().Resolve(kind).ShouldNotBeNull();
        var caller = Posture(Bound(patchOnly, WorkspaceAccess.Write), Bound(branchMode, WorkspaceAccess.Write));

        // The tool itself, as the MCP dispatch invokes it once the gate (and, for a merge, a human) let the call through.
        var refused = await OutcomeAsync(tool, new AgentToolCall { Input = WriteArguments(patchOnly), TeamId = world.TeamId, CallerPosture = caller });
        var passed = await OutcomeAsync(tool, new AgentToolCall { Input = WriteArguments(branchMode), TeamId = world.TeamId, CallerPosture = caller });

        refused.ShouldBe($"Repository {patchOnly} does not take pull-request writes from an agent: the repository requires patch-only publishing.");
        passed.ShouldNotContain("patch-only", customMessage: $"a branch-mode repository's write reaches the pull-request service, which refuses this local provider on its own terms: {passed}");
        passed.ShouldNotBe(AgentRepositoryBinding.NotFound(branchMode));
    }

    [Theory]
    [MemberData(nameof(PullRequestWriteTools))]
    public async Task A_read_only_context_repository_refuses_every_agent_pull_request_write_before_the_provider(string kind)
    {
        await using var world = await SeedWorldAsync(withGit: false);
        var readContext = await SeedCredentialedRepositoryAsync(world.TeamId, world.OwnerUserId, RepositoryPublishMode.Branch);
        var writable = await SeedCredentialedRepositoryAsync(world.TeamId, world.OwnerUserId, RepositoryPublishMode.Branch);
        using var scope = fixture.BeginScope();
        var tool = scope.Resolve<IAgentToolRegistry>().Resolve(kind).ShouldNotBeNull();
        var caller = Posture(Bound(writable, WorkspaceAccess.Write), Bound(readContext, WorkspaceAccess.Read));

        // Two branch-mode, credentialed repositories that differ only in how the run is bound to them. The writable one's
        // write reaches the pull-request service (which refuses this local provider on its own terms); read-only context
        // is refused on the binding's access, before its publish policy or its connection credential is ever reached.
        var readCall = new AgentToolCall { Input = WriteArguments(readContext), TeamId = world.TeamId, CallerPosture = caller };
        var onRead = await OutcomeAsync(tool, readCall);
        var onWrite = await OutcomeAsync(tool, new AgentToolCall { Input = WriteArguments(writable), TeamId = world.TeamId, CallerPosture = caller });

        onRead.ShouldBe(AgentRepositoryBinding.ReadOnlyContextWrite(readContext));
        (await tool.RefusalAsync(readCall, CancellationToken.None)).ShouldBe(onRead, "the MCP dispatch's admission check gives the same answer before it would park the call");
        onWrite.ShouldNotContain("read-only context", customMessage: $"fixture check: the writable twin's write gets past the binding: {onWrite}");
        onWrite.ShouldNotBe(AgentRepositoryBinding.NotFound(writable));
    }

    [Theory]
    [InlineData("git.fetch_pr_diff")]
    [InlineData("git.list_prs")]
    [InlineData("git.fetch_pr_checks")]
    public async Task A_read_only_context_repository_keeps_its_pull_requests_readable_the_pin_holds_only_what_a_command_checks_out(string kind)
    {
        // The documented scope of the read-only ref pin (AgentRepositoryBinding): it holds the ref a command checks out,
        // not what the provider publishes about a bound repository. A pull-request read of read-only context bound at main
        // passes the binding to the provider layer — here the local provider, which serves no pull requests.
        await using var world = await SeedWorldAsync(withGit: false);
        var readContext = await SeedCredentialedRepositoryAsync(world.TeamId, world.OwnerUserId, RepositoryPublishMode.Branch);
        var handler = HandlerBoundTo(world, Bound(readContext, WorkspaceAccess.Read) with { Ref = "main" });

        var result = await CallToolAsync(handler, kind, ArgumentsFor(readContext, branch: null, command: "true"));

        Text(result).ShouldNotBe(AgentRepositoryBinding.NotFound(readContext));
        Text(result).ShouldNotContain("read-only context");
        Text(result).ShouldContain("does not implement capability", customMessage: $"the read reached the provider layer: {Text(result)}");
    }

    [Theory]
    // tool                    binding           what the call names
    [InlineData("agent.run_command", "unbound")]
    [InlineData("git.open_pr", "unbound")]
    [InlineData("git.merge_pr", "unbound")]
    [InlineData("git.open_pr", "read-context")]
    [InlineData("agent.run_command", "read-context-off-branch")]
    [InlineData("git.post_pr_comment", "patch-only")]
    public async Task A_standard_tier_call_that_would_be_refused_is_refused_at_once_with_no_approval_parked_or_posted(string kind, string target)
    {
        // Standard is the default tier: every side-effecting tool asks a human first. A call the tool would refuse anyway
        // must not post a card, park a ledger row, or block the agent on the approval bound — it is answered at once.
        await using var world = await SeedWorldAsync(withGit: false);
        var patchOnly = await SeedCredentialedRepositoryAsync(world.TeamId, world.OwnerUserId, RepositoryPublishMode.PatchOnly);
        var channelId = await SeedChannelAsync(world.TeamId, world.OwnerUserId);
        var runId = Guid.NewGuid();
        var (named, binding, branch) = target switch
        {
            "unbound" => (world.UnboundRepositoryId, Bound(world.BoundRepositoryId, WorkspaceAccess.Write), (string?)null),
            "read-context" => (world.BoundRepositoryId, Bound(world.BoundRepositoryId, WorkspaceAccess.Read), null),
            "read-context-off-branch" => (world.BoundRepositoryId, Bound(world.BoundRepositoryId, WorkspaceAccess.Read), "secret-branch"),
            _ => (patchOnly, Bound(patchOnly, WorkspaceAccess.Write), null),
        };

        var previous = Environment.GetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar);
        Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, "5");   // a regression parks and times out in seconds, never the 10-minute default

        try
        {
            using var scope = fixture.BeginScope();
            var handler = new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Standard, world.TeamId, null, runId,
                scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true, approvalConversationId: channelId,
                scope.Resolve<IChatBotService>(), scope.Resolve<IToolApprovalWaiterRegistry>(), scope.Resolve<IInteractionComponentRegistry>(), repositories: [binding]);
            var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["repositoryId"] = named.ToString(), ["command"] = "true", ["branch"] = branch, ["number"] = 7, ["title"] = "t",
                ["sourceBranch"] = "feature", ["targetBranch"] = "main", ["body"] = "b",
            }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value));

            var result = await CallToolAsync(handler, kind, arguments);

            result.GetProperty("isError").GetBoolean().ShouldBeTrue();
            Text(result).ShouldBe(target switch
            {
                "unbound" => AgentRepositoryBinding.NotFound(named),
                "read-context" => AgentRepositoryBinding.ReadOnlyContextWrite(named),
                "read-context-off-branch" => AgentRepositoryBinding.RefOutsideBinding(named, binding, branch, "main"),
                _ => $"Repository {named} does not take pull-request writes from an agent: the repository requires patch-only publishing.",
            });
            (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, world.TeamId, CancellationToken.None)).ShouldBeEmpty("no ledger row is parked for a call that could only be refused");
            (await CardCountAsync(world.TeamId, channelId)).ShouldBe(0, "no human is asked to approve a call that could only be refused");
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpRequestHandler.ApprovalBoundSecondsEnvVar, previous);
        }
    }

    [Fact]
    public async Task A_patch_only_refusal_travels_the_real_mcp_dispatch_and_a_read_of_the_same_repository_does_not_meet_it()
    {
        await using var world = await SeedWorldAsync(withGit: false);
        var patchOnly = await SeedCredentialedRepositoryAsync(world.TeamId, world.OwnerUserId, RepositoryPublishMode.PatchOnly);
        var handler = HandlerBoundTo(world, Bound(patchOnly, WorkspaceAccess.Write));

        // Unleashed: a reversible write is gate-Allowed, so only the repository's policy stands between it and the provider.
        var comment = await CallToolAsync(handler, "git.post_pr_comment", WriteArguments(patchOnly));
        var read = await CallToolAsync(handler, "git.list_prs", ArgumentsFor(patchOnly, branch: null, command: "true"));

        comment.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(comment).ShouldContain("patch-only");
        Text(read).ShouldNotContain("patch-only", customMessage: "the publish policy governs writes; reading a bound patch-only repository is untouched");
    }

    [Fact]
    public async Task A_workflow_nodes_call_with_no_calling_run_reaches_any_repository_of_its_team_as_before()
    {
        if (!await GitReadyAsync()) return;

        await using var world = await SeedWorldAsync();
        using var scope = fixture.BeginScope();
        var tool = scope.Resolve<IAgentToolRegistry>().Resolve("agent.run_command").ShouldNotBeNull();

        // No CallerPosture: an authored workflow step's repository is the author's choice within the team, unchanged.
        var result = await tool.CallAsync(new AgentToolCall { Input = ArgumentsFor(world.UnboundRepositoryId, "secret-branch", "cat", "SECRET.txt"), TeamId = world.TeamId }, CancellationToken.None);

        result.IsError.ShouldBeFalse(result.Error);
        result.Output.GetRawText().ShouldContain(UnboundSecret, customMessage: "fixture check: the unbound repository really holds the secret the agent path must never print");
    }

    [Fact]
    public async Task RunCommandService_holds_an_agent_caller_to_its_binding_even_when_called_directly()
    {
        if (!await GitReadyAsync()) return;

        await using var world = await SeedWorldAsync();
        using var scope = fixture.BeginScope();
        var service = scope.Resolve<IRunCommandService>();
        // The foreign repository is BOUND here, as no admitted run could be, so its call gets past the binding and the
        // service's own tenant filter is what refuses it — the miss the binding's "not found" claims to be byte-identical to.
        var caller = Posture(Bound(world.BoundRepositoryId, WorkspaceAccess.Read), Bound(world.ForeignRepositoryId, WorkspaceAccess.Write));

        var unbound = await Should.ThrowAsync<WorkspaceException>(() => service.RunAsync(new RunCommandRequest { RepositoryId = world.UnboundRepositoryId, TeamId = world.TeamId, Ref = "secret-branch", Command = "cat", Args = ["SECRET.txt"], CallerPosture = caller }, CancellationToken.None));
        // README.md and a short timeout: were the tenant filter to fail open, the command must end in seconds (a bare
        // `cat` would wait on stdin for the default ten minutes) and the assertion below names the regression.
        var foreign = await Should.ThrowAsync<WorkspaceException>(() => service.RunAsync(new RunCommandRequest { RepositoryId = world.ForeignRepositoryId, TeamId = world.TeamId, Command = "cat", Args = ["README.md"], TimeoutSeconds = 30, CallerPosture = caller }, CancellationToken.None));
        var offBranch = await Should.ThrowAsync<WorkspaceException>(() => service.RunAsync(new RunCommandRequest { RepositoryId = world.BoundRepositoryId, TeamId = world.TeamId, Ref = "secret-branch", Command = "cat", Args = ["SECRET.txt"], CallerPosture = caller }, CancellationToken.None));

        unbound.Message.ShouldBe(AgentRepositoryBinding.NotFound(world.UnboundRepositoryId));
        foreign.Message.ShouldBe(AgentRepositoryBinding.NotFound(world.ForeignRepositoryId), "the tenant filter's own miss is byte-identical to the binding's");
        offBranch.Message.ShouldContain("read-only context");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private McpRequestHandler HandlerBoundTo(World world, params WorkspaceRepositorySpec[] repositories)
    {
        var scope = fixture.BeginScope();
        world.Own(scope);

        return new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Unleashed, world.TeamId, runId: Guid.NewGuid(), repositories: repositories);
    }

    private async Task<Guid> SeedChannelAsync(Guid teamId, Guid ownerUserId)
    {
        using var scope = fixture.BeginScope();
        var slug = "bind-" + Guid.NewGuid().ToString("N")[..8];

        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerUserId, CancellationToken.None);
    }

    /// <summary>The interactive cards (an approval card is one) posted into <paramref name="channelId"/>.</summary>
    private async Task<int> CardCountAsync(Guid teamId, Guid channelId)
    {
        using var scope = fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Message.AsNoTracking().CountAsync(m => m.ConversationId == channelId && m.TeamId == teamId && m.InteractionJson != null && m.DeletedDate == null);
    }

    private static AgentRunPosture Posture(params WorkspaceRepositorySpec[] repositories) =>
        new() { RunId = Guid.NewGuid(), Autonomy = AgentAutonomyLevel.Unleashed, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Unleashed), Repositories = repositories };

    private static WorkspaceRepositorySpec Bound(Guid repositoryId, WorkspaceAccess access) => new() { Alias = repositoryId.ToString("N"), RepositoryId = repositoryId, Access = access };

    /// <summary>One argument bag every repository tool accepts: each node reads its own keys and ignores the rest, so a theory over tools needs no per-tool shape.</summary>
    private static JsonElement ArgumentsFor(Guid repositoryId, string? branch, string command, params string[] args) => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
    {
        ["repositoryId"] = repositoryId.ToString(),
        ["number"] = 1,
        ["state"] = "open",
        ["command"] = command,
        ["args"] = args,
        ["branch"] = branch,
    }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>The required inputs of every pull-request write at once (open / merge / review / comment).</summary>
    private static JsonElement WriteArguments(Guid repositoryId) => JsonSerializer.SerializeToElement(new
    {
        repositoryId = repositoryId.ToString(), number = 7, title = "t", sourceBranch = "feature", targetBranch = "main", body = "b", verdict = "comment",
    });

    /// <summary>What a write came back with — its error, or the message of what it threw past the node (the MCP dispatch maps a throw to the same tool error).</summary>
    private static async Task<string> OutcomeAsync(IAgentTool tool, AgentToolCall call)
    {
        try
        {
            var result = await tool.CallAsync(call, CancellationToken.None);
            return result.IsError ? result.Error ?? "" : "ok";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private static async Task<JsonElement> CallToolAsync(McpRequestHandler handler, string name, JsonElement arguments)
    {
        var request = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });

        return (await handler.HandleAsync(request, CancellationToken.None))!.Value.GetProperty("result");
    }

    private static string Text(JsonElement toolResult) => toolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;

        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = ["--version"], TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>Team A with the run's repository and a second, unbound one; team C with a foreign one. Each git-backed repository has a README on main and an unmerged <c>secret-branch</c>.</summary>
    private async Task<World> SeedWorldAsync(bool withGit = true)
    {
        var (teamId, ownerUserId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var world = new World(teamId, ownerUserId);

        world.BoundRepositoryId = await SeedLocalRepositoryAsync(world, teamId, withGit, BoundContent, BoundSecret);
        world.UnboundRepositoryId = await SeedLocalRepositoryAsync(world, teamId, withGit, "unbound-readme", UnboundSecret);
        world.ForeignRepositoryId = await SeedLocalRepositoryAsync(world, foreignTeamId, withGit, "foreign-readme", "FOREIGN-SECRET");

        return world;
    }

    private async Task<Guid> SeedLocalRepositoryAsync(World world, Guid teamId, bool withGit, string readme, string secret)
    {
        var origin = world.TempDir();
        if (withGit) await SeedOriginAsync(origin, readme, secret);

        return await SeedRepositoryRowAsync(teamId, new Uri(origin).AbsoluteUri, credentialId: null, RepositoryPublishMode.Branch);
    }

    private async Task<Guid> SeedCredentialedRepositoryAsync(Guid teamId, Guid ownerUserId, RepositoryPublishMode mode)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instanceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        // A local (Git) provider: nothing behind it serves pull requests, so a write that gets past the policy fails in
        // the service with no outbound call — the test stays hermetic whichever way it goes.
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = $"https://git-{instanceId:N}.example.invalid" });
        db.Credential.Add(new Credential { Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, OwnerUserId = ownerUserId, AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = "not-a-real-ciphertext", Status = CredentialStatus.Active });
        await db.SaveChangesAsync();

        return await SeedRepositoryRowAsync(teamId, "https://git.example.invalid/acme/compliance.git", credentialId, mode, instanceId);
    }

    private async Task<Guid> SeedRepositoryRowAsync(Guid teamId, string cloneUrl, Guid? credentialId, RepositoryPublishMode mode, Guid? providerInstanceId = null)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instanceId = providerInstanceId ?? Guid.NewGuid();
        if (providerInstanceId is null) db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = $"https://local-{instanceId:N}" });

        var repositoryId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repositoryId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId, PublishMode = mode,
            ExternalId = repositoryId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
        });
        await db.SaveChangesAsync();

        return repositoryId;
    }

    private static async Task SeedOriginAsync(string dir, string readme, string secret)
    {
        await GitAsync(dir, "init", "-b", "main");
        await GitAsync(dir, "config", "user.email", "test@codespace.dev");
        await GitAsync(dir, "config", "user.name", "Test");
        await GitAsync(dir, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), readme);
        await GitAsync(dir, "add", ".");
        await GitAsync(dir, "commit", "-m", "seed");
        await GitAsync(dir, "checkout", "-b", "secret-branch");
        await File.WriteAllTextAsync(Path.Combine(dir, "SECRET.txt"), secret);
        await GitAsync(dir, "add", ".");
        await GitAsync(dir, "commit", "-m", "unmerged work");
        await GitAsync(dir, "checkout", "main");
    }

    private static async Task GitAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
        if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    /// <summary>The seeded world, plus every temp origin and DI scope a test opened — all released on dispose, even when an assertion fails.</summary>
    private sealed class World(Guid teamId, Guid ownerUserId) : IAsyncDisposable
    {
        private readonly List<string> _dirs = new();
        private readonly List<ILifetimeScope> _scopes = new();

        public Guid TeamId { get; } = teamId;
        public Guid OwnerUserId { get; } = ownerUserId;
        public Guid BoundRepositoryId { get; set; }
        public Guid UnboundRepositoryId { get; set; }
        public Guid ForeignRepositoryId { get; set; }

        public string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cs-bind-origin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _dirs.Add(dir);
            return dir;
        }

        public void Own(ILifetimeScope scope) => _scopes.Add(scope);

        public async ValueTask DisposeAsync()
        {
            foreach (var scope in _scopes) await scope.DisposeAsync();
            foreach (var dir in _dirs)
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
