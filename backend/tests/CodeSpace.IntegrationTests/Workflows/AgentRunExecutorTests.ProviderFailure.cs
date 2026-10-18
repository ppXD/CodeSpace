using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

public partial class AgentRunExecutorTests
{
    // 🟢 High fidelity — the real executor over real Postgres. A repository whose credential was disconnected cannot be cloned; the run
    // fails, and what it says about WHY is now a code and what it names, not only the sentence.
    [Fact]
    public async Task A_run_whose_clone_met_a_disconnected_credential_records_the_failure_with_the_repository_beside_the_sentence()
    {
        var teamId = await SeedTeamAsync();
        var repositoryId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var runId = await CreateTaskRunAsync(teamId, new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 60, RepositoryId = repositoryId });

        using (var scope = _fixture.BeginScope(b => b.RegisterInstance(new DisconnectedCredentialResolver(credentialId)).As<IAgentWorkspaceResolver>()))
            await NewExecutor(scope, new ScriptedHarness("true")).ExecuteAsync(runId, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var run = await read.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        var result = System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

        run.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe(FailureCodes.CredentialDisconnected, "the exit reason already carried the code");
        result.Failure.ShouldNotBeNull();
        result.Failure.Code.ShouldBe(FailureCodes.CredentialDisconnected);
        result.Failure.DetailGuid("credentialId").ShouldBe(credentialId, "and now what it names too");
        result.Failure.RepositoryId.ShouldBe(repositoryId, "the repository the run was working in");
        result.Error.ShouldContain("was disconnected", customMessage: "the sentence is untouched");
    }

    [Fact]
    public async Task A_run_that_failed_for_another_reason_records_no_failure_so_its_result_reads_as_it_always_did()
    {
        var teamId = await SeedTeamAsync();
        var runId = await CreateTaskRunAsync(teamId, new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 60, RepositoryId = Guid.NewGuid() });

        using (var scope = _fixture.BeginScope(b => b.RegisterInstance(new ThrowingResolver(new InvalidOperationException("the clone broke"))).As<IAgentWorkspaceResolver>()))
            await NewExecutor(scope, new ScriptedHarness("true")).ExecuteAsync(runId, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var run = await read.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.ResultJson!.ShouldNotContain("\"failure\"", Case.Insensitive, "an absent failure is omitted, never written as null");
        System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!.Failure.ShouldBeNull();
    }

    private sealed class DisconnectedCredentialResolver : IAgentWorkspaceResolver
    {
        private readonly Guid _credentialId;

        public DisconnectedCredentialResolver(Guid credentialId) { _credentialId = credentialId; }

        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw new CredentialDisconnectedException(_credentialId, "GitLab · nacho");

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null) => throw new CredentialDisconnectedException(_credentialId, "GitLab · nacho");
    }

    private sealed class ThrowingResolver : IAgentWorkspaceResolver
    {
        private readonly Exception _failure;

        public ThrowingResolver(Exception failure) { _failure = failure; }

        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw _failure;

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null) => throw _failure;
    }
}
