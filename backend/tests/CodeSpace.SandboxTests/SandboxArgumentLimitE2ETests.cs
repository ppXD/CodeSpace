using System.ComponentModel;
using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// Fidelity tier: 🟢 HIGH — a real <c>execve</c> on a real kernel, plus the real production
/// <see cref="LocalProcessRunner"/>. No mock, no mirror.
///
/// <para>This is the tier the argv bug needed and did not have. <see cref="SandboxArgumentLimit.MaxStringBytes"/> is
/// COMPUTED from <c>32 * PAGE_SIZE - 1</c>, and a computed limit has two ways to be wrong that no unit test can
/// falsify: too high and the kernel still refuses launches the guard waved through (the original bug, unchanged),
/// too low and the guard refuses launches this host can run. Only asking the kernel settles it. The whole existing
/// suite runs where the limit does not exist — macOS enforces no per-string cap at all, and the largest goal any
/// other test uses is 29 characters — which is exactly why a >128 KiB prompt reached production as a phantom
/// OOM kill.</para>
///
/// <para>Linux-gated (Rule 12.1) but deliberately NOT bubblewrap-gated: this asserts a kernel constant, which holds
/// with or without confinement. GUID-suffixed spool keys (12.2), cleanup of every staged artefact (12.3), paths via
/// the production resolver (12.7).</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class SandboxArgumentLimitE2ETests : IDisposable
{
    private const int E2Big = 7;

    private readonly List<string> _spools = [];

    [Fact]
    public void This_kernel_accepts_a_string_at_the_computed_limit_and_refuses_one_byte_more()
    {
        if (!OperatingSystem.IsLinux()) return;

        var limit = SandboxArgumentLimit.MaxStringBytes;

        Exec(new string('x', limit)).ShouldBeNull(
            customMessage: $"the guard would refuse launches this kernel can run: PAGE_SIZE={System.Environment.SystemPageSize}, computed limit={limit}. Reproduce by hand with: python3 -c \"import os; os.execv('/bin/true',['true','A'*{limit}])\"");

        Exec(new string('x', limit + 1)).ShouldBe(E2Big,
            customMessage: $"the guard would wave through launches this kernel refuses, which is the original defect: PAGE_SIZE={System.Environment.SystemPageSize}, computed limit={limit}. Reproduce by hand with: python3 -c \"import os; os.execv('/bin/true',['true','A'*{limit + 1}])\"");
    }

    [Fact]
    public void The_environment_block_obeys_the_same_per_string_ceiling_as_argv()
    {
        // copy_strings() walks envp with the same valid_arg_len() test, and an envp entry is copied as one
        // "NAME=VALUE" string — so measuring a value in isolation would pass a launch the kernel then refuses.
        if (!OperatingSystem.IsLinux()) return;

        var name = "CODESPACE_ARGV_LIMIT_PROBE";
        var justFits = new string('x', SandboxArgumentLimit.MaxStringBytes - name.Length - 1);

        Exec("small", (name, justFits)).ShouldBeNull(customMessage: $"NAME=VALUE is exactly {SandboxArgumentLimit.MaxStringBytes} bytes here and must be accepted");
        Exec("small", (name, justFits + "x")).ShouldBe(E2Big, customMessage: "one byte over the same ceiling, counted across NAME, '=' and VALUE");
    }

    [Fact]
    public async Task The_production_launch_path_refuses_an_oversized_goal_instead_of_letting_it_reach_the_kernel()
    {
        if (!OperatingSystem.IsLinux()) return;

        var key = Stage();
        var spec = new SandboxSpec { Command = "/bin/echo", Args = ["--goal", new string('x', SandboxArgumentLimit.MaxStringBytes + 1)], TimeoutSeconds = 10 };

        var refusal = await Should.ThrowAsync<NativeLaunchException>(() => new LocalProcessRunner().LaunchOrDiscoverAsync(new SandboxLaunchRequest(spec, key), CancellationToken.None));

        refusal.Reason.ShouldBe("argument-too-long");
        Directory.Exists(LocalProcessRunner.SpoolDirectoryFor(key)).ShouldBeFalse(
            customMessage: $"nothing may be staged for a launch that cannot happen — inspect {LocalProcessRunner.SpoolDirectoryFor(key)} by hand if this fails");
    }

    /// <summary>The errno <c>execve</c> set, or null when the child really started. Real fork+exec through the runtime, which reports a child's exec failure back as <see cref="Win32Exception"/>.</summary>
    private static int? Exec(string argument, (string Name, string Value)? environment = null)
    {
        var info = new ProcessStartInfo("/bin/true") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(argument);
        if (environment is { } entry) info.Environment[entry.Name] = entry.Value;

        try
        {
            using var process = Process.Start(info)!;
            process.WaitForExit(milliseconds: 10_000).ShouldBeTrue(customMessage: "/bin/true must exit immediately once it really started");
            return null;
        }
        catch (Win32Exception error) { return error.NativeErrorCode; }
    }

    private string Stage()
    {
        var key = "argv-limit-e2e-" + Guid.NewGuid().ToString("N");
        _spools.Add(LocalProcessRunner.SpoolDirectoryFor(key));
        return key;
    }

    public void Dispose()
    {
        // Best effort, and on the failure path too: a refused launch should stage nothing, and a spool that exists
        // here is itself the assertion failure — but it must not be left for the next run either way.
        foreach (var spool in _spools.Where(Directory.Exists))
            try { Directory.Delete(spool, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
