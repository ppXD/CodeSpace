using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The PURE prlimit argv builder (<see cref="ProcessRlimits.Wrap"/>) + the operator cap-override resolution —
/// unit-testable on any OS. The REAL enforcement (caps reaching the agent, a runaway file truncated) is proven by
/// the Linux/prlimit E2E in <c>LocalProcessDurableRunnerTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProcessRlimitsTests
{
    [Fact]
    public void Wraps_the_command_in_prlimit_with_both_caps_then_a_terminator()
    {
        var (cmd, args) = ProcessRlimits.Wrap("prlimit", "/bin/sh", new[] { "-c", "echo hi" }, maxProcesses: 8192, maxFileMb: 64);

        cmd.ShouldBe("prlimit");
        args.ShouldBe(new[] { "--nproc=8192", "--fsize=67108864", "--", "/bin/sh", "-c", "echo hi" });   // 64 MiB → bytes
    }

    [Fact]
    public void Includes_only_the_positive_caps()
    {
        ProcessRlimits.Wrap("prlimit", "cmd", Array.Empty<string>(), maxProcesses: 0, maxFileMb: 64).Args
            .ShouldBe(new[] { "--fsize=67108864", "--", "cmd" });

        ProcessRlimits.Wrap("prlimit", "cmd", Array.Empty<string>(), maxProcesses: 512, maxFileMb: 0).Args
            .ShouldBe(new[] { "--nproc=512", "--", "cmd" });
    }

    [Fact]
    public void Leaves_the_command_unchanged_when_no_cap_is_requested()
    {
        var (cmd, args) = ProcessRlimits.Wrap("prlimit", "mycmd", new[] { "a", "b" }, maxProcesses: 0, maxFileMb: 0);

        cmd.ShouldBe("mycmd");
        args.ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public void Cap_override_env_var_names_are_pinned()
    {
        // Air-gapped operators tune the worker's fork-bomb / file caps via these — renaming breaks the pin (Rule 8).
        ProcessRlimits.MaxProcessesEnvVar.ShouldBe("CODESPACE_AGENT_MAX_PROCESSES");
        ProcessRlimits.MaxFileSizeMbEnvVar.ShouldBe("CODESPACE_AGENT_MAX_FILE_MB");
    }

    [Theory]
    [InlineData("512", 4096, 512)]   // operator env override wins
    [InlineData(null, 4096, 4096)]   // no env → the spec value
    [InlineData("bad", 4096, 4096)]  // unparseable env → the spec value
    public void EffectiveMaxProcesses_prefers_the_env_override_then_the_spec(string? env, int spec, int expected)
    {
        Environment.SetEnvironmentVariable(ProcessRlimits.MaxProcessesEnvVar, env);
        try { ProcessRlimits.EffectiveMaxProcesses(spec).ShouldBe(expected); }
        finally { Environment.SetEnvironmentVariable(ProcessRlimits.MaxProcessesEnvVar, null); }
    }

    [Fact]
    public void Available_is_null_off_linux() =>
        // No prlimit concept off Linux → callers apply no caps (the documented unconfined trust mode).
        (OperatingSystem.IsLinux() || ProcessRlimits.Available is null).ShouldBeTrue();
}
