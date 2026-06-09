using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The behavioral contract EVERY <see cref="ISandboxRunner"/> must satisfy, regardless of backend
/// (local process, Docker, K8s Job). A new runner gets full, identical coverage by subclassing this
/// and supplying the runner — turning "implements the interface" into "provably behaves like every
/// other runner" (Liskov). Drives a real execution environment via <see cref="ContractSpecs"/>.
/// </summary>
public abstract class SandboxRunnerContractTests
{
    protected abstract ISandboxRunner Runner { get; }

    [Fact]
    public async Task Success_reports_success_with_exit_zero_and_stdout()
    {
        var result = await Runner.RunAsync(ContractSpecs.Print("hello-contract"), CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.Success);
        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("hello-contract");
    }

    [Fact]
    public async Task Nonzero_exit_reports_failed_with_the_exit_code()
    {
        var result = await Runner.RunAsync(ContractSpecs.ExitWith(3), CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.Failed);
        result.ExitCode.ShouldBe(3);
    }

    [Fact]
    public async Task Stderr_is_captured()
    {
        var result = await Runner.RunAsync(ContractSpecs.PrintToStderr("err-contract"), CancellationToken.None);

        result.Stderr.ShouldContain("err-contract");
    }

    [Fact]
    public async Task Environment_is_passed_through()
    {
        var spec = ContractSpecs.PrintEnvVar("CONTRACT_VAR") with { Environment = new Dictionary<string, string> { ["CONTRACT_VAR"] = "contract-value" } };

        var result = await Runner.RunAsync(spec, CancellationToken.None);

        result.Stdout.ShouldContain("contract-value");
    }

    [Fact]
    public async Task Working_directory_is_honoured()
    {
        var leaf = "contract-" + Guid.NewGuid().ToString("N");
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), leaf));

        try
        {
            var spec = ContractSpecs.PrintWorkingDirectory() with { WorkingDirectory = dir.FullName };

            var result = await Runner.RunAsync(spec, CancellationToken.None);

            result.Stdout.ShouldContain(leaf);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Timeout_reports_timed_out_with_exit_minus_one()
    {
        var result = await Runner.RunAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 1 }, CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.TimedOut);
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => Runner.RunAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 }, cts.Token));
    }
}
