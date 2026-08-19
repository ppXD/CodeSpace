using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// How a CAS verdict becomes an Agent Run log verdict, and which of those a caller may retry. Both halves are
/// load-bearing: the code is what an operator reads off the terminalized stream, and the transience decides whether
/// the capture loop spends its whole finalization budget on a fault that will never clear.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunLogStorageProblemMappingTests
{
    [Theory]
    [InlineData(ArtifactCasProblemCode.ProfileMissing)]
    [InlineData(ArtifactCasProblemCode.ProfileNotActive)]
    [InlineData(ArtifactCasProblemCode.ProfileRevisionMissing)]
    [InlineData(ArtifactCasProblemCode.ProfileInvalid)]
    [InlineData(ArtifactCasProblemCode.CredentialUnavailable)]
    [InlineData(ArtifactCasProblemCode.CredentialInvalid)]
    [InlineData(ArtifactCasProblemCode.CredentialBrokerUnavailable)]
    public void A_storage_activation_fault_the_cas_layer_calls_permanent_is_a_permanent_storage_problem(ArtifactCasProblemCode code)
    {
        // Every one of these says the team's configured destination could not be activated. Reporting them as a
        // capture backend outage sends the operator to the agent's log source for a storage misconfiguration.
        var problem = AgentRunLogService.Map(new ArtifactCasProblem(code, IsRetryable: false));

        problem.Code.ShouldBe(AgentRunLogProblemCode.StorageActivationFailed);
        problem.IsRetryable.ShouldBeFalse();
        problem.IsTransient.ShouldBeFalse("retrying a permanent storage fault only spends the caller's budget before the same refusal");
    }

    [Fact]
    public void A_storage_activation_fault_the_cas_layer_calls_retryable_stays_retryable()
    {
        // The CAS layer marks a temporarily unreachable credential broker retryable. Hard-coding permanence for the
        // whole family would replace one dishonest classification with another.
        var problem = AgentRunLogService.Map(new ArtifactCasProblem(ArtifactCasProblemCode.CredentialBrokerUnavailable, IsRetryable: true));

        problem.Code.ShouldBe(AgentRunLogProblemCode.StorageActivationFailed);
        problem.IsTransient.ShouldBeTrue();
    }

    [Theory]
    [InlineData(AgentRunLogProblemCode.BackendUnavailable, false, false)]
    [InlineData(AgentRunLogProblemCode.BackendUnavailable, true, true)]
    [InlineData(AgentRunLogProblemCode.StorageActivationFailed, false, false)]
    [InlineData(AgentRunLogProblemCode.ProviderTimeout, false, true)]
    [InlineData(AgentRunLogProblemCode.ConcurrentMutation, false, true)]
    [InlineData(AgentRunLogProblemCode.ArtifactCorrupt, false, false)]
    public void Transience_follows_the_retryable_flag_except_for_the_two_race_codes(AgentRunLogProblemCode code, bool isRetryable, bool expectedTransient)
    {
        // BackendUnavailable used to be transient by code alone, which discarded the flag the mapper had just set from
        // the storage layer's own verdict. ProviderTimeout and ConcurrentMutation stay code-transient — redundantly
        // today, since every production construction of them passes the flag — because they name a deadline or a lost
        // race, where a caller that omits the flag must not terminalize a stream the next attempt would commit.
        new AgentRunLogProblem(code, isRetryable).IsTransient.ShouldBe(expectedTransient);
    }
}
