using System.Text.Json;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <c>git.integrate</c> — drives the real node against a fake <see cref="IBranchIntegrator"/> + a fake
/// <see cref="IAgentWorkspaceResolver"/>. Pins: the manifest is side-effecting (so a re-run routes the D7-3 approval
/// gate, like git.open_pr); a CONFLICTED integration is a routable SUCCESS outcome (status="Conflicted", NOT a node
/// failure — the workflow branches on it); a clean integration outputs the branch; a genuine infrastructure failure
/// (the integrator throwing) IS a node failure; contributions parse + thread through with the request's team + base.
/// </summary>
[Trait("Category", "Unit")]
public class GitIntegrateNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";
    private const string Team = "22222222-2222-2222-2222-222222222222";
    private const string RunId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void Manifest_is_side_effecting_so_a_rerun_is_approval_gated()
    {
        // Mirrors git.open_pr: a clean integration pushes a branch (a permanent side effect), so the engine refuses
        // auto-resume / gates a re-run through the D7-3 side-effect approval card.
        new GitIntegrateNode(new FakeIntegrator(), new FakeResolver()).Manifest.IsSideEffecting.ShouldBeTrue();
    }

    [Fact]
    public async Task A_clean_integration_outputs_the_branch_and_applied_count()
    {
        var integrator = new FakeIntegrator
        {
            Result = IntegrationResult.Build(IntegrationStatus.Clean, "codespace/integration/r", new[]
            {
                new ContributionOutcome { Label = "a", Disposition = ContributionDisposition.Applied },
                new ContributionOutcome { Label = "b", Disposition = ContributionDisposition.Applied },
            }),
        };

        var result = await new GitIntegrateNode(integrator, new FakeResolver()).RunAsync(Context(TwoContributions()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["status"].GetString().ShouldBe("Clean");
        result.Outputs["integratedBranch"].GetString().ShouldBe("codespace/integration/r");
        result.Outputs["appliedCount"].GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_conflict_is_a_routable_success_outcome_not_a_node_failure()
    {
        // CROWN JEWEL — a conflict is a normal, branchable result (the workflow routes on status), NEVER a thrown
        // node failure that would look like a crash. The conflict detail is surfaced for human review.
        var integrator = new FakeIntegrator
        {
            Result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[]
            {
                new ContributionOutcome { Label = "a", Disposition = ContributionDisposition.Applied },
                new ContributionOutcome { Label = "b", Disposition = ContributionDisposition.Conflicted, ConflictedFiles = new[] { "f.txt" }, FallbackBranch = "codespace/agent/b", Reason = "textual conflict" },
            }, "a contribution conflicted while integrating"),
        };

        var result = await new GitIntegrateNode(integrator, new FakeResolver()).RunAsync(Context(TwoContributions()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "a conflict is a valid outcome the workflow branches on, not a node crash");
        result.Outputs["status"].GetString().ShouldBe("Conflicted");
        result.Outputs["integratedBranch"].ValueKind.ShouldBe(JsonValueKind.Null);

        var conflicts = result.Outputs["conflicts"];
        conflicts.GetArrayLength().ShouldBe(1, "only the non-applied contribution is reported");
        conflicts[0].GetProperty("label").GetString().ShouldBe("b");
        conflicts[0].GetProperty("conflictedFiles")[0].GetString().ShouldBe("f.txt");
    }

    [Fact]
    public async Task An_infrastructure_failure_is_a_node_failure()
    {
        var integrator = new FakeIntegrator { Throw = new WorkspaceException("git push failed: *** rejected") };

        var result = await new GitIntegrateNode(integrator, new FakeResolver()).RunAsync(Context(TwoContributions()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure, "a genuine infra failure (auth/network) is a node failure routed to the error handle");
        result.Error.ShouldContain("integration failed");
    }

    [Fact]
    public async Task Threads_the_team_base_and_parsed_contributions_into_the_request()
    {
        var integrator = new FakeIntegrator { Result = Empty() };

        await new GitIntegrateNode(integrator, new FakeResolver()).RunAsync(Context(TwoContributions()), CancellationToken.None);

        integrator.LastRequest!.TeamId.ShouldBe(Guid.Parse(Team), "the run's team flows into the request (the tenancy boundary)");
        integrator.LastRequest.BaseSha.ShouldBe("base-sha-123");
        integrator.LastRequest.IntegrationBranch.ShouldBe($"codespace/integration/{RunId}", "the integration branch is run-id-derived so a re-run never forks");
        integrator.LastRequest.Contributions.Count.ShouldBe(2);
        integrator.LastRequest.Contributions[0].Label.ShouldBe("agent-a");
        integrator.LastRequest.Contributions[0].Patch.ShouldContain("diff --git");
    }

    [Fact]
    public async Task Fails_closed_without_a_team()
    {
        var integrator = new FakeIntegrator();

        var result = await new GitIntegrateNode(integrator, new FakeResolver()).RunAsync(
            ContextWithSys(TwoContributions(), new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("team context");
        integrator.Calls.ShouldBe(0, "without a team the node short-circuits before any git side effect");
    }

    [Fact]
    public async Task Fails_when_the_repository_cannot_be_resolved()
    {
        var integrator = new FakeIntegrator();

        var result = await new GitIntegrateNode(integrator, new FakeResolver { ReturnNull = true }).RunAsync(Context(TwoContributions()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("could not be resolved");
        integrator.Calls.ShouldBe(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static IntegrationResult Empty() => IntegrationResult.Build(IntegrationStatus.Empty, null, Array.Empty<ContributionOutcome>());

    private static Dictionary<string, JsonElement> TwoContributions() => new()
    {
        ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        ["baseSha"] = JsonSerializer.SerializeToElement("base-sha-123"),
        ["contributions"] = JsonSerializer.SerializeToElement(new[]
        {
            new { label = "agent-a", baseSha = "base-sha-123", patch = "diff --git a/a.txt b/a.txt\n" },
            new { label = "agent-b", baseSha = "base-sha-123", patch = "diff --git a/b.txt b/b.txt\n" },
        }),
    };

    private static NodeRunContext Context(Dictionary<string, JsonElement> inputs) =>
        ContextWithSys(inputs, new()
        {
            [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(Team),
            [SystemScopeKeys.WorkflowRunId] = JsonSerializer.SerializeToElement(RunId),
        });

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

    private sealed class FakeIntegrator : IBranchIntegrator
    {
        public IntegrationResult? Result;
        public Exception? Throw;
        public int Calls;
        public IntegrationRequest? LastRequest;

        public string Kind => "local";

        public Task<IntegrationResult> IntegrateAsync(IntegrationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result ?? IntegrationResult.Build(IntegrationStatus.Empty, null, Array.Empty<ContributionOutcome>()));
        }
    }

    private sealed class FakeResolver : IAgentWorkspaceResolver
    {
        public bool ReturnNull;

        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null) =>
            Task.FromResult(ReturnNull ? null : new WorkspaceRequest { RepositoryUrl = "file:///remote.git", Ref = @ref ?? "main", Token = "tok", TokenUsername = "x-access-token" });
    }
}
