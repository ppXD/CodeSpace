using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class StopHookExecutionEvidenceFlowTests
{
    private readonly PostgresFixture _fixture;
    public StopHookExecutionEvidenceFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_real_anonymous_admission_refusal_cannot_be_swallowed_into_a_stop_hook_pass()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        using var evidence = new StopHookExecutionEvidence("claude", directory: "");
        var executorCalls = 0;
        AgentAuthorityDeniedException? denial = null;

        await Assert.ThrowsAsync<ShouldAssertException>(() => evidence.AssessAsync(async () =>
        {
            using var scope = _fixture.BeginScope();
            try
            {
                var run = await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Harness = "claude-code", Goal = "stop-hook admission regression" }, teamId, null, null, "", CancellationToken.None);
                evidence.Admitted(run.Id);
                executorCalls++;
                await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(run.Id, CancellationToken.None);
                return (true, "unreachable after admission denial");
            }
            catch (AgentAuthorityDeniedException exception)
            {
                denial = exception;
                throw;
            }
        }));

        denial.ShouldNotBeNull();
        denial.Reason.ShouldBe("actor-unverifiable");
        executorCalls.ShouldBe(0);
        evidence.Record.QualificationSucceeded.ShouldBeFalse();
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AnyAsync(r => r.TeamId == teamId)).ShouldBeFalse();
    }
}
