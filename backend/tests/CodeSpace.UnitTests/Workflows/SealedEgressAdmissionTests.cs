using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the local runner's refusal of a network-off brokered run it would confine but cannot seal — the admission the
/// executor asks for before anything is spent. The executor asking at all, and landing the refusal typed with no spend
/// and no process, is pinned one tier up in <c>AgentRunExecutorTests</c>; the kernel refusing a real setup is pinned in
/// the sandbox lane.
/// </summary>
[Trait("Category", "Unit")]
public class SealedEgressAdmissionTests
{
    [Theory]
    [InlineData(false, false, false, SealedEgressUnavailableException.CauseMissingTools)]
    [InlineData(true, false, true, SealedEgressUnavailableException.CauseNoPrivilege)]
    [InlineData(true, true, false, SealedEgressUnavailableException.CauseBrokerLoopbackOnly)]
    [InlineData(true, true, true, null)]
    public void Each_wall_a_confining_host_can_hit_is_named(bool haveTools, bool canSeal, bool brokerReachable, string? expected) =>
        LocalProcessRunner.SealRefusal(haveTools, canSeal, brokerReachable).ShouldBe(expected);

    [Fact]
    public void A_spec_with_no_broker_port_is_admitted_on_any_host() =>
        // Nothing to seal: every unbrokered or network-on run launches exactly as it always did, even on a host that could not seal.
        Should.NotThrow(() => new LocalProcessRunner().EnsureEgressAdmissible(new SandboxSpec { Command = "agent" }, modelBrokerReachableFromNamespace: false));

    [Fact]
    public void A_brokered_network_off_spec_is_refused_exactly_where_this_host_confines_but_cannot_seal()
    {
        // Honest on either host: unconfined hosts (macOS dev, a pod without userns) admit it untouched, because nothing
        // there would have severed it; a confining host refuses it unless it can seal and the broker is reachable.
        var spec = new SandboxSpec { Command = "agent", ModelBrokerPort = 43121 };
        var refusal = BubblewrapSandbox.Available is null ? null : LocalProcessRunner.SealRefusal(FilteredEgressNetns.IsSupported, FilteredEgressNetns.CanSeal, brokerReachableFromNamespace: false);

        var thrown = Record.Exception(() => new LocalProcessRunner().EnsureEgressAdmissible(spec, modelBrokerReachableFromNamespace: false));

        if (refusal is null) thrown.ShouldBeNull();
        else thrown.ShouldBeOfType<SealedEgressUnavailableException>().Cause.ShouldStartWith(refusal, customMessage: "the cause leads with the wall, then the probe's own account of it");
    }

    [Fact]
    public void The_refusal_is_an_unavailable_failure_that_names_its_cause_and_its_remedy()
    {
        IFailure failure = new SealedEgressUnavailableException(SealedEgressUnavailableException.CauseNoPrivilege);

        failure.Kind.ShouldBe(FailureKind.Unavailable, "nothing about the launch can change to make it work — only the host can");
        failure.Code.ShouldBe(FailureCodes.SandboxSealedEgressUnavailable);
        failure.ClientMessage.ShouldBe("This host cannot give a network-off run a sealed route to its model.");
        ((Exception)failure).Message.ShouldContain(SealedEgressUnavailableException.CauseNoPrivilege, customMessage: "the operator must be told which wall it was");
        ((Exception)failure).Message.ShouldContain("a retry on this host helps only once it can build one", customMessage: "a probe failure can be transient, so the remedy must not promise a retry is hopeless");
    }

    [Fact]
    public void A_setup_step_that_failed_on_a_host_that_can_seal_is_told_to_fix_that_step()
    {
        // The probe proved this worker can build a namespace, so granting it more and waiting for a re-probe fixes
        // nothing: the failed step (a route that discards the run's /30, say) fails every launch until it is fixed.
        var refusal = SealedEgressUnavailableException.SetupFailed("ip route get 10.1.1.2 from 10.1.1.1 → exit 2: RTNETLINK answers: No route to host");

        ((IFailure)refusal).Code.ShouldBe(FailureCodes.SandboxSealedEgressUnavailable, "the same wall as every other refusal, so the supervisor steers it the same way");
        refusal.Cause.ShouldBe("the sealed namespace's setup failed: ip route get 10.1.1.2 from 10.1.1.1 → exit 2: RTNETLINK answers: No route to host");
        refusal.Message.ShouldContain("fix what that step names", customMessage: "the remedy points at the failed step");
        refusal.Message.ShouldNotContain("Dockerfile.worker", customMessage: "the worker already has what a sealed namespace needs");
        refusal.Message.ShouldNotContain("once a minute", customMessage: "the setup runs afresh on every launch; no probe is waited for");
    }
}
