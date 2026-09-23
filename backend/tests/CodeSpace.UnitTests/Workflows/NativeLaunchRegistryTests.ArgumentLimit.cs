using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The launch path's own half of <see cref="SandboxArgumentLimit"/>: a spec the kernel would refuse is refused HERE,
/// before a spool exists and before a start commitment is consumed. The pure byte accounting is pinned separately by
/// <c>SandboxArgumentLimitTests</c>; what these assert is that the check actually runs on the production entry point
/// and that it runs EARLY — a refusal that first created a spool and started a broker would leave a half-launched
/// slot behind and burn the one commitment that slot can ever make.
/// </summary>
public sealed partial class NativeLaunchRegistryTests
{
    private static SandboxLaunchRequest OversizedRequest(string key) =>
        new(new SandboxSpec { Command = "/bin/echo", Args = ["--goal", new string('x', SandboxArgumentLimit.MaxStringBytes + 1)] }, key);

    [Fact]
    public async Task A_launch_whose_argument_the_kernel_would_refuse_is_refused_before_a_spool_exists()
    {
        var key = "argv-limit-" + Guid.NewGuid().ToString("N");

        var refusal = await Should.ThrowAsync<SandboxArgumentTooLongException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(OversizedRequest(key), CancellationToken.None));

        ((IFailure)refusal).Code.ShouldBe(FailureCodes.SandboxArgumentTooLong);
        Directory.Exists(LocalProcessRunner.SpoolDirectoryFor(key)).ShouldBeFalse(
            customMessage: "the refusal must be preflight: a spool created and then abandoned is a slot whose single start commitment can never be re-made");
    }

    [Fact]
    public async Task The_refusal_names_the_size_the_limit_and_that_it_is_not_a_memory_problem()
    {
        var oversized = SandboxArgumentLimit.MaxStringBytes + 1;

        var refusal = await Should.ThrowAsync<SandboxArgumentTooLongException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(OversizedRequest("argv-limit-" + Guid.NewGuid().ToString("N")), CancellationToken.None));

        refusal.Message.ShouldContain(oversized.ToString());
        refusal.Message.ShouldContain(SandboxArgumentLimit.MaxStringBytes.ToString());
        refusal.Message.ShouldContain("not a memory limit", Case.Insensitive,
            customMessage: "this replaces a message that said 'likely an external/OOM kill', which cost a full investigation");
    }

    [Fact]
    public async Task The_refusal_is_unprocessable()
    {
        // A launch refused for its argument size fails identically on every attempt. This pins the KIND only — that
        // the node then declines to retry it is a separate claim, asserted against the real node mapping in
        // AgentCodeNodeTests, because Kind alone is Unprocessable for every native-launch refusal and would pass
        // here with the feature removed. Note the coupling AgentCodeNode makes: the verdict is only reached while
        // no escalation is available, so a change there changes what this failure costs.
        var refusal = await Should.ThrowAsync<SandboxArgumentTooLongException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(OversizedRequest("argv-limit-" + Guid.NewGuid().ToString("N")), CancellationToken.None));

        ((IFailure)refusal).Kind.ShouldBe(FailureKind.Unprocessable);
    }

    [Fact]
    public async Task A_standard_input_the_launch_pipe_cannot_carry_is_refused_before_a_spool_exists()
    {
        // The prompt now rides stdin, which the kernel does not cap — but it still crosses the private broker pipe inside
        // the invocation frame, and that frame is bounded. Past the bound the write used to fail AFTER transmission was
        // marked started, which skips the netns/cgroup teardown on purpose (an ACK may have been lost) and surfaces as a
        // generic executor error the node retries. Refusing here keeps it the same early, attributable, terminal
        // refusal an oversized argument gets.
        var key = "stdin-limit-" + Guid.NewGuid().ToString("N");
        var request = new SandboxLaunchRequest(new SandboxSpec { Command = "/bin/cat", StandardInput = new string('x', NativeLaunchProtocol.MaximumFrameBytes / 2 + 1) }, key);

        var refusal = await Should.ThrowAsync<SandboxArgumentTooLongException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(request, CancellationToken.None));

        ((IFailure)refusal).Code.ShouldBe(FailureCodes.SandboxArgumentTooLong);
        refusal.Message.ShouldContain("standard input", Case.Insensitive);
        refusal.Message.ShouldNotContain("xxxx", Case.Sensitive, "a refusal is host metadata — never the prompt itself");
        Directory.Exists(LocalProcessRunner.SpoolDirectoryFor(key)).ShouldBeFalse("refused before anything is created on disk or any commitment is consumed");
    }

    [Fact]
    public async Task An_oversized_environment_value_is_refused_without_putting_it_in_the_message()
    {
        var key = "argv-limit-" + Guid.NewGuid().ToString("N");
        var secret = "sk-ant-" + new string('x', SandboxArgumentLimit.MaxStringBytes);
        var request = new SandboxLaunchRequest(new SandboxSpec { Command = "/bin/echo", Environment = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = secret } }, key);

        var refusal = await Should.ThrowAsync<SandboxArgumentTooLongException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(request, CancellationToken.None));

        refusal.Message.ShouldContain("ANTHROPIC_API_KEY");
        refusal.Message.ShouldNotContain(secret);
        refusal.Message.ShouldNotContain("sk-ant-", Case.Sensitive);
    }
}
