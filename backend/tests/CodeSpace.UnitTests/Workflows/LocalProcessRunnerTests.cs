using CodeSpace.Core.Services.Workflows.Sandbox;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// High-fidelity tests for the v0 runner: they drive a REAL OS process (no mock can stand in for
/// System.Diagnostics.Process) via the platform shell, so success/exit-code/stderr/env/working-dir/
/// timeout/cancellation are exercised against actual process behaviour. Cross-platform by shelling
/// through /bin/sh or cmd.exe.
/// </summary>
[Trait("Category", "Unit")]
public class LocalProcessRunnerTests
{
    private static readonly LocalProcessRunner Runner = new();

    private static SandboxSpec Sh(string script, int timeoutSeconds = 30, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null) => new()
    {
        Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        Args = OperatingSystem.IsWindows() ? new[] { "/c", script } : new[] { "-c", script },
        TimeoutSeconds = timeoutSeconds,
        WorkingDirectory = workingDirectory,
        Environment = environment ?? new Dictionary<string, string>(),
    };

    private static string EchoEnv(string name) => OperatingSystem.IsWindows() ? $"echo %{name}%" : $"echo ${name}";
    private static string PrintCwd() => OperatingSystem.IsWindows() ? "cd" : "pwd";
    private static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 >nul" : $"sleep {seconds}";

    [Fact]
    public void Kind_is_local() => Runner.Kind.ShouldBe("local");

    [Fact]
    public async Task Successful_command_succeeds_and_captures_stdout()
    {
        var result = await Runner.RunAsync(Sh("echo hello"), CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.Success);
        result.ExitCode.ShouldBe(0);
        result.Stdout.Trim().ShouldBe("hello");
    }

    [Fact]
    public async Task Nonzero_exit_reports_failed_with_the_exit_code()
    {
        var result = await Runner.RunAsync(Sh("exit 3"), CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.Failed);
        result.ExitCode.ShouldBe(3);
    }

    [Fact]
    public async Task Captures_stderr()
    {
        var result = await Runner.RunAsync(Sh("echo oops 1>&2"), CancellationToken.None);

        result.Stderr.ShouldContain("oops");
    }

    [Fact]
    public async Task Passes_environment_variables_through()
    {
        var spec = Sh(EchoEnv("CODESPACE_SBX_VAR"), environment: new Dictionary<string, string> { ["CODESPACE_SBX_VAR"] = "codespace" });

        var result = await Runner.RunAsync(spec, CancellationToken.None);

        result.Stdout.ShouldContain("codespace");
    }

    [Fact]
    public async Task Honours_the_working_directory()
    {
        var leaf = "codespace-sbx-" + Guid.NewGuid().ToString("N");
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), leaf));

        try
        {
            var result = await Runner.RunAsync(Sh(PrintCwd(), workingDirectory: dir.FullName), CancellationToken.None);

            result.Stdout.ShouldContain(leaf);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Times_out_a_long_running_command()
    {
        var result = await Runner.RunAsync(Sh(Sleep(10), timeoutSeconds: 1), CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.TimedOut);
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Caller_cancellation_throws_rather_than_returning_a_result()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => Runner.RunAsync(Sh(Sleep(10), timeoutSeconds: 30), cts.Token));
    }
}
