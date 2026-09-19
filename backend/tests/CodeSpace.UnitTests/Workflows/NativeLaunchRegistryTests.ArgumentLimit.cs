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

        var refusal = await Should.ThrowAsync<NativeLaunchException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(OversizedRequest(key), CancellationToken.None));

        refusal.Reason.ShouldBe("argument-too-long");
        Directory.Exists(LocalProcessRunner.SpoolDirectoryFor(key)).ShouldBeFalse(
            customMessage: "the refusal must be preflight: a spool created and then abandoned is a slot whose single start commitment can never be re-made");
    }

    [Fact]
    public async Task The_refusal_names_the_size_the_limit_and_that_it_is_not_a_memory_problem()
    {
        var oversized = SandboxArgumentLimit.MaxStringBytes + 1;

        var refusal = await Should.ThrowAsync<NativeLaunchException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(OversizedRequest("argv-limit-" + Guid.NewGuid().ToString("N")), CancellationToken.None));

        refusal.Message.ShouldContain(oversized.ToString());
        refusal.Message.ShouldContain(SandboxArgumentLimit.MaxStringBytes.ToString());
        refusal.Message.ShouldContain("not a memory limit", Case.Insensitive,
            customMessage: "this replaces a message that said 'likely an external/OOM kill', which cost a full investigation");
    }

    [Fact]
    public async Task The_refusal_is_unprocessable_so_the_launch_is_never_retried()
    {
        // A launch refused for its argument size fails identically on every attempt. Classifying it retryable is how
        // the pre-guard failure burned a budget on a run that could not start.
        var refusal = await Should.ThrowAsync<NativeLaunchException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(OversizedRequest("argv-limit-" + Guid.NewGuid().ToString("N")), CancellationToken.None));

        ((IFailure)refusal).Kind.ShouldBe(FailureKind.Unprocessable);
    }

    [Fact]
    public async Task An_oversized_environment_value_is_refused_without_putting_it_in_the_message()
    {
        var key = "argv-limit-" + Guid.NewGuid().ToString("N");
        var secret = "sk-ant-" + new string('x', SandboxArgumentLimit.MaxStringBytes);
        var request = new SandboxLaunchRequest(new SandboxSpec { Command = "/bin/echo", Environment = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = secret } }, key);

        var refusal = await Should.ThrowAsync<NativeLaunchException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(request, CancellationToken.None));

        refusal.Message.ShouldContain("ANTHROPIC_API_KEY");
        refusal.Message.ShouldNotContain(secret);
        refusal.Message.ShouldNotContain("sk-ant-", Case.Sensitive);
    }
}
